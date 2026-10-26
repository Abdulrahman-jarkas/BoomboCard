# BoomboCard

Rebuild of the Onyx Orders domain as a modular monolith on DDD and event-driven lines.
The issue register driving it is `ISSUES.md` in the Onyx repo — 101 findings, each with a
**→ BoomboCard** obligation line. Those lines are the acceptance criteria.

**Built:** Catalog · Customers · Promotions · Loyalty. **Next:** Sales, Payments,
Notifications, Reporting — see **[docs/ROADMAP.md](docs/ROADMAP.md)**.

```
dotnet build
dotnet test                      # 294 tests
cd src/BoomboCard.Api && dotnet run
```

**Swagger UI: <http://localhost:5120/swagger>** (`/` redirects there) ·
OpenAPI document at `/openapi/v1.json` · `/health`

**SQL Server**, via LocalDB (`(localdb)\MSSQLLocalDB`) — no Docker, no setup on Windows.
Migrations are applied on first run in Development.

**Watch the event flow:**

```
docker compose -f docker-compose.observability.yml up -d   # OTLP dashboard on :18888
```

One business operation is **one trace**, across every asynchronous handoff in it. Register a
customer and the trace spans three modules and two background dispatcher ticks:

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

Those two `outbox publish` spans did not run during the request — it had already returned `201`.
The trace survives because the W3C `traceparent` is stored on the outbox row and committed in the
same transaction as the message, then restored as the publish span's parent. Without that, the
default is not a visibly broken trace but something worse: two healthy-looking traces that each
tell a fraction of the story, with nothing reporting an error.
**[docs/OBSERVABILITY.md](docs/OBSERVABILITY.md)**

## Projects

```
src/
  BoomboCard.SharedKernel              Entity, AggregateRoot, ValueObject, Money, Country,
                                       event + unit-of-work abstractions.
                                       NO EF Core, NO ASP.NET Core.
  BoomboCard.SharedKernel.Persistence  EF-dependent plumbing: unit of work, domain event
                                       dispatcher, transaction behaviour, versioning
                                       interceptor, outbox, inbox. Every per-module service is
                                       registered as `IService<TDbContext>` — see below.
  BoomboCard.Catalog                   The Catalog bounded context (internal).
  BoomboCard.Catalog.Contracts         Catalog's public surface. Other modules reference
                                       THIS, never BoomboCard.Catalog.
  BoomboCard.Catalog.Migrations.
    SqlServer                          Provider-specific migrations. See below.
  BoomboCard.Customers                 Identity, credentials, addresses, lifecycle.
  BoomboCard.Customers.Contracts       Plus the authorization vocabulary every module names
                                       and the IAccessTokenIssuer seam the host fills.
  BoomboCard.Customers.Migrations.
    SqlServer
  BoomboCard.Promotions                Discount campaigns: codes, windows, caps, scope,
                                       stacking, redemption ledger, performance reporting.
  BoomboCard.Promotions.Contracts      Validate / reserve / release, plus integration events.
  BoomboCard.Promotions.Migrations.
    SqlServer
  BoomboCard.Loyalty                   A points ledger: earn, redeem, expire, reverse, with the
                                       balance asserted against the log.
  BoomboCard.Loyalty.Contracts         Balance / validate / redeem / release / grant / reverse.
  BoomboCard.Loyalty.Migrations.
    SqlServer
  BoomboCard.Sales                     Checkout and orders: the seven-step pricing pipeline, the
                                       three-transaction sale, and all four recovery layers.
  BoomboCard.Sales.Contracts           Mark paid / cancel / refund, the order summary, the
                                       checkout-outcome query recovery layer 3 needs, and the
                                       integration events the rest of the system was waiting for.
  BoomboCard.Sales.Migrations.
    SqlServer
  BoomboCard.Payments                  Payment intents, provider interactions, refunds,
                                       chargebacks and reconciliation. NO HTTP package - the
                                       gateway is an interface the host implements.
  BoomboCard.Payments.Contracts        Payment status, the order-payment outcome, and the
                                       capture / decline / failure / refund / chargeback events.
  BoomboCard.Payments.Migrations.
    SqlServer
  BoomboCard.Notifications             Every message this system owes a customer, with a retry
                                       budget, a dead-letter and a row per delivery attempt. NO
                                       HTTP or SMTP package - the sender is an interface the
                                       host implements.
  BoomboCard.Notifications.Contracts   The notification summary, the queue health, and the one
                                       event worth publishing: a message that will not arrive.
  BoomboCard.Notifications.Migrations.
    SqlServer
  BoomboCard.Api                       The host. Owns the provider, the authentication
                                       scheme, the signing key and the rate limits;
                                       composes modules.
tests/
  BoomboCard.ArchitectureTests         Rules that apply to EVERY module, driven by a registry,
                                       so a new module inherits them rather than re-stating
                                       them. Layering, encapsulation, event shape, DI wiring,
                                       and the source-text conventions.
  BoomboCard.Catalog.Tests             Catalog's own domain, model and concurrency tests.
                                       SQL Server LocalDB, not the InMemory provider.
  BoomboCard.Customers.Tests           The aggregate's invariants, the credential rules, what
                                       the database guarantees, and the ownership check.
  BoomboCard.Promotions.Tests          Campaign rules, the discount arithmetic, and both usage
                                       caps proven under real concurrency.
  BoomboCard.Loyalty.Tests             The points rules, the FIFO expiry arithmetic, and the
                                       ledger-sums-to-balance invariant.
  BoomboCard.Sales.Tests               The pricing pipeline (exhaustive property tests over the
                                       total), the order state machine, and the four unique
                                       indexes the sale's idempotency rests on.
  BoomboCard.Payments.Tests            The payment state machine, the four-way provider outcome,
                                       the refund arithmetic and the three reference types.
                                       UNIT ONLY - see the Payments section for what that leaves
                                       uncovered.
  BoomboCard.Notifications.Tests       Every customer-facing message asserted as text, the
                                       retry schedule, the secret-body rules, and the
                                       idempotency index that stops a replayed event sending a
                                       second email.
```

`SharedKernel` is split in two on purpose. Onyx's `ARCHITECTURE.md` claimed its domain had
"no EF Core, no ASP.NET Core, no knowledge of HTTP or SQL" while `Onyx.Domain.csproj`
referenced EF Core and its pricing service took an `IQueryable` (**A11**). Here that claim is
structural rather than aspirational — and `ArchitectureTests` asserts it, so it stays true.

---

## Domain events

Dispatch is **save → publish → re-save → repeat → commit**, all inside one transaction owned
by each module's `ModuleUnitOfWorkBehavior`.

```
Command (ITransactionalRequest)
 └─ BeginTransaction
     ├─ handler: load aggregate → mutate → SaveChangesAsync()      rows written
     ├─ DomainEventDispatcher.DispatchUntilDrainedAsync()
     │     pop events → publish → handlers mutate → SaveChanges
     │     repeat until drained (cap 10)
     └─ Commit                                                     all or nothing
```

Why not any of the three reference implementations as-is:

| | Talabat | dome-gym | RiverBooks | here |
|---|---|---|---|---|
| Dispatch vs save | **before** | after (HTTP path) | after | after |
| Transactional with the command | ✗ | ✓ | ✗ | ✓ |
| Same behaviour outside HTTP | ✓ | ✗ forks on `HttpContext` | ✓ | ✓ |
| Response reflects the commit | ✓ | ✗ commits in `OnCompleted` | ✓ | ✓ |
| Failures visible | ✗ | ✗ `catch (EventualConsistencyException) { }` | partly | ✓ propagates |
| Cascading events | accidental recursion | single pass | single pass | ✓ drain loop |
| Missing dispatcher registration | n/a | n/a | ✗ silently disables **all** events | ✓ fails to resolve |
| Events carry | ids | **live aggregates** | **live aggregates** | ids + primitives |

Rules the code holds to:

- **Every state change raises a domain event, including ones nothing subscribes to.** The event
  is the record of the fact; adding a listener later must not require editing the aggregate.
  `ProductRenamedEvent` and `ProductCostPriceChangedEvent` have no handlers today.
- **Events carry ids and primitives, never aggregate instances.** Keeps them immutable,
  serialisable and outbox-ready.
- **A domain event handler may mutate aggregates. It may not do I/O or call another context.**
  To cross a boundary it writes an `OutboxMessage`, which commits in the same transaction.
- **Domain events are not a way to persist a new aggregate.** If a command writes two
  aggregates, the handler adds both and saves once.
- **The DbContext knows nothing about MediatR.** No `SaveChangesAsync` override, no
  `IPublisher`, no `IHttpContextAccessor`.

---

## The module boundary

Seven modules share one process, one solution and one set of plumbing. Two things keep that from
becoming a monolith with folders.

**A module is reached only through its `Contracts` assembly.** `BoomboCard.Sales` references
`BoomboCard.Catalog.Contracts`, never `BoomboCard.Catalog`, so another module's aggregates,
repositories and `DbContext` are not merely discouraged — they are not on the compiler's radar.
An architecture test asserts it for every pair.

**Every per-module service is typed on that module's own `DbContext`.**

```csharp
services.AddScoped<IUnitOfWork<TContext>, EfUnitOfWork<TContext>>();      // per module
```

and **nothing** is registered against the bare `IUnitOfWork`. This is not decoration. It was
registered unkeyed until Phase 0, which is correct for exactly one module: the second
`AddXModule` call would have replaced all three per-module services for *every handler in the
process*, so a Catalog handler would have saved the Sales context, written none of its own rows,
and had an empty transaction committed over the top — no exception, no log, a `201` with nothing
behind it. [Bug 7](docs/BUGS-AND-LESSONS.md) has the full diagnosis.

