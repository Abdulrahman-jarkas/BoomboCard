# Seeing the event flow

One business operation produces **one trace**, from the HTTP request through every domain event,
every outbox handoff and every module that reacts — and it stays one trace even though the
reacting work happens later, on another thread, in another DI scope, driven by a timer rather than
by the request.

Companion to [CONCEPTS.md](CONCEPTS.md).

---

## Run it

```bash
docker compose -f docker-compose.observability.yml up -d
dotnet run --project src/BoomboCard.Api
```

Open <http://localhost:18888>, pick **Traces**, and do something — for example register a customer:

```bash
curl -X POST http://localhost:5120/api/auth/register \
  -H "Content-Type: application/json" \
  -d '{"email":"someone@example.com","name":"Someone","password":"a-long-enough-password-123"}'
```

Within a few seconds that one request appears as one trace containing this (a real capture, span
names unedited):

```
POST /api/auth/register
  └─ command RegisterCustomerCommand
      ├─ domain event CustomerRegisteredEvent
      │   └─ outbox publish CustomerRegisteredIntegrationEvent      <- Customers → Loyalty
      │       └─ domain event PointsAccountOpenedEvent
      └─ outbox publish EmailVerificationRequestedIntegrationEvent  <- Customers → Notifications
          └─ command EnqueueNotificationCommand
              └─ domain event NotificationQueuedEvent
```

Three modules, two asynchronous handoffs, one trace. The two `outbox publish` spans did not run
during the request at all — the request had returned `201` and its span had ended well before the
dispatcher picked those rows up on a later poll.

Nothing is listening on 4317? The application runs exactly as before. See *Switching it off*.

---

## The problem this solves

An outbox deliberately breaks the call stack. A command writes a row and returns; some seconds
later a `PeriodicTimer` tick in `OutboxDispatcher` picks it up with **no ambient `Activity`** —
different thread, different scope, different DI container scope.

A span's parent normally comes from `Activity.Current`, which is ambient on the async call stack.
So the default outcome is not a broken trace that looks broken. It is worse: two healthy-looking
traces that are each a fraction of the story. `POST /api/auth/register` appears to do nothing but
write a row, and `publish CustomerRegisteredIntegrationEvent` appears out of nowhere with no
reason for existing. The question anyone actually has — *what did this registration set in
motion?* — has no answer, and nothing anywhere reports an error.

## How it is solved

The trace context travels **with the message**, in the database, in the same row and the same
transaction as the message itself.

| Stage | What happens | Where |
|---|---|---|
| Enqueue | `Activity.Current` is serialised to a W3C `traceparent` and stored on the outbox row | `OutboxWriter` |
| Commit | The trace context commits atomically with the message and the aggregate change | one `SaveChanges` |
| Dispatch | The stored `traceparent` is parsed back and used as the **parent** of the publish span | `OutboxDispatcher` |
| React | Consumers, their inbox claims, their commands and their own outbox rows inherit it | ambient `Activity` |

That last row is what makes the chain unbounded: a consumer that queues an event of its own
captures the context it is currently running in, so hop three joins the same trace as hop one, and
so on for as long as the cascade runs.

Two details that are load-bearing rather than incidental:

- **W3C format, not an invented one.** It is what the ASP.NET Core and `HttpClient`
  instrumentation in this host already speak, and what every OTLP backend understands. A bespoke
  pair of columns would have needed a bespoke propagator at every edge.
- **Nothing assumes one process.** Today the handoff is across a thread, a scope and a clock tick.
  If a module moves out to its own service, the context travels in a column it already had and the
  trace does not change shape.

See `TraceContext` for the full reasoning, including why a malformed `traceparent` must never fail
a delivery.

---

## What is instrumented

| Span | Kind | Emitted by |
|---|---|---|
| `POST /api/...` | Server | ASP.NET Core instrumentation |
| `command X` / `query X` | Internal | `TracingBehavior` (every MediatR request) |
| `domain event X` | Internal | `DomainEventDispatcher`, one per event, nested by cascade depth |
| `outbox publish X` | Consumer | `OutboxDispatcher`, parented to the originating trace |
| `outbox pass <Module>` | Internal | `OutboxDispatcher`, one per non-empty batch |
| `job <name>` | Internal | `JobRunner`, the root of a job-initiated flow |
| outgoing HTTP | Client | `HttpClient` instrumentation (payment gateway, notification sender) |

Span **events**, which sit on a timeline rather than taking a row of their own:

- `outbox.enqueued` on the command's span — did this command queue what it was supposed to?
- `inbox.claimed` / `inbox.duplicate` on the publish span, per consumer.

That last pair matters more than it looks. A skipped duplicate is otherwise **indistinguishable
from a successful handler**: both return normally, both commit, both mark the message published,
and the consumer deliberately logs nothing because at-least-once delivery makes a redelivery
routine. So "the points never arrived" and "the points arrived once and the second delivery
correctly did nothing" look identical in the logs — and they are exactly the two answers you need
to tell apart. `boombocard.inbox.duplicate` is the attribute that separates them.

### Attributes worth filtering on

- `boombocard.module` — `Catalog`, `Sales`, `Loyalty`, … Derived from the DbContext name.
- `boombocard.message.type` — the event, command or query type name.
- `boombocard.event.kind` — `domain` or `integration`.
- `boombocard.inbox.duplicate` — see above.
- `boombocard.outbox.attempt`, `boombocard.outbox.status` — which retry this was, and where the
  message ended up.
- `boombocard.job.name`, `boombocard.job.items_processed` / `_failed`.

