# Roadmap — the rest of the Onyx business

**Built:** Catalog, the multi-module plumbing, Customers, Promotions, Loyalty, Sales, Payments,
Notifications. **Next:** Reporting, admin and platform.

Progress lives in the status table at the bottom of this file. Each phase's own section carries
a **What actually happened** note where the build diverged from the plan — those divergences are
the most useful thing in here, because the plan being wrong in a specific way is more
informative than the plan being right.

The acceptance criteria are unchanged: the **→ BoomboCard** lines in Onyx's `ISSUES.md`. What is
closed, and the test that fails if it reopens, is tracked finding by finding in
[ISSUE-COVERAGE.md](ISSUE-COVERAGE.md) — including what is still open, listed rather than
omitted. What remains needs the reporting projections, a real scheduler and telemetry to exist.

Every phase ends with the same four things: a `CONCEPTS.md` section naming the issues it closes,
`BUGS-AND-LESSONS.md` entries for whatever bit during the build, rows in
[ISSUE-COVERAGE.md](ISSUE-COVERAGE.md) mapping each finding to **the test that fails if it
reopens**, and a README capability table that advertises nothing without an endpoint (**A32**,
**X11**).

---

## Phase 0 — Make the monolith actually multi-module · **DONE**

**The blocker.** `AddModulePersistence<TContext>` registered `IUnitOfWork`,
`IDomainEventDispatcher` and `IOutboxWriter` **unkeyed**. Called twice, the last registration
wins for every handler in the process: a Catalog handler would be handed
`EfUnitOfWork<SalesDbContext>`, call `SaveChangesAsync` on the wrong context, and write nothing
— while `UnitOfWorkBehavior` committed a transaction on a context with no changes. Silent data
loss, arriving the moment a second module is added.

The fix makes the mistake unrepresentable rather than documented:

- `IUnitOfWork<TScope>`, `IDomainEventDispatcher<TScope>`, `IOutboxWriter<TScope>`, where
  `TScope` is the module's own `DbContext` type. The untyped interfaces survive only as the base
  the pipeline behaviour is written against; **nothing is registered against them**, so a handler
  asking for the wrong module's unit of work cannot compile.
- One abstract `ModuleUnitOfWorkBehavior<TRequest, TResponse>` in Persistence, and one sealed
  subclass per module constrained to that module's request marker (`ICatalogRequest`,
  `ISalesRequest`, …). A command with no module marker would get no transaction — so a test
  asserts every `ITransactionalRequest` carries exactly one marker and every marker has a
  registered behaviour.
- A per-module **inbox** (`InboxMessage(EventId, Consumer)`, unique at the database).
  `OutboxDispatcher` retries the whole message when any one consumer throws, so a second
  consumer that already succeeded gets re-delivered. `IIntegrationEvent` already demands
  idempotent handlers; this is the mechanism rather than the hope.
- `SourceConventionTests` discovers projects by globbing `src/` instead of consulting a
  hardcoded five-element array — otherwise a new module silently escapes the `DateTime.Now`,
  `.Result`, `async void` and empty-catch gates. A stale array enforces as little as a stale
  document.
- Architecture tests move to `tests/BoomboCard.ArchitectureTests`, driven by a module list, so
  each new module inherits the layering rules instead of re-stating them.

Keyed services were the alternative and are worse: a forgotten
`[FromKeyedServices("catalog")]` resolves the wrong module with no error, which is the failure
mode being removed.

## Phase 1 — Customers · **DONE**

The dependency root: everything downstream needs an authenticated principal and a customer id.

`Customer` (`EmailAddress` VO, normalised and unique-indexed → **S1**, **D10**; owned addresses;
`CustomerStatus`), `PasswordCredential` (PBKDF2 via `PasswordHasher<T>`, per-user salt, re-hash on
login when the format moves → **S7**), `PasswordResetToken` and `EmailVerification` (crypto-random,
hash stored, single-use, expiring → **S14**), `AuthAttempt` (immutable, drives progressive
lockout → **S12**).

Identity without `IdentityDbContext`: the aggregate is ours, the hashing algorithm is theirs.

Decisions: entities are never model-bound and `Role` moves through a separately-authorised audited
command (**S3**); `FallbackPolicy = RequireAuthenticatedUser()` with explicit opt-out (**S2**,
**S16**, **X14**); ownership is a first-class authorization check (**S4**); JWT validates issuer,
audience, lifetime and key, from User Secrets (**S15**, **S5**); no entity reaches the wire
(**S11**, **A24**); the customer list is a server-side projection, keyset-paged and capped
(**A26**–**A28**); anonymisation instead of hard delete (**D13**); PII hashed in logs (**A42**);
rate limiting on auth (**S12**).