Because the transaction behaviour needs a concrete context too, and MediatR's open-generic
pipeline registration permits exactly two type parameters, the module cannot be a third one. So
`ModuleUnitOfWorkBehavior` is abstract, and each module closes it over its own context and
constrains it to its own request marker:

```csharp
internal sealed class CatalogUnitOfWorkBehavior<TRequest, TResponse>(
    IUnitOfWork<CatalogDbContext> unitOfWork,
    IDomainEventDispatcher<CatalogDbContext> dispatcher)
    : ModuleUnitOfWorkBehavior<TRequest, TResponse>(unitOfWork, dispatcher)
    where TRequest : notnull, ICatalogRequest;
```

The container skips an open-generic registration whose constraints the closed type would violate,
so the constraint is what does the routing. The honest cost: a command that forgets its marker
compiles, runs, and gets **no** transaction — quieter than the bug it replaced. So
`ModuleWiringTests` fails unless every `ITransactionalRequest` carries exactly one marker, unless
every marker has a registered behaviour, and if anything is ever registered against a bare
interface again.

**Cross-module delivery is at-least-once, so consumers claim before they act.** `OutboxDispatcher`
publishes a message to every subscribed handler and then marks the message as a whole, so one
consumer throwing means the others get re-delivered on the retry. Each module has an
`InboxMessages` table keyed `(EventId, Consumer)`, and a handler's first line is:

```csharp
if (!await inbox.TryClaimAsync(notification, GetType(), ct)) return;
```

The claim is **added, not saved** — it commits with the handler's own work, so "this was consumed"
and "here is what consuming it did" cannot disagree. The composite primary key is the guarantee;
the read in front of it is just the fast path.

An invariant never crosses a boundary, so none of this is a distributed transaction. A Sales
command that sends a Catalog command gets a second transaction on Catalog's own connection,
deliberately — the two commits are sequenced and made recoverable by the checkout session and the
expiry sweeper.

---

## Catalog

**`Product`** — aggregate root. `Sku`, `Name`, `ProductStatus`, `StockLevel`, `CostPrice`,
`LowStockThreshold`, and a collection of `ProductPrice`.

**Value objects carrying the rules:** `Sku` (normalised, validated), `StockLevel` (cannot be
negative; owns the single `CanFulfil` definition), `ProductStatus` (legal transitions),
`Money` (immutable, rounded, currency-checked), `Country` (code → currency → tax rate).

**`StockMovement`** — immutable audit row, its own table. `SUM(Delta)` must equal
`Product.StockQuantity`; there is a test asserting it.

### Decisions

**Explicit price per country, effective-dated.** Not a base price plus FX. A `ProductPrice` has
identity, so a quote stores the `PriceId` it used and the system can *prove* what a customer was
shown. Superseding rather than overwriting gives price history for free. Closes **A6** — Onyx
served browsing from a never-invalidated cache and ordering from the live row, so after any
price change a shopper saw one price and was charged another.

**Prices are tax-exclusive and displayed as-is.** Tax is applied at checkout only, so there is
no inclusive/exclusive display mode to get wrong.

**Tax hardcoded per country, in one place** (`Country`). Two countries: `US/USD @ 8%`,
`AE/AED @ 5%` — different rates, so the tax path is actually exercised. Onyx held the same promo
expiry in two files with two different dates (**M14**); one definition makes that impossible.
When a rate must vary by category or over time, this graduates to a table.

**No reservations. Overselling prevented by the database.** `AggregateRoot.Version` is a
concurrency token bumped by `VersioningInterceptor`, so EF writes
`WHERE Id = @id AND Version = @original`. A losing writer affects zero rows, raises
`DbUpdateConcurrencyException`, and is retried against fresh state.

```
3 in stock. A wants 2, B wants 2, simultaneously.
A: read(3, v=7) → ok → UPDATE WHERE v=7 → 1 row  → stock 1, v=8
B: read(3, v=7) → ok → UPDATE WHERE v=7 → 0 rows → CONFLICT
   retry: read(1, v=8) → 1 >= 2 FAILS → InsufficientStock
Sold 2, stock 1. Correct.   (Onyx D1: both wrote 1 — four units sold from three.)
```

Retry must wrap the **whole command dispatch in a fresh scope** — after a conflict the change
tracker is poisoned and the aggregate's events have already been popped. Reload, never reuse.

**Stock moves only for a stated reason.** `Deduct` at checkout; `Restore` on payment failure,
session expiry, cancellation or return; `ReceiveStock` for goods-in. Nothing invents stock —
Onyx's job added 50 units every 15 minutes with no supplier or delivery (**D5**), which silently
healed every oversell before anyone could see it.

**Idempotency on `(ProductId, Reference, Reason)`**, unique at the database. `Reference` is the
checkout session id. This is what makes at-least-once outbox delivery safe and makes a retried
checkout a no-op rather than a double deduction.

**No cache.** Onyx put a singleton `ProductCache` holding a *scoped* `DbContext` in front of the
catalogue lookup (**A1**), disabled the DI scope validator to permit it (**A2**), never
invalidated it (**A3**), wrote to its dictionary outside its own lock (**A4**), and shipped a
warmer that no-opped after its first run (**A5**) — for a five-product catalogue served by an
indexed primary-key lookup. A cache goes in when a measurement asks for one.

### Invariants

- Stock cannot go negative — `StockLevel` cannot represent it.
- A product cannot be activated without a price in at least one country.
- Stock can only be deducted from an `Active` product. Restores work on `Discontinued` ones so
  returns reconcile.
- A price's currency must match its country's currency; the caller never supplies a currency.
- At most one open price per country.
- An active product cannot have its last price withdrawn.

---

## Customers

**`Customer`** — aggregate root. `EmailAddress`, `PersonName`, `PasswordCredential`,
`CustomerRole`, `CustomerStatus`, a verification timestamp, and a collection of `Address`.

**Separate aggregates, because each is an unbounded log or an independently-written row:**
`AuthAttempt` (every sign-in attempt, successful or not), `PasswordResetToken`,
`EmailVerificationToken`.

Onyx's `Customer` was eight public get-set properties with no behaviour — model-bound from an
HTTP body (**S3**), serialised to an HTTP response with the hash included (**S11**), looked up by
interpolated SQL on the unauthenticated login path (**S1**), and hard-deleted by an anonymous
endpoint (**S2**, **D13**). Six Critical findings on one class, every one a consequence of the
class having no opinions about itself.

### Decisions

**Identity's hasher, without Identity.** One package reference for one class —
`PasswordHasher<T>` — and no `IdentityDbContext`, no `IdentityUser`, no `UserManager`, no
stores, no tables this system does not use. The aggregate stays ours, with its own lifecycle and
its own events; only the part that should never be hand-written is borrowed. That part gives
PBKDF2-HMAC-SHA512 at 100k iterations, a random 128-bit salt per password, a self-describing
format marker so the work factor can rise later, and a fixed-time comparison. Onyx's
`HashHelper` had none of the five: one unsalted SHA-256 pass, compared with `==` (**S7**).

**A correct password against an outdated hash is re-hashed on sign-in.** That is what lets the
KDF cost move with hardware without a migration or a forced reset — and it is deliberately not
recorded as a password *change*, because nothing the customer controls changed.

**The value object is what closes the SQL injection.** Onyx reached for raw SQL to get
case-insensitive email matching, via SQLite's `COLLATE NOCASE` — which also welded the query to
one provider while the same PR claimed the provider was swappable (**X13**). `EmailAddress`
normalises to lower case, so the lookup is an ordinary equality predicate on an ordinary unique
index and there is nothing left for string concatenation to do. **S1** was a symptom of putting
a collation concern where a domain concern belonged.

**Every sign-in failure returns the same error.** Wrong password, unknown address, suspended
account — one 401, one body. Distinguishing them turns the endpoint into an account-enumeration
oracle. The distinction is recorded in `AuthAttempt`, where it is useful to us and not to an
attacker. Lockout is the one deliberate exception: a customer who has mistyped their password
five times needs to be told that waiting will fix it, and an attacker who has already made five
attempts against an address knows it is worth attacking.

**Two independent throttles.** A fixed-window rate limiter partitioned by client address bounds
a *client*; the progressive lockout on the credential bounds an *account*. An attacker
distributing across many addresses still hits the lockout; one hammering a single account still
hits the limiter. Neither alone is enough, and Onyx had neither (**S12**). The lockout doubles
from one minute to a thirty-minute cap and clears on success — uncapped doubling would turn a
modest guessing attempt into a permanent denial of service against a real customer, which hands
the attacker a different win.

**A locked-out account rejects the correct password too.** Otherwise the lockout only throttles
wrong guesses, and the attacker who finally guesses right is let straight in.

**The audit row is produced on every path, so `RecordSignInAttempt` returns the attempt rather
than an `ErrorOr`.** A result type carrying the error has no room for the row, and the row for a
*failure* is the one that matters.

**Tokens are 256 bits from the OS CSPRNG, stored as a hash, single-use, and expiring** — one
hour for a reset, seven days for a verification. The asymmetry is the point: one proves an inbox
received mail, the other grants "become this customer". Single use is enforced by the aggregate's
`Version`, not by the `IsRedeemed` check: two requests presenting the same token both pass the
check, and the version predicate is what makes exactly one of them win. Onyx's reset token was
eight characters from `new Random()`, stored in plaintext, with no expiry and no single-use
marker (**S14**); its payment confirmation code was twenty bits (**S13**).