No request payloads are ever attached to a span. Commands here carry names, addresses, webhook
bodies and tokens, and a trace store is a copy of production data with different retention and a
wider audience — the shape of Onyx S10. The type name and the module are enough to read a
waterfall.

---

## Things that are deliberately *not* one trace

**A poller's pass.** `outbox pass <Module>` is a **root** with **links** to the flows it served,
not a parent of them. Its cause is a clock, not a request. Parenting it to one of the messages it
happened to be carrying would claim that one checkout caused that pass, and would drop the batch
size and failure count of unrelated work into that checkout's trace. Links express the real
relationship: relevant, not causal.

**A background job.** `job <name>` is also a root, because not every flow starts with somebody
calling an endpoint. The expiry sweeper releases stock holds, the reconciliation jobs correct
state, and each mutates aggregates, raises domain events and writes outbox rows — setting off the
same cross-module cascade a checkout does. So "what did last night's sweeper set in motion?" is
one trace, on equal footing with a request.

**An empty poll.** No span at all. At a two-second interval across seven modules that would be
roughly 300,000 spans a day saying nothing happened — which is how a trace store becomes too
expensive to keep and too noisy to read.

---

## Failures are visible, not just handled

Three places in this codebase catch an exception and deliberately keep going: the outbox's
per-message handler (so one bad message cannot block a batch), the outbox's per-pass boundary (so
one bad pass cannot stop the loop), and `JobRunner` (so one bad job cannot take down the host).
Each is correct, and each has the same cost — the failure is invisible unless something records
it. So each also marks its span `Error` and attaches the exception.

A failing message therefore shows up as a red `outbox publish` span inside the trace of the
command that queued it, carrying its attempt number and whether it has been dead-lettered, next to
everything else that command set off.

---

## Configuration

`Observability` in `appsettings`:

| Setting | Default | Notes |
|---|---|---|
| `Enabled` | `true` | `false` registers no exporter and creates no activities at all |
| `OtlpEndpoint` | `http://localhost:4317` | gRPC. Jaeger, Tempo and the OTel Collector all listen here too |
| `ServiceName` | `boombocard-api` | One name for the host; modules are an attribute, not a service |
| `SampleRatio` | `1.0` | Applied **parent-based** — see below |
| `ExportLogs` / `ExportMetrics` | `true` | Logs carry trace and span ids, so a line and its span find each other |

`appsettings.json` deliberately carries **no** `OtlpEndpoint`: a committed default would point
every deployment at `localhost` and silently export nothing. Supply it from the environment.

**Sampling is always parent-based**, whatever the ratio. A fresh per-span decision at the outbox
would discard the downstream half of flows whose first half was kept, and a truncated trace looks
exactly like a bug in the outbox. With the default ratio of `1.0` the wrapper is a no-op; it is
written that way so turning sampling down later cannot quietly start tearing flows in half.

### Switching it off

`Observability:Enabled = false` — or simply nothing listening on the endpoint — and the
application behaves identically. `ActivitySource.StartActivity` returns `null` when nothing is
subscribed, so no activities are created anywhere, outbox rows are written with null trace columns,
and messages are delivered exactly as before.

That is a design rule, not a side effect: **telemetry is never allowed to be the reason something
does not work.** It is also why a missing `OtlpEndpoint` does not stop the host, while a missing
`Jwt:Key` does. An application with no signing key cannot serve a request; one with no dashboard
can serve every request it was asked to.

---

## Where the dependencies live

The modules and the SharedKernel reference **no** OpenTelemetry package. They emit spans through
`System.Diagnostics.ActivitySource`, which ships in the .NET shared framework — so
`BoomboCard.SharedKernel` stays "deliberately free of EF Core, ASP.NET Core and any other
infrastructure package" (asserted by `SharedKernel_DoesNotDependOnInfrastructure`) while being
fully instrumented.

The SDK, the exporter and the instrumentation live in the host only, next to the EF provider and
the JWT packages, for the same reason: the endpoint, the sampling ratio and the credential are
deployment concerns.

**`OpenTelemetry.Instrumentation.EntityFrameworkCore` is deliberately absent.** It has only ever
shipped as `-beta`, and this build treats dependency hygiene as a gate rather than a preference
(`NuGetAuditMode=all`, audit level `low`, `NU1903`/`NU1904` as errors). Taking a prerelease
dependency to get SQL spans would be the first exception to that, and it would buy timing detail
the event flow does not depend on. When it goes stable: add the package and one
`AddEntityFrameworkCoreInstrumentation()` line in `ObservabilityComposition`.

---

## Tests

`tests/BoomboCard.Observability.Tests` — 23 tests, against real SQL Server, covering the claim
rather than the plumbing:

- A captured `traceparent` is well-formed, and the `Recorded` flag survives the round trip.
- A span built from a restored context continues the original trace **after that span has ended**,
  with no ambient activity.
- Enqueuing inside a command stores that command's trace on the row, read back through a real
  `nvarchar(55)` column by a second DbContext.
- **The real `OutboxDispatcher`, started as a hosted service on a real timer, publishes inside the
  trace that enqueued the message** — the end-to-end claim.
- An event queued *by a consumer* stays in the same flow, two hops and two database columns later.
- A message with no stored context is still published; a malformed one does not throw.
- A failed message leaves a span marked `Error`, and the pass span reports the failure count.

The dispatcher tests drive the real background service rather than reimplementing its loop,
because a test that called a private method directly would keep the caller's ambient context and
pass whether propagation worked at all.