**`CreditLimit` is deleted.** Onyx carried the column and never read it (**M13**). Credit terms are
a real capability or they are not, and a field with no check is the speculative seam **A45** is
about. Recorded here so the omission is a decision rather than an oversight.

## Phase 2 — Promotions · **DONE**

Before Sales, because the quote consumes it and building it after means writing the pricing
pipeline twice.

`Promotion` (code VO, `DiscountKind`, UTC validity window with explicit boundaries → **M17**,
minimum spend, total and per-customer caps, scope, stacking rule, status) and
`PromotionRedemption` — an immutable ledger whose unique indexes enforce both caps **at the
database**. One definition, read and never duplicated (**M14**). Promotions are data with campaign
management (**M16**). Applying a code is a command that fails with a reason (**M15**).

## Phase 3 — Loyalty · **DONE**

`PointsLedgerEntry`, immutable, typed `Earned | Redeemed | Returned | Expired | Reversed |
Adjusted | Forfeited`, each carrying its source. Points have a stated monetary value, defined
once like `Country`'s tax rate — 100 points to one unit of currency, chosen so the conversion can
never round. Redemption is validated during the quote and debited in the transaction that
confirms the session (**D4**). Reversal on refund; expiry writes rows rather than deleting
history.

**One change from this plan, made while building it.** The line above originally read "the
balance is a `SUM`, never a column". That instinct was aimed at Onyx's counter-with-no-log and is
half right — but a `SUM` cannot be the subject of a concurrency token, so two simultaneous
redemptions of the last 500 points both compute the same `SUM` and both commit. The balance is
therefore state on the account row under `AggregateRoot.Version`, with the ledger as the
immutable log and `PointsIntegrityCheckJob` asserting `SUM(Delta) == Balance` — the same
arrangement Catalog uses for `Product.Stock` against `StockMovement`. What makes the counter
trustworthy is not that it is the source of truth; it is that something proves it matches the
log. See `CONCEPTS.md` §23.

**Deferred to Phase 4, deliberately — and now closed:** earning fires from Sales'
`OrderPaidIntegrationEvent`, and Sales did not exist, so a module could not subscribe to it yet.
`GrantPointsCommand` was published and tested then — an administrator granting goodwill points
uses the same path — on the stated expectation that Phase 4 would add a thin inbox-guarded
handler calling it, and that the handler would be the only new code.

That is exactly what it took. `OnOrderPaidGrantPoints` is one handler and no new rule, and
`OnOrderRefundedReverseEarnedPoints` closes the reversal half of **M12** the same way. Verified
end to end: an `OrderPaid` message queued in Sales' outbox credits 80 points for 80.00 of
qualifying spend, writes an `Earned:80` ledger row, and credits once when redelivered.

## Phase 4 — Sales · **DONE**

`CheckoutSession` is the quote and the anchor that commits first. It captures the `PriceId`
alongside the unit price, so the price shown is provably the price charged (**A6**).

The pricing pipeline, as an explicit ordered basis, each step emitting a typed persisted
`QuoteLine` so the total is reconstructible from rows:

1. line extended = unit × qty, rounded per line (**M6**, **M7**)
2. `MerchandiseSubtotal`
3. promotion discount
4. loyalty redemption, as a priced discount line (**M11**)
5. shipping, threshold evaluated against `MerchandiseSubtotal` **pre-discount** (**M8**), one
   named constant and one comparison (**M9**)
6. tax on discounted merchandise **plus shipping** (**M10**)
7. total

No money value ever arrives inbound (**M2**). `Order` is created only from a validated, priced,
confirmed session (**M19**, **D2**), with `OrderItem` as a real entity (**D15**), a persisted
gap-tolerant `OrderNumber` (**D8**), and a guarded state machine past `Paid` (**A31**, **A40**).

The cross-context flow and its four recovery layers are already designed in the README; this phase
builds them, including the orphan-sale reconciliation the README deliberately left open.

### What actually happened

All of the above landed. Four things are worth recording because the plan did not anticipate them.

**The plan said each step emits a persisted `QuoteLine`.** It emits a persisted `QuoteAdjustment`
instead — same intent, better name, and it carries the **basis** each figure was computed from as
well as the figure itself. A persisted amount makes the total reconstructible; a persisted basis
makes it *explicable*, which is what **M19** actually asks for.