**The default address is one nullable id on the customer, not a flag per address.** The first
attempt was a boolean plus `.HasFilter("[IsDefault] = 1")` — SQL Server syntax in a module whose
whole seam is that it references no provider, and a constraint that would hold on some databases
and not others. One column cannot hold two values, so "exactly one default" stopped being a rule
to check and became a shape that cannot express the violation.

**Erasure, not deletion, and it is one-way.** The row survives so the financial record does; the
name, email, credential and every address line are overwritten, and
`CustomerAnonymisedIntegrationEvent` tells other modules to erase their copies — the half of an
erasure request a `DELETE` cannot do. The placeholder email carries the customer id, because a
constant would collide on the unique index and the *second* erasure in the system would fail.
Onyx removed the row outright from an anonymous endpoint, orphaning or failing on every
dependent order, and did not achieve erasure either (**S2**, **D13**).

**`CreditLimit` is deleted.** Onyx carried the column and never read it (**M13**). A field with
no check is the speculative seam **A45** is about; the omission is on the record in
[ROADMAP.md](docs/ROADMAP.md) rather than silent.

**Client addresses are stored as a keyed HMAC, never in the clear.** The operational question is
"is this the same client again", which a hash answers exactly as well — and the IPv4 space is
small enough to enumerate completely, so an *unkeyed* hash is not anonymous at all. The key is
required configuration with no default, because a security control with a convenient fallback is
one that is off in production without anyone noticing.

**`EmailAddress.ToString()` is redacted.** `ToString()` is what an interpolated log message
reaches for, so the only thing it can reach has to be safe; code that genuinely needs the
address asks for `.Value` and says so. Onyx **A42** logged
`$"Fetched customer profile: {customer.Email}, credit limit {customer.CreditLimit:C}"` — an
interpolated string, so it bypassed structured logging *and* wrote an identifier plus a financial
detail into plaintext logs on every profile read.

### Security, as composition

The host owns the authentication scheme, the signing key, the policies and the limits; a module
only ever *names* a policy. Seven modules each configuring their own would be seven chances to
configure one differently.

| | How |
|---|---|
| **Deny by default** | `FallbackPolicy = RequireAuthenticatedUser()`. A route naming no policy still needs a token, so forgetting one fails closed. Onyx applied `[Authorize]` per action and missed one — `DELETE /api/admin/customers/{id}` was anonymous (**S2**, **S16**) while the PR claimed every admin endpoint was behind the Admin role (**X14**) |
| **Ownership** | `SelfOrAdmin` compares the route's `customerId` to the principal's claim, and **fails closed** when the route carries no usable id — otherwise a route that forgot the parameter has no check at all, which is **S4** reintroduced by a typo. There is a test for exactly that case |
| **Token validation** | Issuer, audience, lifetime and signing key, all explicitly, with `ClockSkew = Zero`. Onyx validated neither issuer nor audience (**S15**) |
| **Secrets** | `appsettings.json` carries **no** key, so production refuses to start without one from the environment. Dev-only values live in `appsettings.Development.json` and are not secrets — anyone reading the repo has them. Onyx committed a live payment key, a production database password and the signing key to the base file (**S5**) |
| **Role changes** | Three independent guards: the `Admin` policy on the endpoint, the aggregate refusing a change where the actor is the subject, and the published event carrying the actor. Onyx had none of the three (**S3**, **A40**) |
| **Rate limiting** | Before authentication in the pipeline, so a flood is rejected before any PBKDF2 work happens — an unlimited login endpoint is a CPU amplifier as well as a guessing oracle |

### Invariants

- Email is unique, normalised, and unique *at the database*.
- A role only changes through `ChangeRole`, which demands an actor and refuses self-promotion.
- A customer with addresses always has a default, and it always names one of theirs.
- Nothing about an anonymised customer can change.
- Only an `Active`, verified customer may transact — one definition, published as
  `CustomerSummaryResponse.CanTransact` so Sales does not re-derive it, with a test asserting the
  SQL projection and the C# property agree on every combination. (Onyx **M18** is the warning:
  one availability rule written twice, as `<=` and `<`, so two screens disagreed about the same
  product.)

---

## Promotions

**`Promotion`** — aggregate root. `PromotionCode`, `Discount`, `ValidityWindow`,
`PromotionStatus`, `PromotionScope`, `StackingPolicy`, a minimum spend, two usage caps, and a
live redemption count.

**`PromotionRedemption`** — immutable ledger row per claimed use. Its own table, because the
rows grow without bound and the caps have to be enforced by constraints.

Onyx's entire promotions capability was nine lines — a `private static readonly Dictionary` of
two codes, and a `switch` keyed on the same string that returned the rate. It carried six
findings. The full diagnosis is in **[CONCEPTS.md §22](docs/CONCEPTS.md)**.

### Decisions

**The validity window is a value object, and both ends are instants.** `[StartsAtUtc, EndsAtUtc)`
— half-open, UTC, stated once. "Valid through 31 December" is `2027-01-01T00:00:00Z`: the caller
states the instant it means rather than a date plus a convention about how to read it. Onyx's
comment said "through the end of the listed day" while its code compared `DateTime.Now` against
midnight at the *start* of that day, so the code died a day early and the cutover instant moved
with the deployment's timezone (**M17**).

**One row holds everything about a code.** **M14** was not a wrong date — it was two places that
had to agree. The expiry lived in a dictionary and the rate in a separate `switch`, and the same
code's expiry was `2026-12-31` in the pricing service and `2026-11-30` in the nightly report, so
from December the report showed zero uptake on a code still costing margin. `UX_Promotions_Code`
is unique, and one row carries the rate, window, caps, scope and stacking together. There is no
second location to diverge from.

**Failures name their reason — the opposite of the sign-in endpoint, deliberately.** Phase 1
collapses every authentication failure into one response because distinguishing them is an
enumeration oracle. A promotion code is not a credential: the customer typed it, and "your
basket is below the $50 minimum" is the entire value of the response. Onyx **M15** returned a 0%
rate for an unrecognised code and let the order through as though a valid one had been used, so a
typo became silently no discount with no error and no log.

The one exception: a Draft or Paused campaign reports as `CodeNotRecognised`, because internal
campaign state is not something a shopper can act on.

**Two caps, two mechanisms, because they have different races.**

| Cap | Enforced by | Why that one |
|---|---|---|
| Total uses | the aggregate's `Version` concurrency token | The count is state an invariant is checked against. Two checkouts racing for the last use both read 99 and both pass; the version predicate makes exactly one win — the same shape as Catalog's oversell prevention |
| Uses per customer | `UX_PromotionRedemptions_CustomerOrdinal` | A count cannot be a unique index, so the ledger row carries `CustomerOrdinal` — "this customer's n-th use". Two concurrent reservations compute the same ordinal and collide at the database |

The obvious alternative for the second was a filtered unique index on
`(PromotionId, CustomerId)`. It needs provider-specific SQL in a module that references no
provider, **and only works for a cap of one** — that exact mistake was made and reverted in
Phase 1's address defaulting. The ordinal generalises to any N.

A third unique index, `(PromotionId, CheckoutSessionId)`, makes a retried checkout a no-op — the
same reference-idempotency Catalog's stock movements use, and what makes retry the cheapest
recovery layer in the checkout saga.

**Validate, reserve and release are three operations.** A quote is shown many times and consumed
once; if validation reserved, the last uses of a limited campaign would be burned by people
browsing. `Release` takes a **session id, not a promotion id**, and is safe to call
unconditionally — Sales' expiry sweeper fires it blind without knowing whether that session
reserved anything, exactly as it does `RestoreStockCommand`. Making the caller enumerate which
promotions were on the session would mean two modules holding one fact, which is how **M14**
happened.

**A release does not reopen a closed campaign.** The slot returns to the count, so a limited
campaign is not permanently short because somebody abandoned a basket — but a human decides
whether to run it again. Automatic reopening would make a "first 100 customers" campaign's end
date unpredictable.

**Scope changes the basis, not just the eligibility.** "20% off headphones" against a basket of
headphones and a laptop discounts the headphones, not a fifth of the laptop — so a scoped
promotion computes against the sum of its own matching lines. The minimum spend is still tested
against the *whole* subtotal, because "spend 100, get 20% off headphones" is the normal reading.
Onyx applied every discount to the whole subtotal because that was the only number it had.

**There is no category scope**, and finding out why produced a documentation bug worth recording:
`CONCEPTS.md` §11 described an `IX_Products_CategoryId` index, with a code snippet, for a column
`Product` does not have. The fix was to correct the document rather than add the column —
categories are a recorded capability gap, and adding a property so a stale paragraph stops being
wrong is the speculative generality **A45** is about. See
[bug 11](docs/BUGS-AND-LESSONS.md).

**A fixed-amount discount is denominated; a percentage is not.** 10% off is 10% off in any
currency; "50 off" is not, and applying a USD code to an AED basket would hand out roughly 3.7
times the intended discount. A minimum spend is denominated too, even for a percentage discount,
because "over 50" also means two different things. Converting at an exchange rate is deliberately
not built — a rate must come from somewhere, be effective-dated and be auditable, and `Country`
carries no FX at all.

**Discounts are capped at the basis.** A "50 off" code on a 30 basket yields 30, not 50 — the
alternative is a negative subtotal flowing into the shipping threshold and the tax base,
producing a total nobody can explain. Capping lives in the value object so every caller gets it.

**Stacking is enforced at reservation, in both directions.** An `Exclusive` code already on a
session rejects a `Combinable` newcomer, because "exclusive" must mean nothing else may join it.
`Exclusive` is the default: a merchandiser who has not thought about stacking has not intended
"combine this 30% code with every other 30% code", and the strict default costs a conversation
while the permissive one costs margin.

**Live terms are frozen.** `Reconfigure` is Draft-only, because a live campaign's terms are
something customers may already have acted on — silently changing the minimum spend under them is
the same class of wrong as **A6**, showing one price and charging another. To change a live
campaign, close it and create its successor; the ledger then attributes each redemption to the
terms actually in force. `Activate` also refuses a window that has **already ended**, which is
**M16** as an invariant rather than a warning: Onyx shipped `SUMMER25` live and already expired.

**The rate column is `precision(9,4)`, not the model-wide `(18,2)`.** The money convention is
right for money and wrong for a multiplier — it would have rounded `0.075` to `0.08` and turned a
7.5% campaign into an 8% one. Same class of mistake as the 500-character string convention
landing on the outbox payload ([bug 9](docs/BUGS-AND-LESSONS.md)).

**Campaign reporting, which Onyx could not have built.** Its promo code was a nullable string on
the order, so nothing recorded whether a code had been applied, abandoned or refunded. Because a
release *marks* its ledger row rather than deleting it, three numbers are separable:
`liveRedemptions`, `releasedRedemptions`, `distinctCustomers`. The middle one is the difference
between "100 people used this" and "140 tried and 40 abandoned". Aggregated in SQL with the empty
case guarded — **A18** is what happens when a report assumes otherwise.

### Invariants

- A code is unique, normalised, and unique *at the database*.
- Terms change only while the promotion is a `Draft`.
- A promotion is usable only when `Active`, inside its window, under both caps, over its minimum
  spend, and against a basket its scope matches — **one definition**, `Promotion.Evaluate`, called
  by both the validation query and the reservation command. Two independent copies could quote a
  discount the reservation then refused, which is **M18** applied to money.
- `RedemptionCount` never exceeds the total cap.
- `Closed` is terminal.

---

## Loyalty

**`PointsAccount`** — aggregate root, one per customer. A `Balance`, a closed flag, and nothing
else.

**`PointsLedgerEntry`** — immutable record of every movement, with its reason, its cause and the
balance it left behind. `SUM(Delta)` must equal `Balance`, and
`loyalty.points-integrity-check` asserts it.

Onyx's loyalty scheme was one `int` on `Customer` and one method that subtracted from it — called
*after* the order total had been computed and committed. It carried three findings: the points
bought nothing (**M11**), nothing could ever increase them (**M12**), and the sufficiency check
threw a 500 after the order and all its stock decrements were already committed (**D4**). Full
diagnosis in **[CONCEPTS.md §23](docs/CONCEPTS.md)**.

### Decisions

**The balance is a column, and the roadmap said it should not be.** The plan for this phase said
"the balance is a `SUM`, never a column" — aimed at Onyx's counter-with-no-log, and half right.
Building it exposed the other half: **a `SUM` cannot be the subject of a concurrency token.** Two
simultaneous redemptions of the last 500 points both compute the same `SUM`, both pass the
sufficiency check, and both commit — spending 1,000 from 500, which is Onyx **D1**'s shape
applied to money. So the balance is state under `AggregateRoot.Version` and the ledger is the
immutable log, exactly as `Product.Stock` sits against `StockMovement`.

**What makes the counter trustworthy is not that it is the source of truth — it is that something
proves it matches the log.** `PointsIntegrityCheckJob` asserts `SUM(Delta) == Balance` every
pass, reports into `JobRun.ItemsFailed`, and **never corrects**. That is the whole difference
from Onyx, which had a counter, no log and nothing checking anything — and whose one
reconciliation job (**D5**) silently healed every discrepancy by inventing stock.

**Points are worth money, at 100 to one unit of currency** — so one point is exactly one cent.
The rate is chosen so **the conversion can never round**: both currencies are 2-decimal, so every
whole number of points maps to an exact cash amount and back. "3 points per unit" would make 100
points worth 33.333…, and that fraction then has to be rounded somewhere, in a direction somebody
decides, on a number the customer can check. Onyx rounded nowhere and produced totals like
`83.096766` (**M7**).

**Qualifying spend is merchandise after discounts, excluding tax and shipping.** Tax is money
collected for a government, so paying loyalty on it funds a reward from somebody else's money.
Shipping is a pass-through cost, so earning on it rewards buying from further away. And discounts
come off first, because earning on the pre-discount figure makes a promotion cost margin twice.
Earning truncates rather than rounds — rounding up means 49.50 earns 50, and splitting an order
in half would then earn more than placing it once.

**Redemption is two steps, which is the D4 fix.** `ValidatePointsRedemptionQuery` prices it
during the quote with no write; `RedeemPointsCommand` debits in the transaction that confirms the
session. Both call the same rule, `PointsAccount.CanRedeem` — there is a test walking all 28
combinations of four balances and seven amounts asserting the check and the debit agree, because
a quote promising a discount the debit refuses is **M18**.

**Expiry needs no lot tracking, because consumption is FIFO.** Points are consumed oldest-first,
so whatever is left is always the most recently credited — making the amount still alive from
before a cutoff exactly `max(0, creditedBefore − consumedEver)`. Two indexed sums, no
`PointsLot` entity, no per-batch counters, nothing mutable on a ledger row. Expiry writes a
negative entry rather than removing the earning it lapses.

**Points returned after a failed checkout get a fresh twelve months.** Slightly generous, and
deliberate: the customer is being handed back points they tried to spend on a checkout that
failed for our reasons, and shortening their life too would be charging them for our failure.

**The expiry job is load-bearing; the promotions one was not.** `PromotionExpiryJob` only records
a status `Evaluate` already enforces. Nothing except `PointsExpiryJob` writes off lapsed points,
so if it never runs the liability never ends. It is still the opposite of **D5** in the direction
that matters: it only removes value the scheme already said had a deadline, and writes a row
saying so.

**Three operations clamp to the balance** — reversal on refund, expiry, and a negative
adjustment. Each absorbs the shortfall rather than carrying a debt, because **a negative loyalty
balance is a concept this scheme does not have**, and inventing one for a refund edge case would
be worse than the difference. The private `Debit` primitive refuses anything over the balance, so
a caller that forgets to clamp fails loudly rather than producing one.

**Nothing is ever deleted or updated.** Expiry, reversal and forfeiture all write negative rows.
A balance that can be reconstructed by replaying the log is one a customer can be shown an
explanation of — which is the answer to **M11**'s "there is no way to answer 'where did my points
go?'".

**No endpoint grants, redeems, releases or reverses.** Those are module-to-module commands the
checkout and refund flows send. An HTTP route that credits points is the same category of mistake
as **M2**, where the client supplied the amount to charge.

### This module is the first real consumer of another's events

Phase 0 built a per-module inbox for at-least-once delivery, and nothing used it until now.

| Event | What Loyalty does |
|---|---|
| `CustomerRegisteredIntegrationEvent` | Opens the points account |
| `CustomerAnonymisedIntegrationEvent` | Closes it, forfeiting what remains |

Both claim the event before acting, with the claim **added rather than saved** so it commits with
the work — the alternative allows a crash between the two, leaving an event marked consumed with
none of its effects applied, permanently. Both then call `SaveAndDispatchAsync`, because an
integration event handler is not in the MediatR request pipeline and nothing would otherwise
drain the events the aggregate raised ([bug 10](docs/BUGS-AND-LESSONS.md)).

Verified live: registering a customer produced a points account in another module's database
within two seconds; anonymising them closed it. That second handler is also the half of an
erasure request a `DELETE` cannot do — Onyx **D13** deleted the customer row and left every copy
elsewhere standing. The ledger itself survives: it is a financial record holding a customer id
and nothing else personal.

### Invariants

- The balance never goes negative — and `SUM(Delta) == Balance`, asserted every 15 minutes.
- Every movement produces a ledger entry naming its reason and its cause. Nothing changes the
  balance silently.
- One account per customer, unique at the database.
- One movement per `(account, reference, reason)`, unique at the database — so a redelivered
  "order paid" credits once and a retried checkout debits once.
- A closed account cannot change. Terminal, like `CustomerStatus.Anonymised`.

---

## Sales

**`CheckoutSession`** — aggregate root. A priced basket with a 20-minute deadline, and the durable
handle every hold placed in another module is named by. Its total always equals the sum of its
lines and adjustments.

**`CheckoutLine`** — a line as the server resolved it: product, quantity, unit price, line total,
and the **`PriceId`** proving which price the customer was shown.

**`QuoteAdjustment`** — one pricing step's contribution, with its kind, a description, a signed
amount and the **basis** it was computed from.

**`Order`** — aggregate root, created at confirm from the session, copying the quote verbatim.
Gap-tolerant `BC-00000123` number, a guarded state machine, and per-item `QuantityShipped`.

**`OrderPayment`** — one capture a provider confirmed. A row rather than a nullable column on the
order, because the reference needs a unique index and a nullable unique column would permit
exactly one unpaid order.

**`QuoteCalculator`** — the seven-step pricing pipeline, as a pure static function.

Onyx had one selling endpoint, `POST /api/orders`, whose handler created the order row, then
priced it, then deducted stock, then charged the card, committing at each step. Fourteen findings
live in that method and the pricing service it called, including six in nine lines of arithmetic.
Full diagnosis in **[CONCEPTS.md §24](docs/CONCEPTS.md)**.