**Stating M8's property correctly was most of the work on M8.** The obligation line asks for "a
monotonicity property test over the total", and the obvious reading — *the total never falls as
the basket grows* — is **false by design**: at the free-shipping threshold the total drops by the
delivery charge, because that is what a free-delivery offer is. The first version of the test
asserted it and failed on correct code. The property that distinguishes the fix from the bug is
piecewise: within a shipping band the total never falls, the drop happens in exactly one place,
and that place does not move with the discount. All three are in the suite, exhaustive over 0–500
in 1-cent steps. See `CONCEPTS.md` §24.

**`PaymentReference` became `OrderPayment`.** It needs a unique index to be the idempotency
guarantee rather than a hopeful read, and a nullable unique column does not work — SQL Server
treats two `NULL`s as equal, so exactly one unpaid order could exist. A filtered index would be
provider-specific SQL in a provider-agnostic module. The capture is therefore a row that either
exists or does not. Third time this project has reached for that move, after
`Customer.DefaultAddressId` and `CustomerOrdinal`.

**The end-to-end run found a release that invented inventory.** A handler written the same
afternoon restored a basket's lines for a session that had never held anything — Onyx **D5** by a
different road. Fixed in both modules: Sales records `HoldsPlacedAtUtc` and does not ask, and
`RestoreStockCommandHandler` caps every release at `SUM(Delta)` for that reference and so cannot
comply. Neither idempotency nor the integrity check could have caught it, for reasons worth
reading: `BUGS-AND-LESSONS.md` §13.

**Deliverables.** 75 Sales tests and 7 new Catalog regression tests (393 in total, up from 294),
17 new architecture-test cases from one registry line, `CONCEPTS.md` §24, `BUGS-AND-LESSONS.md`
§12–13, `ISSUE-COVERAGE.md` rows for M1–M5, M8, M9, M10, M12, M19, D2, D5 and D8, and a 57-check
live verification of the whole flow including the expiry sweeper releasing a staged crash state.

## Phase 5 — Payments · **DONE**

`Payment` transitions on a provider response and on nothing else (**M1**), with a constrained
persisted status (**A39**) and a `ProviderReference` that is nullable and never fabricated
(**M4**). `PaymentAttempt` persists every provider interaction (**A17**). A stable idempotency key
per intent (**M5**). The amount is derived server-side (**M2**).

No transaction across the HTTP call (**A12**): commit the intent, call the provider, record the
outcome. Typed `HttpClient` with a pooled handler (**A14**), Polly, options validated at startup
(**A15**), credential attached by a delegating handler (**S17**), and **no certificate callback,
ever** (**S8**) — banned by a source-convention test. Specific exceptions, logged with a
correlation id (**A16**). Webhooks, reconciliation, declines, refunds, partial refunds,
chargebacks. Confirmation references from `RandomNumberGenerator` (**S13**).

### What actually happened

All of the above landed. Five things are worth recording.

**The plan listed nine fixes; they turned out to be one insight and eight consequences.** Onyx's
gateway result was `class PaymentGatewayResult { bool Success; string GatewayReference; }`, and
that single boolean is the root cause of both **M1** and **M5**. A two-state answer cannot express
*"we do not know"*, so the code had to guess — and it guessed differently in two places ten lines
apart: the controller read a lost response as success, the retry loop read the same lost response
as failure and charged again. So `AttemptOutcome` has four cases, `Indeterminate` is the one that
earns its keep, and `IdempotencyKey` / `EnquireAsync` / `IndeterminateSinceUtc` /
`payments.reconciliation` exist for no purpose other than making an unanswered charge
**resolvable by asking** rather than by assuming.

**M1 and M4 became one type instead of two rules.** `ProviderReference` has a single factory that
rejects null and blank, no `New()`, and no implicit conversion — so a caller holding no provider
answer has nothing to pass to `Capture`, and `result?.GatewayReference ?? confirmationCode` does
not compile. The rule was already known: the register's obligation line states it, and Onyx's
author would have agreed with it. The difference is whether the agreement is load-bearing.

**`Payment.Status` could not carry the M3 constraint, so a row does.** "At most one payment per
order" is wrong — a declined card must be followed by another attempt. The real constraint is
"at most one payment per order *able to take money*", which is a predicate over the status, and a
filtered unique index would be provider-specific SQL in a provider-agnostic module. So
`PaymentClaim`'s primary key is the order id. **Third time this project has made that move**,
after `Customer.DefaultAddressId` and `OrderPayment`, which is enough to state as a rule: *when a
conditional uniqueness constraint is needed, make the condition the existence of a row.*