### Decisions

**The shipping threshold is tested against the pre-discount merchandise subtotal.** That single
argument is the whole of **M8**. Onyx tested it against the post-discount figure, so a discount
could pull a basket back under the threshold and spending more could cost more — a bump in the
total, not a step. The pre-discount subtotal is the one figure that rises monotonically with what
the customer puts in the basket, and no later step can lower it.

**Stating M8's property correctly was most of the work.** "The total never falls as the basket
grows" is the obvious phrasing and it is **false by design** — at the threshold the total drops by
the delivery charge, because that is what a free-delivery offer is. The first version of the test
asserted it and failed on correct code. What distinguishes the fix from the bug is piecewise:
within a shipping band the total never falls, the drop happens in exactly one place, and that
place does not move with the discount. All three are exhaustive over 0–500 in 1-cent steps.

**Whether shipping is taxed is a stated policy, not an assumption.** The register's complaint
about **M10** was not that Onyx excluded shipping from tax; it was that the choice was
"undocumented and untested, so it cannot be distinguished from a bug". `ShippingIsTaxable` is a
per-country decision with the jurisdiction rule written next to it.

**Three calls, not one.** Prices are visible before anything is held, and only the third call
touches another module. `POST /api/checkouts` can be called as many times as the customer likes
and holds nothing; the confirm returns **202 Accepted**, because an order that exists and is not
yet paid is what has actually happened.

**The session commits first.** If it were created at confirm time, a confirm failure would leave
stock held against an id that exists nowhere — invisible, permanent loss of sellable inventory.
Committing first makes every intermediate state enumerable, which is what the expiry sweeper
depends on.

**The order number tolerates gaps.** A retried transaction consumes a number it does not use.
Guaranteeing density means a second write to reclaim numbers, under contention, for a cosmetic
property nobody has asked for. Onyx's `static int` reset to 1000 on every restart and was
per-process (**D8**).

**`MarkOrderPaidCommand` has no HTTP route.** That absence is **M1** and **M4** enforced
structurally: an order transitions to `Paid` only from a provider's answer, and the provider
reference is required and never fabricated. Onyx exposed `PUT /api/orders/{id}/status` with an
unvalidated string and no ownership check (**S7**).

**Sales orchestrates the releases; Loyalty subscribes directly.** Sales is the only module that
knows what a checkout *held*, so it consumes its own abandonment events and fans out three release
commands — which keeps Catalog, Promotions and Loyalty completely ignorant that Sales exists. A
points grant or reversal needs nothing but the customer and the order, both on the event, so
Loyalty subscribes to those itself. The asymmetry tracks who owns the missing information.

### The earn rule finally has a trigger

Loyalty shipped in Phase 3 with `EarnRule`, the ledger, the idempotency index and
`GrantPointsCommand` fully tested — and nothing that called them, because a module cannot
subscribe to a contract that has not been written. The gap was recorded in `ROADMAP.md` and in the
command's own documentation rather than implied.

Closing it took **one inbox-guarded handler and no new rule**. That the earn side needed nothing
but a subscription is the clearest evidence the seam was drawn in the right place.

### The bug this module found in its own code

The end-to-end run caught a release handler restoring a basket's lines for a session that had
**never held anything** — two units of inventory created out of a cancelled basket. That is Onyx
**D5** by a different road, in a handler whose documentation cites D5 as the thing it is carefully
not doing.

Idempotency could not have caught it: a unique index stops an operation being *repeated*, and this
was an operation performed *wrongly*, once. The integrity check could not either, because the
counter and the log **agreed** — both said 10.

Fixed in both modules. Sales records `HoldsPlacedAtUtc` and does not ask; Catalog caps every
release at `SUM(Delta)` for that reference and so cannot comply. The second is the one that closes
the class, and it belongs in Catalog because **"never invent stock" is Catalog's own invariant, and
an invariant enforced by trusting the caller is not enforced**. Write-up in
[bug 13](docs/BUGS-AND-LESSONS.md).

### Invariants

- A session's total equals the sum of its lines and adjustments — and `Order.PlaceFrom` **refuses**
  a session where it does not.
- An order can only be placed from a session that is `Holding`, which means TX 1 succeeded.
- One order per checkout session, unique at the database — so a double-clicked confirm places one
  order and deducts stock once.
- One capture per provider reference, unique at the database and global across orders.
- One line per product per session, unique at the database; duplicates are collapsed before
  pricing.
- Order numbers are unique, from a singleton sequence under a concurrency token.
- Confirmed, Expired and Cancelled are terminal on a session; a confirmed session cannot be
  expired out from under its order.
- An order reaches `Shipped` only when every item is fully shipped — the status is derived from the
  items, so it cannot disagree with them.
- Every quoted figure is rounded to two places, and the total is exactly the sum of its parts.

---

## Payments

**`Payment`** — aggregate root, one per attempt to take money for an order. A fixed amount, a
nine-state machine, a stable idempotency key, and a provider reference that is nullable and never
fabricated.

**`PaymentAttempt`** — immutable record of every provider interaction: operation, outcome,
idempotency key, correlation id, provider reference and code, latency. Never updated.

**`Refund`** — one row per refund, each with its own provider reference and its own idempotency
key, so partial refunds compose and deduplicate independently.

**`PaymentClaim`** — a row whose primary key *is* the order id. The conditional-uniqueness
constraint behind "at most one payment per order can take money".

**`ProviderWebhook`** — every notification a provider sends, recorded with its verdict and its raw
payload before anything is acted on.

**`IPaymentGateway`** — declared here, implemented in the host. This module references **no HTTP
package at all**.

Onyx's entire payment surface was one controller action. Nine findings, four of them Critical, and
the register's note on **M1** is the one that stays with you: *"the configured gateway host does
not resolve, so every payment ever taken by this system is in this state"* — where "this state" is
a row reading `"Paid"` for money that never moved. Full diagnosis in
**[CONCEPTS.md §25](docs/CONCEPTS.md)**.

### Decisions

**The gateway outcome has four cases, not a `bool`.** This is the one insight the module is built
on. Onyx's result was `class PaymentGatewayResult { bool Success; string GatewayReference; }`, and
a two-state answer cannot express *"we do not know"* — so the code had to guess, and guessed
differently in two places ten lines apart. The controller read a lost response as success
(**M1**); the retry loop read the same lost response as failure and charged again (**M5**).

**`Indeterminate` is the case that earns its keep.** A timeout against a payment provider is not
a failure, it is an unanswered question, and there are only three things you can do with one:
assume it failed and charge again, assume it succeeded, or **go and ask**. Only the third is
correct, and it requires having sent something the provider can recognise the request by.
`IdempotencyKey`, `AttemptOutcome.Indeterminate`, `EnquireAsync` and `IndeterminateSinceUtc` exist
for no other purpose.

**M1 and M4 are one type, not two rules.** `ProviderReference` has a single factory,
`FromProvider(string?)`, which rejects null and blank. No `New()`, no default, no implicit
conversion. A caller holding no provider answer has nothing to pass to `Capture`, so Onyx's
`result?.GatewayReference ?? confirmationCode` is not a line someone has to be talked out of — it
is a line that does not compile. The rule itself was already known; the difference is whether the
agreement is load-bearing.

**The transaction never crosses the HTTP call.** Three steps on two transactions: the intent
commits, the provider is called outside every transaction, the outcome commits separately.
`SubmitPaymentCommand` and `RefundPaymentCommand` deliberately carry **no module marker**, which
is how they get no transaction — and the wiring test asserting that every transactional request
carries exactly one marker is what makes that absence a visible decision rather than an oversight.
Onyx held a transaction across up to three gateway calls at a 100-second timeout each; on its
single-writer database that was five minutes of total write unavailability per payment (**A12**).

**A declined card is a 200.** With `status`, `declineCode` and `canRetry`, because the customer
needs to know which of "try another card" and "call your bank" applies. Onyx had no declined path
anywhere in the system, so it had nothing to tell anyone.

**A decline is never retried; a transport failure is.** A decline is the provider's considered
answer. Retrying one is how a customer's bank starts treating you as an attacker. Onyx retried
everything three times and counted declines against the same budget as network blips.

**One payment per order that can take money — as a row, not a filtered index.** A declined card
must be followed by another attempt, so an order legitimately accumulates several payments; the
constraint is a predicate over the *status*. `PaymentClaim`'s primary key is the order id:
initiating inserts it, a dead end deletes it, a capture keeps it forever. Third time this project
has made that move, after `Customer.DefaultAddressId` and `OrderPayment`. The rule is now stated:
**when a conditional uniqueness constraint is needed, make the condition the existence of a row.**

**Card data never enters this system.** The client tokenises against the provider and sends a
token; there is no `cardNumber` field anywhere for anyone to be tempted by. It is the only version
of this that keeps the whole codebase out of PCI-DSS scope.

**The gateway lives in the host.** `IPaymentGateway` is declared here and implemented in
`BoomboCard.Api`, exactly as `IAccessTokenIssuer` is — the base address, the API key, the webhook
secret, the retry policy and the TLS configuration are all deployment concerns. The practical
consequence is that this module references no HTTP package, so **Onyx S8 cannot be written inside
it**: there is no handler type in scope.

### S8: the three tokens that must not come back

```csharp
ServerCertificateCustomValidationCallback = (_, _, _, _) => true
```

On the payment gateway client. That callback is the one control preventing an attacker
impersonating the provider; with it returning `true`, card tokens and charge requests are readable
and alterable by anyone on the wire.

**It is banned by a source-convention test, not by a comment**, and the reasoning is worth being
explicit about: it is three tokens, it looks like configuration, it makes a stubborn sandbox work
immediately, and it is the first result for every search about TLS errors in development. *It will
be suggested again.* The only durable defence is a build failure — and because the scan discovers
every project under `src/` at run time, it already covers modules that do not exist yet.

The adapter reports a TLS handshake failure as a transport failure, which is the correct behaviour
and the precise inverse of S8: Onyx disabled validation so that case would silently *succeed*.

### The job that corrects, and why that is not D5

Every other integrity job here **reports and never corrects**, because a job that quietly repaired
a discrepancy would erase the evidence of the defect that caused it — Onyx **D5**.

`payments.reconciliation` corrects, and the distinction is not a compromise. D5 **computed** a
figure it had no authority over and imposed it. Reconciliation **retrieves** the answer from the
system of record: for *"did this card get charged?"* the provider is the authority and we are not.
It has no opinion at all — it asks, and writes down what it is told. Where the provider cannot say
either, the payment stays visibly unknown rather than being settled in some direction.

### The chain this module completes

```
capture  →  PaymentCapturedIntegrationEvent  →  Sales marks the order paid
         →  OrderPaidIntegrationEvent        →  Loyalty credits the points
```

Three modules, none referencing another's internals, each step guaranteed by an outbox row
committed in the same transaction as the state change that produced it. Onyx did all three in one
controller action with four separate commits and no compensation (**D3**).

`MarkOrderPaidCommand` documented itself in Phase 4 as a stand-in for exactly this subscription,
and needed no change when it arrived. It still has no HTTP route: the only caller is that
consumer, and the only thing that makes it run is a provider saying the money moved.

A decline raises the event that releases the order's holds — **the first thing in the system that
can mean `RestoreReason.PaymentFailed`**, a value that has sat in `Catalog.Contracts` since
Phase 0 with nothing able to produce it.

### The most important guard in the consumer code

```csharp
if (!notification.AttemptsExhausted || notification.OutcomeIsUnknown) return;
```

`OnPaymentFailedCancelOrder` cancels the order only when the budget is spent **and** the outcome
is known. Cancelling on an unknown outcome would release stock for an order that may in fact have
been paid — an oversell caused by cleaning up, which is worse than one caused by a race because it
looks deliberate and the customer has a receipt.

Onyx's handling of the same situation was to treat every unanswered request as a failure and
charge again. The symmetric mistake on this side is to treat it as a failure and give the goods
away. Both come from the same missing distinction.

### Tests: unit only, by request

The pure parts are covered thoroughly — the state machine's every transition, the four-way outcome
mapping, the refund arithmetic, the three reference types. Those carry **M1**, **M4**, **M5** and
**A39** between them.

What that leaves uncovered is stated rather than implied. Three guarantees are database
constraints asserted by configuration, by the generated migration and by the live smoke run, but
not by a test that inserts a conflicting row: `PK_PaymentClaims` (**M3**),
`UX_Payments_IdempotencyKey` and `UX_Refunds_Idempotency`.

And the defect that decision let through is written up as [bug 14](docs/BUGS-AND-LESSONS.md): the
smoke run found a submit returning `status: "Pending"` for a payment that was `Captured`. The
write was correct; the handler re-read the aggregate through a context that had been tracking it
since before the provider call, and EF's identity map handed back the stale instance. Nothing is
wrong with any unit — the bug lives entirely in the interaction between two DI scopes and EF's
identity map, so it needs a real container to exist at all.

### Invariants

- A settled status is unreachable without a provider reference, and the reference can only be
  built from a provider response.
- The idempotency key is generated once and never changes — not across attempts, not across
  failures.