**One job in this phase corrects, and that needed justifying.** Every other integrity job here
reports and never corrects, because Onyx **D5** is a job that healed discrepancies and hid the
defect causing them. `payments.reconciliation` writes. The distinction: D5 *computed* a figure it
had no authority over; reconciliation *retrieves* the answer from the system of record, which for
"did this card get charged?" is the provider and never us. A job that resolves an unknown by asking
the owner is the opposite of one that guesses.

**The smoke run found a bug the unit tests could not.** A submit returned `status: "Pending"` for
a payment that was `Captured` — the write was correct, and the handler re-read the aggregate
through a context that had been tracking it since before the provider call, so EF's identity map
handed back the stale instance. Six of the seven smoke-run failures were that one defect. Fixed by
having the command that changes the state return it, rather than by defeating the cache:
`BUGS-AND-LESSONS.md` §14.

### Tests: unit only, by request

**96 unit tests**, covering the state machine's every transition, the four-way outcome mapping,
the refund arithmetic and the three reference types — which carry **M1**, **M4**, **M5** and
**A39** between them. Plus **two new source-convention tests** that apply to the whole solution:
the **S8** certificate-callback ban, and a ban on assigning a status from a string literal
(**M1**/**A39**).

What that leaves uncovered is recorded rather than implied: three guarantees are database
constraints asserted by configuration, by the generated migration and by the 49-check live smoke
run, but **not** by a test that inserts a conflicting row — `PK_PaymentClaims` (**M3**),
`UX_Payments_IdempotencyKey` and `UX_Refunds_Idempotency`. Bug 14 is the defect that decision let
through, and it is worth noting that it was a wiring bug rather than a logic bug: exactly the
class an integration test exists to catch.

**Deliverables.** 96 Payments tests and 19 new architecture-test cases (508 in total, up from
393), `CONCEPTS.md` §25, `BUGS-AND-LESSONS.md` §14, `ISSUE-COVERAGE.md` rows closing **S8**,
**S17**, **M1**, **M3**, **M4**, **M5**, **A12**, **A14**, **A15**, **A16**, **A17** and **A39**,
and a 49-check live run covering the decline path, the capture chain across three modules, the
indeterminate-then-reconciled path, partial refunds and webhook rejection.

## Phase 6 — Notifications · **DONE**

`NotificationRequest` with a delivery log, backoff and dead-letter — the outbox shape, because the
problem is the outbox problem. A provider abstraction with a logging sender in development, so the
capability is reachable and tested (**A8**, **A32**) rather than "wired up separately per
environment". Always post-commit (**A9**).

### What actually happened

All of the above landed, across eleven consumers and four modules. Five things are worth
recording.

**The finding has two sentences, and the second one shaped the module.** `A8` reads: *"No customer
has ever received any communication from this system, **and nothing records that**. There is no
retry, no dead-letter, no delivery record."* The missing implementation is the obvious defect and a
day's work. The missing *record* is why it survived long enough to be written down in an audit — a
system with a notifications table showing ten thousand rows and zero deliveries is one somebody
fixes in an afternoon, because somebody looks. So the status machine, the attempt log, the
published dead-letter, the health endpoint and the integrity check all exist for the second
sentence.

**"Always post-commit" turned out to be slightly the wrong phrasing**, and separating it was the
most useful thing this phase clarified. The *send* is post-commit — a separate job outside every
transaction. The *decision to send* is in-transaction: the notification row is written alongside
the inbox claim, so a notification exists if and only if the event that caused it was consumed.
Which makes it exactly as durable as the fact it describes, rather than merely later than it.

**It deliberately does NOT copy Phase 5's machinery for an unanswered send.** Payments has
`Indeterminate`, an idempotency key, an enquiry endpoint and a reconciliation job. A charge retried
after a timeout bills a customer twice; an email retried after a timeout arrives twice. So there
are three outcomes here rather than four, and at-least-once delivery is accepted. The instinct to
reuse the previous phase's shape wholesale would have been wrong, and stating why is the point:
**build the guarantee the cost of being wrong justifies.**

**Choosing the idempotency reference per consumer was the real work.** Uniqueness on
`(Kind, Reference)` is obvious; which reference is not. Four of the eleven would be bugs with the
first answer that comes to mind — verification and reset key on the **token** (asking twice is
normal, and each request is a different link, so keying on the customer swallows every later one);
shipment and refund key on the **event id** (both recur per order, and a second parcel deserves a
second message). The pattern: *key on the thing that is unique per message, not per subject.*

**One template decision has a security consequence.** Rendering at enqueue rather than at send is
what makes a retry byte-identical to the first attempt — but it means a verification or reset body
sits in a table holding a plaintext token, because the email is the only thing that can deliver
one. So `ContainsSecret` travels on the row, the logging sender redacts it, no query projects it,
and `notifications.maintenance` discards it an hour after delivery while keeping the subject. Onyx
**S14** stored reset tokens in plaintext with no expiry at all.

### What this phase added elsewhere

`OrderShippedEvent` and `OrderDeliveredEvent` had existed as **domain** events since Phase 4 with
nothing publishing them, because nothing outside Sales had asked. **A31** is that Onyx's lifecycle
"stops at `Paid`" and nothing ever set `Shipped`; BoomboCard could set it and could not tell
anybody. Sales gained two integration events, two translators, and a `UnitsShipped` count — "3
items have shipped" is a better message than "your order has shipped", and a partial shipment is
confusing without it.

### Nothing in this phase went wrong

**`BUGS-AND-LESSONS.md` gets no entry**, which is worth recording rather than passing over. The two
defects this module would most plausibly have had were both avoided by having met them already:

- `DispatchNotificationCommand` has exactly the shape that produced §14's stale read — read, call a
  provider in a new scope, report the result — and returns the state from the command that changed
  it. Written that way from the start because Phase 5 had just paid for the lesson.
- `NextAttemptAtUtc` is set at creation, because Phase 4's verification script hand-built an outbox
  row without one and spent a cycle wondering why nothing was delivered.

What did bite was the test suite, twice, and both times the same mistake: asserting on async work
before it had finished, and asserting a global ordering against a per-class shared database. The
second is §6 in a new costume. Both are fixed with a comment saying so.

**Deliverables.** 77 Notifications tests (unit plus SQL Server, because the idempotency index is
the module's central guarantee and the InMemory provider has no unique indexes), 17 new
architecture-test cases from one registry line, `CONCEPTS.md` §26, `ISSUE-COVERAGE.md` rows closing
**A8**, **A9**, **A31** and **A32** and extending **S14**, **M11**/**M12** and **A16**, and a
53-check live run covering the verification email, the order → payment → shipment chain, partial
shipment as two messages, a decline with its advice, a replayed event producing no duplicate, and
the rendered secret being discarded after its window. 602 tests in total, up from 508.

## Phase 7 — Reporting, admin and platform

A `DailySalesSummary` projection maintained from events (**A19**), with the empty case guarded
(**A18**). A real scheduler with overlap prevention and leader election (**A20**, **A21**). Health
checks over the existing `JobRun` history (**A7**). An audit log built from persisted domain events
(**A40**). OpenTelemetry, and no counter columns (**D9**). `Idempotency-Key` on writes, API
versioning, and `Retry-After`/`ETag` on the poll endpoints (`CONCEPTS.md` §17).

---

## Order, and why

| Phase | Depends on | Status |
|---|---|---|
| 0 · multi-module plumbing | — | **done** — see `CONCEPTS.md` §20, `BUGS-AND-LESSONS.md` §7–8 |
| 1 · Customers | 0 | **done** — see `CONCEPTS.md` §21, `BUGS-AND-LESSONS.md` §9 |
| 2 · Promotions | 0 | **done** — see `CONCEPTS.md` §22, `BUGS-AND-LESSONS.md` §10–11 |
| 3 · Loyalty | 0 | **done** — see `CONCEPTS.md` §23. One plan change: the balance is a column under a concurrency token, not a pure `SUM` |
| 4 · Sales | 1, 2, 3 | **done** — see `CONCEPTS.md` §24, `BUGS-AND-LESSONS.md` §12–13. Two plan changes: the pricing step emits a `QuoteAdjustment` carrying its basis rather than a `QuoteLine`, and the payment reference is a row rather than a nullable unique column |
| 5 · Payments | 4 | **done** — see `CONCEPTS.md` §25, `BUGS-AND-LESSONS.md` §14. Unit tests only, by request; the three database constraints it leaves unasserted are listed in `ISSUE-COVERAGE.md`. One plan change: the M3 constraint is a claim row keyed on the order id, because the real predicate is over the status |
| 6 · Notifications | 5 | **done** — see `CONCEPTS.md` §26. No `BUGS-AND-LESSONS` entry: nothing in the module went wrong, and the two defects it would most plausibly have had were avoided by having met them in Phases 4 and 5. One plan clarification: the *send* is post-commit, the *decision to send* is in-transaction |
| 7 · Reporting and platform | 4, 5 | next |

Promotions and Loyalty come before Sales deliberately. Each is one aggregate and one ledger, and
the quote's discount steps consume both — building them afterwards means designing the pricing
pipeline twice.