- No two payments share an idempotency key (unique at the database).
- At most one payment per order can take money (`PaymentClaims`' primary key).
- Every provider interaction leaves an attempt row, including one the state machine refuses.
- A decline is terminal and never retried; a failure is retryable; an unknown outcome is neither
  settled nor abandoned.
- Refunds never exceed what was captured, and the refunded total equals the sum of its rows.
- A refund deduplicates on its own key, unique per payment.
- Every webhook is recorded before it is acted on, deduplicated on the provider's event id, and
  applied only if signed and only if the state machine permits it.

---

## Notifications

**`NotificationRequest`** — aggregate root. One message this system owes one person, with a
six-state machine, a frozen body, a retry budget and a finite end.

**`DeliveryAttempt`** — immutable record of every interaction with a provider: outcome, which
sender handled it, the provider's message id, our correlation id, latency.

**`NotificationTemplate`** — a pure static function from a kind plus facts to a subject and a body.

**`INotificationSender`** — declared here, implemented in the host. This module references **no
HTTP or SMTP package at all**.

### The finding has two sentences

> **A8** — "No customer has ever received any communication from this system, **and nothing
> records that**. There is no retry, no dead-letter, no delivery record."

Onyx's entire delivery mechanism was `_ = Task.Run(() => SendConfirmationEmail(...))` against a
method whose body threw `NotImplementedException`. Discarded with `_ =`, so the exception was never
observed.

**Almost everyone reading that finding stops after the first sentence.** The missing
implementation is the obvious defect and the cheap one — a day's work. The second sentence is why
it survived long enough to be written down in an audit: a system with a notifications table showing
ten thousand rows and zero deliveries is a system somebody fixes in an afternoon, *because
somebody looks*.

So this module is shaped around the second sentence. The status machine, the attempt log, the
published dead-letter, `GET /api/notifications/health` and the integrity check all exist so that
"we have sent nothing, ever" is a five-second discovery. Full diagnosis in
**[CONCEPTS.md §26](docs/CONCEPTS.md)**.

### Decisions

**Rendered at enqueue, not at send.** The facts arrive on an integration event; the send may
happen three seconds later or, after four failures and backoff, eight hours later. Rendering late
would mean a retried "your order shipped" email describing the order *as it is now*. Freezing the
text makes a retry byte-identical to the first attempt, which is the only version of a retry a
recipient can make sense of — the same reasoning as `Order.PlaceFrom` copying the quote verbatim
instead of recomputing it.

**"Always post-commit" is slightly the wrong phrasing.** The *decision to send* is
in-transaction — the notification row is written alongside the inbox claim, so a notification
exists if and only if the event that caused it was consumed. The *send* is post-commit, by a
separate job outside every transaction. **A9** is Onyx dispatching its payment notification before
the payment row was added to the context; the templates carry the same fix in words, because
getting the ordering right and the wording wrong would still tell a customer they had paid.

**Three outcomes, not four — and the asymmetry is the point.** Payments has an `Indeterminate`
outcome, an idempotency key, an enquiry endpoint and a reconciliation job, all to resolve a request
that was sent and never answered. None of that is here. A charge retried after a timeout bills a
customer twice; an email retried after a timeout arrives twice. **Build the guarantee the cost of
being wrong justifies** — copying the previous phase's shape wholesale would have been the wrong
instinct.

**The outbox shape, and where it stops being the outbox.** Claim, send, record, back off,
dead-letter — the same five steps `OutboxDispatcher` performs. What differs is what is being moved:
the outbox carries internal events between modules, so its rows are transient and a redelivery hits
an inbox guard. A notification is a business fact with a recipient, an audit trail and a retention
rule — and its duplicate protection is *the recipient*, who is not amused. Which is why the claim
is committed **before** the send rather than after.

**A rejection is terminal and costs no retry.** A bad address will be bad next time, and five
attempts at a blocked domain is five chances to damage a sending reputation. The same asymmetry as
a payment decline.

**The idempotency key is `(Kind, Reference)`, and choosing the reference is the work.** Uniqueness
on the reference alone would be wrong: one order legitimately produces a confirmation, a shipment
notice, a delivery notice and a cancellation. And four of the eleven consumers would be buggy with
the obvious reference — verification and reset key on the **token** (asking twice is normal, and
each request is a different link), shipment and refund key on the **event id** (both recur per
order). The pattern: *key on the thing that is unique per message, not per subject.*

**The body is sometimes a credential.** A verification or reset body holds the plaintext token,
because the email is the only thing that can deliver one — the database stores a hash. So
`ContainsSecret` is stored on the row, the logging sender redacts it, no query projects it, and
`notifications.maintenance` discards it an hour after delivery. The subject stays, so the audit
trail still says what was sent. Onyx **S14** stored reset tokens in plaintext with no expiry at
all.

**It subscribes to `PaymentDeclined` and not to `PaymentFailed`.** A decline is the provider's
considered answer and the customer must act on it. A failure may be our infrastructure, possibly
followed by a successful fourth attempt — and a failure with an unknown outcome may mean the charge
*succeeded*, in which case "your payment failed" is simply false.

**A notification failure never fails the business operation.** Every consumer logs an enqueue
failure and acknowledges the event anyway. An order that cannot be emailed about is still an order;
refusing to acknowledge would park the message in the outbox forever and block everything behind
it, which is RiverBooks' poller.

### The capability has to be reachable, not merely implemented

The roadmap asks for a logging sender in development *"so the capability is reachable and tested
rather than wired up separately per environment"* — which is **A32**:

> `CsvImportExportService.ImportProductsAsync` is implemented and tested, but **no endpoint,
> command or job calls it**. The only caller is the test project … The import half does not exist
> as a capability.

A notification path that only works once somebody has a provider account is the same shape: code
that exists, tests that pass, a README true in letter, and nothing that has ever run end to end.
`LoggingNotificationSender` means the whole chain runs on a fresh clone with no setup.

It is refused outside Development by the options validator, and the process **fails to start** if
configured there — a sender that records every message as delivered and transmits nothing is **A8**
reached deliberately. The delivery log records which sender handled each attempt, so the mistake
would be visible in the data even without the validator.

The same rule applies to the enum: every notification kind corresponds to an event some module
publishes and a consumer here that subscribes to it, asserted by a test driven from
`Enum.GetValues`. `NotificationChannel.Sms` is declared as a seam with no sender and **refuses to
render** rather than falling through to email.

### The eleven consumers are nine lines each

All the thinking — idempotency, recipient resolution, rendering, suppression when there is nowhere
to send — lives in `EnqueueNotificationCommand`. A consumer names the kind, names the reference,
and fills in the facts. Adding the twelfth is a nine-line file, which is the test of whether the
seam was drawn in the right place.

`GetCustomerSummaryQuery` has said since Phase 1 that it is *"used by Sales to confirm a
checkout's customer is real and may transact, and by Notifications to address a message"* — the
second half arrived without that contract changing.

### What this phase added elsewhere

`OrderShippedEvent` and `OrderDeliveredEvent` had existed as **domain** events since Phase 4,
raised by a guarded state machine with per-item quantities, and nothing published them because
nothing outside Sales had asked. Onyx **A31** is that its lifecycle "stops at `Paid`" and nothing
ever set `Shipped`; BoomboCard could set it and could not tell anybody. Sales gained two
integration events, two translators, and a `UnitsShipped` count on the domain event — because "3
items have shipped" is a better message than "your order has shipped", and a partial shipment is
confusing without it.

### Nothing in this phase went wrong

`BUGS-AND-LESSONS.md` gets no entry, which is worth recording rather than passing over. The two
defects this module would most plausibly have had were both avoided by having met them already:
`DispatchNotificationCommand` has exactly the shape that produced the stale-read bug
([§14](docs/BUGS-AND-LESSONS.md)) and returns the state from the command that changed it; and
`NextAttemptAtUtc` is set at creation, because Phase 4's verification script hand-built an outbox
row without one and spent a cycle wondering why nothing was delivered.

What did bite was the test suite, twice, and both times the same mistake: asserting on async work
too early, and asserting a global ordering against a per-class shared database. The second is the
Phase 3 bug ([§6](docs/BUGS-AND-LESSONS.md)) in a new costume.

### Invariants

- One notification per `(Kind, Reference)`, ever — unique at the database.
- A notification row exists if and only if the event that caused it was consumed.
- The text is frozen at enqueue, so a retry is byte-identical to the first attempt.
- Every delivery attempt leaves a row, including one whose transition is then refused.
- A rejection is terminal and consumes no retry; a transport failure backs off exponentially; the
  budget is five and then it dead-letters, publicly.
- A claim is committed before the provider is called, and a claim nobody completes is swept back to
  the queue without consuming an attempt.
- A body that contains a token is never logged, never projected, and discarded an hour after
  delivery.
- A notification failure never fails the operation that caused it.

---

## Cross-context flow

**Built and verified end to end as of Phase 4.**

An invariant never crosses a boundary. Stock correctness is Catalog's alone, so no shared
transaction is needed — sequenced transactions with a durable correlation handle instead:

```
TX 0 (Sales)    CheckoutSession, Open, ExpiresAt = now + 20m               ← the anchor
TX 1 (others)   DeductStock / ReservePromotion / RedeemPoints              ← all named by session id
TX 2 (Sales)    Order(PaymentPending) + session.Confirm(orderId)           ← 202 Accepted
```

Four layers of recovery, cheapest first:

0. **Retry** — `Deduct` is idempotent on the session id, so resending is safe and free.
1. **Inline compensation** — best effort; never relied on, since it can fail for the same
   reason TX 2 did.
2. **Session expiry sweeper** (`sales.checkout-expiry`) — the actual guarantee, because it does
   not require the failing request to survive long enough to clean up after itself. Fires the
   release unconditionally; the receiver caps it at what is actually held.
3. **Orphan reconciliation** (`sales.orphan-reconciliation`) — stock held against a session that
   does not exist, which layer 2 structurally cannot see because it enumerates sessions. Catalog
   reports the references, Sales decides what each one means, and the decision is a command back
   into Catalog. Should find nothing, and **alerts** when it does.

"Fires unconditionally and relies on idempotency" needs one qualification the first version of
this list did not have: it only works when the blind call is **capped by the receiver**. Blind
plus trusting is how you invent inventory — see [bug 13](docs/BUGS-AND-LESSONS.md).

The session must commit **first**. If it were created in TX 2, a TX 2 failure would leave stock
held against an id that exists nowhere — invisible, permanent loss of sellable inventory.

**Command or event:** command when the caller needs the answer to decide what to do next
(`DeductStock` — the customer is waiting and out-of-stock is the common outcome); event when it
does not (`RestoreStock` — nobody waits, it cannot fail for a business reason, it must be
guaranteed). Catalog accepts commands and publishes events; it listens to nobody, so the module
everything depends on depends on nothing.

**Responses describe what is committed.** `202 PaymentPending`, never `200 Paid`. Async work
moves state forward and never revokes an acknowledged fact — unlike dome-gym, which returns
`200` with the created resource and then silently rolls it back.

---

## Capabilities

| Area | Endpoints |
|---|---|
| **Auth** | register · sign in · request password reset · reset password · verify email |
| **My account** | profile · rename · change password · resend verification · sign-in attempts · addresses (add/edit/remove/set default) |
| **Customer admin** | search (keyset paged, filterable) · grant/revoke role · suspend · reinstate · anonymise |
| **Promotions** | validate a code against a basket · create · get · search (keyset paged) · reconfigure draft · activate · pause · close · campaign performance |
| **Loyalty** | balance and what it is worth · statement (keyset paged) · price a redemption · administrative adjustment |
| **Products** | create · get · search (keyset paged, filterable, low-stock) · price history · rename · set/withdraw price per country · cost price · activate · discontinue |
| **Stock** | bulk availability check · deduct (idempotent) · restore · goods-in receipt · stocktake adjustment · shrinkage write-off |
| **Exports** | request (202 + id) · poll status · download by opaque id |
| **Imports** | upload CSV, `dryRun` supported |
| **Checkout** | start (prices a basket, holds nothing) · quote with every adjustment and its basis · confirm (202, idempotent) · abandon |
| **Orders** | get with items, adjustments and lifecycle timestamps · my orders (keyset paged) · cancel · refund · ship (partial supported) · mark delivered |
| **Payments** | initiate (fixes the amount from the order) · submit with a provider token · status with every attempt and refund · my payments (keyset paged) · refund, whole or partial · provider webhook (signature-verified) |
| **Notifications** | queue health (queued / stuck / rejected / dead-lettered) · search by status and customer (keyset paged) · one notification with every delivery attempt. **Reads only** — a notification exists because an event happened, never because somebody asked |
| **Jobs** | run history, filterable by job |

## Background work

**Twenty-two** hosted services — fifteen distinct jobs plus one `OutboxDispatcher` per module —
each with a per-iteration error boundary so one bad pass cannot stop the
loop or the host — Onyx **A21** ran two jobs in one process with no boundary, so either faulting
killed both with nothing to restart them.

| Job | Does |
|---|---|
| `OutboxDispatcher` | publishes committed integration events; per-message failure handling, backoff, dead-letter |
| `ProductExportGenerator` | claims and generates exports, atomically via the version token |
| `StockIntegrityCheckJob` | asserts `SUM(movements) == StockQuantity`. **Reports, never corrects** — silently healing a discrepancy is Onyx **D5** |
| `ExportMaintenanceJob` | expires and deletes old exports; fails exports abandoned mid-generation |
| `CustomersMaintenanceJob` | deletes spent and expired credential tokens, auth attempts past retention, and processed outbox rows |
| `PromotionExpiryJob` | closes campaigns whose window has ended. **Records a fact, never creates one** — `Evaluate` already refuses an expired code, so the customer-facing path does not depend on this running. The opposite of Onyx **D5**, where the job invented stock and hid every oversell |
| `PointsExpiryJob` | writes off lapsed points. **Load-bearing**, unlike the one above: nothing else ends the liability. Still the opposite of **D5** — it only removes value the scheme already said had a deadline, and records that it did |
| `PointsIntegrityCheckJob` | asserts `SUM(ledger) == Balance`. **Reports, never corrects** — the job that makes the balance column trustworthy rather than merely present |
| `CheckoutExpiryJob` | expires sessions past their deadline. **Recovery layer 2, and load-bearing**: nothing else releases a session that simply stopped, because a customer closing the tab sends no request. It expires the session and the outbox does the releasing, so the release commits with the expiry |
| `OrphanSaleReconciliationJob` | recovery layer 3. Asks Catalog for stock held with no matching restore, asks Sales what each reference means, releases only the true orphans — and logs an error when it finds one, because it is impossible under the designed ordering |
| `SalesIntegrityCheckJob` | re-derives every order and session total from its rows. **Reports, never corrects** — a total that disagrees with its rows means the pricing pipeline has a defect, and rewriting the total would erase the only evidence of it |
| `PaymentReconciliationJob` | asks the provider what happened to requests we never got an answer for. **The only job here that corrects** — and it is not D5, because it retrieves the answer from the system of record rather than computing one. For "did this card get charged?" the provider is the authority and we are not |
| `PaymentsIntegrityCheckJob` | asserts that the refunded total equals its rows and never exceeds the capture, plus two invariants that should be structurally unreachable: a settled payment with no provider reference (**M4**) and a payment with attempts but no attempt rows (**A17**). **Reports, never corrects** |
| `NotificationDispatchJob` | sends what is due. The thing Onyx did not have — its delivery mechanism was `_ = Task.Run(...)` against a method that threw. Claim, send outside every transaction, record, back off, dead-letter |
| `NotificationMaintenanceJob` | returns claims nobody completed, and discards the rendered body of a delivered notification that contained a token. Without the first, a process killed mid-send leaves a row stuck forever — Onyx's "silently and forever" by a new road |
| `NotificationsIntegrityCheckJob` | reports the delivery figures and two invariants that should be structurally unreachable: a terminal notification with no attempt rows (**A8**), and a finished notification still holding a token past its retention window. **Reports, never corrects** — silently clearing the second would hide the retention job having stopped |

A job is **not** dispatched through the MediatR pipeline, so nothing opens a transaction for it
and nothing drains the domain events it raises. Jobs therefore call
`IUnitOfWork.SaveAndDispatchAsync`, and `SaveChangesAsync` now **throws** when called outside a
transaction with undispatched events pending. That guard exists because the omission was silent:
Catalog's jobs had been raising export events into the void for weeks, harmlessly only because
nothing yet subscribed to them — see [bug 10](docs/BUGS-AND-LESSONS.md).

Every scheduled job records a `JobRun` row — started, finished, outcome, counts, duration, error.
That is the replacement for Onyx **A7**, whose worker-status panel read `static` fields written in
a *different process* and returned null from the day it shipped.

## CSV round trip

Export and import share one column layout, so export → edit in Excel → re-import is a real loop.
Onyx advertised that and it worked in neither direction.

```
Sku,Name,Status,StockQuantity,LowStockThreshold,CostPrice,CostPriceCurrency,Price_US,Price_AE
```

- **All or nothing.** The whole file is validated first and every error is reported together; if
  any row is bad, nothing is written. Onyx **A35** saved per row, so one bad cell left a
  half-loaded catalogue.
- **Upsert on SKU.** Onyx **A36** always inserted, so a re-import duplicated the catalogue.
- **Stock is set on create only, never on update.** Stock moves only through evented movements
  with a stated reason, so `SUM(Delta) == StockQuantity` holds. A row whose stock column is
  ignored says so in its notes.
- Quoting, CRLF, BOM, InvariantCulture, and formula-injection neutralisation on export.

## API documentation

Swagger UI at `/swagger`. The document itself is generated by the built-in
`Microsoft.AspNetCore.OpenApi`, and Swashbuckle supplies only the UI — so there is one source of
truth rather than two generators that can disagree.

54 operations, 69 schemas, 11 tagged groups. What makes it useful rather than just present:

- **Real status codes per operation**, not one undocumented `200`. A write endpoint declares
  `204/400/404/409` and the `ProblemDetails` schema, so a consumer can see that
  `POST /stock/deduct` returns `409` on insufficient stock before calling it.
- **Schema descriptions from XML doc comments.** .NET 10's OpenAPI integration reads them, so the
  `<summary>` on every request and response record becomes the field documentation —
  `GenerateDocumentationFile` is on for that reason.
- **The operational contract is in the document**, not only in this README: which operations are
  idempotent and on what key, which are all-or-nothing, which return `202` and need polling, and
  that a business failure never returns `200`.
- **No anonymous response types.** `new { productId }` cannot be documented, so those became
  `CreatedProductResponse` and `RequestedExportResponse`.

Onyx also exposed an OpenAPI document — the default, with no descriptions, no declared response
types and no status codes — while its README and PR advertised a CSV import that had no endpoint
(**A32**) and retry safety it did not have (**M5**). A generated document that describes nothing
is not documentation.

## Changing the database

The module references `Microsoft.EntityFrameworkCore` and `.Relational` and **no provider**.
`AddCatalogModule` takes an `Action<DbContextOptionsBuilder>`, so the host picks the database.
Moving from SQLite to SQL Server took one package reference, one `UseSqlServer(...)` call, and a
connection string. An architecture test asserts the module stays provider-free.

**Migrations are the exception, and cannot not be.** EF generates a model snapshot that calls
provider-specific APIs (`SqlServerModelBuilderExtensions.UseIdentityColumns`, native column
types), so generated migration code does not compile without the provider referenced. Putting the
provider into the module to make that compile would make the whole seam a fiction — so migrations
live in `BoomboCard.Catalog.Migrations.SqlServer`, wired up with `MigrationsAssembly(...)`.
Supporting PostgreSQL means adding a sibling project, not editing the module.

Two provider differences the code handles explicitly, both by provider name rather than by
changing the domain:

| | SQLite | SQL Server |
|---|---|---|
| `DateTimeOffset` | cannot `ORDER BY` or range-compare it, so it is converted to UTC ticks | native `datetimeoffset` |
| Schemas | not supported; `HasDefaultSchema` is skipped | `catalog` schema, including `__CatalogMigrations` |

"Provider-agnostic" means the module does not *reference* a provider. It does not mean providers
are interchangeable.

## Enforced, not described

`ArchitectureTests` (NetArchTest) runs as a theory over every module in the registry, so adding
a module is one line and it immediately inherits all of it. It asserts the SharedKernel has no EF
Core or ASP.NET Core and no knowledge that any module exists; the
domain depends on neither infrastructure nor outer layers; persistence types and handlers are
internal to the module; Contracts carries no infrastructure; events are immutable records
carrying ids rather than aggregates; integration events live in Contracts; aggregates expose
read-only collections and have an EF constructor.

`ModuleWiringTests` asserts the composition root itself: that the container builds with scope
validation on (**A2**), that nothing is registered against an untyped per-module service, that
every command carries exactly one module marker, and that a command resolves exactly one
transaction boundary while a query resolves none.

`SourceConventionTests` scans every project under `src/` — discovered at run time, so a new module
cannot escape it — and fails the build on `DateTime.Now`/`UtcNow`,
`.Result`/`.Wait()`, `async void`, empty `catch` blocks, `_ = Task.Run(...)`, and
`EnsureCreated()` — one rule per Onyx defect a compiler accepts happily (**A29**, **A13**,
**D7**, **A16**, **A8**, **D12**).

Onyx's `ARCHITECTURE.md` made fifteen claims its own code contradicted. A document enforces
nothing; these fail CI.

Full walkthrough: **[docs/CONCEPTS.md](docs/CONCEPTS.md)** ·
bugs and diagnoses: **[docs/BUGS-AND-LESSONS.md](docs/BUGS-AND-LESSONS.md)** ·
tracing the event flow: **[docs/OBSERVABILITY.md](docs/OBSERVABILITY.md)**

## Not yet built

Full detail in **[docs/ROADMAP.md](docs/ROADMAP.md)**. Next: Reporting, admin and platform.

Deliberately deferred, with the reason recorded rather than the gap hidden:
- **Changing an email address.** It invalidates a verification that was a fact about a specific
  inbox, so it needs its own flow — issue a token to the *new* address and move the account only
  once that token comes back. A half-built version that changed the address and left
  `EmailVerifiedAtUtc` standing would be worse than its absence.
- **Refresh tokens.** An eight-hour access token with no refresh flow is the trade; a refresh
  flow would let the access token drop to minutes.
- **Credit terms.** Dropped outright rather than deferred — see the Customers decisions above.
- **A real payment provider.** The gateway is a typed `HttpClient` against a documented shape
  with retry, backoff, options validation and a credential handler — but the only implementation
  exercised so far is `StubPaymentGateway`, which is Development-only and which the options
  validator refuses to start with anywhere else. Pointing it at a sandbox is a connection string
  and an API key.
- **Shipment tracking.** Partial shipment works end to end — per-item `QuantityShipped`, the order
  status derived from the items, and a message per parcel — but there is no carrier integration and
  no tracking number. Out of scope rather than deferred: it needs a carrier account, and the
  notification it would enrich already exists.
- **A real email provider.** `HttpNotificationSender` is a typed `HttpClient` against a documented
  shape with retry, options validation and a credential handler — but the only sender exercised so
  far is `LoggingNotificationSender`, which is Development-only and which the options validator
  refuses to start with anywhere else. Pointing it at a provider is a base URL and an API key.
- **Notification channels beyond email, and notification preferences.** `NotificationChannel.Sms`
  is declared as a seam that refuses to render; a preference centre needs marketing notifications
  to exist, and every message here is transactional. Both are recorded in
  [ISSUE-COVERAGE.md](docs/ISSUE-COVERAGE.md) rather than implemented speculatively (**A45**).

**Closed in Phase 4**, both previously listed here: *orphan-sale reconciliation*, which needed
Sales to exist before Catalog's "is this reference a completed sale?" question had anywhere to go,
and the *loyalty earn trigger*, which needed `OrderPaid` to exist before the Phase 3 earn rule had
anything to subscribe to.

**Closed in Phase 6:** *notification delivery of any kind*, which `ISSUES.md` §7 lists under
Platform. Also the *email verification* and *password reset* flows, which have had working tokens
since Phase 1 and nothing that put the link in front of a customer — so an account could not be
verified, and therefore could not check out. And **A31**'s last mile: Sales could set `Shipped`
since Phase 4 and could not tell anybody.

**Closed in Phase 5:** the *payment gateway seam*. `MarkOrderPaidCommand` had documented itself
since Phase 4 as a stand-in until Payments existed, and needed no change when it arrived — the
command, its idempotency on the provider reference and its state machine were all already there.
`RestoreReason.PaymentFailed` has also finally got something able to raise it, after sitting in
`Catalog.Contracts` since Phase 0.
