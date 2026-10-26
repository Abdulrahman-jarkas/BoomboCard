# BoomboCard — Concepts and Decisions

A walkthrough of every pattern in this codebase and why it is there. Written to be read aloud:
short sentences, plain language, and for each decision a reason you can defend.

---

## Contents

1. [The 60-second overview](#1-the-60-second-overview)
2. [DDD building blocks](#2-ddd-building-blocks)
3. [Why prices live inside Product but stock movements do not](#3-why-prices-live-inside-product-but-stock-movements-do-not)
4. [Application patterns](#4-application-patterns)
5. [What `await next()` actually does](#5-what-await-next-actually-does)
6. [The transaction flow, step by step](#6-the-transaction-flow-step-by-step)
7. [Does the behaviour run again for an inner handler?](#7-does-the-behaviour-run-again-for-an-inner-handler)
8. [Concurrency: the version token](#8-concurrency-the-version-token)
9. [Idempotency: the three-column unique index](#9-idempotency-the-three-column-unique-index)
10. [Why TimeProvider](#10-why-timeprovider)
11. [EF configuration, property by property](#11-ef-configuration-property-by-property)
12. [Effective dating](#12-effective-dating)
13. [Cross-context work: outbox, saga, compensation](#13-cross-context-work-outbox-saga-compensation)
14. [Export: streams, async streams, and the generator](#14-export-streams-async-streams-and-the-generator)
15. [Import: all-or-nothing, upsert, and why it does not reuse the product handlers](#15-import-all-or-nothing-upsert-and-why-it-does-not-reuse-the-product-handlers)
16. [Background jobs and the job runner](#16-background-jobs-and-the-job-runner)
17. [Polling versus push: the transport for "is my export ready?"](#17-polling-versus-push-the-transport-for-is-my-export-ready)
18. [Smaller decisions](#18-smaller-decisions)
19. [Likely questions, short answers](#19-likely-questions-short-answers)
20. [Making the monolith multi-module](#20-making-the-monolith-multi-module)
21. [Customers: identity without an identity framework](#21-customers-identity-without-an-identity-framework)
22. [Promotions: nine lines of legacy code, six findings](#22-promotions-nine-lines-of-legacy-code-six-findings)
23. [Loyalty: a counter with no log, and what replaces it](#23-loyalty-a-counter-with-no-log-and-what-replaces-it)
24. [Selling: the seven-step quote and the three-transaction sale](#24-selling-the-seven-step-quote-and-the-three-transaction-sale)
25. [Payments: four Critical findings in one method](#25-payments-four-critical-findings-in-one-method)
26. [Notifications: the finding with two sentences](#26-notifications-the-finding-with-two-sentences)
27. [Trade-offs I would defend, and what I would change](#27-trade-offs-i-would-defend-and-what-i-would-change)

Sections 1–19 cover the SharedKernel and the Catalog module; 20 onwards were written as each
later module landed. The build order and what is still to come are in
**[ROADMAP.md](ROADMAP.md)**.

See also **[BUGS-AND-LESSONS.md](BUGS-AND-LESSONS.md)** — eleven real bugs hit while building
this, each with the diagnosis and the test that now pins it.

---

## 1. The 60-second overview

> "This is a rebuild of a legacy order system. I audited the old one first and wrote up 101
> issues, then designed the new one so the worst of them are structurally impossible rather
> than just fixed.
>
> It is a modular monolith. Each bounded context is its own set of projects with its own
> database schema, its own DbContext, and a Contracts project that is the only thing other
> modules may reference.
>
> Inside a context I use DDD: aggregates with invariants, value objects that cannot hold an
> invalid value, and domain events for everything that changes.
>
> Every command runs in one transaction. The aggregate write and all its domain event handlers
> commit together or not at all. Anything that crosses a context boundary goes through an
> outbox instead, so it is eventually consistent but guaranteed."

---

## 2. DDD building blocks

### Bounded context

**What it is:** a boundary around one part of the business, inside which words have exactly one
meaning.

The classic example is the word "payment". In Sales it means a reference and a status. In a
Payments context it means a provider transaction with attempts, retries, references, refunds and
chargebacks. Same word, two different things. That is the signal you need two contexts.

**How it shows up here:**

```
BoomboCard.Catalog             the context — everything internal
BoomboCard.Catalog.Contracts   its public surface — commands, queries, integration events
```

Other modules reference **Contracts only**. Most types inside `BoomboCard.Catalog` are
`internal`, so the compiler enforces the boundary rather than a code review.

**Four tests I used to decide a boundary:**

| Test | Question |
|---|---|
| **Language** | Does the same word mean the same thing on both sides? |
| **Lifetime** | Do they end at the same time? |
| **Change** | Do they change for the same reason? |
| **Interface** | Is the traffic between them thin and stable? |

I counted the actual messages between modules to decide. Orders ↔ Products was nine messages;
Orders ↔ Payments was five, all carrying just an id. Five thin messages is a good boundary. Nine
chatty ones is a warning.

**Say out loud:** *"A bounded context is a language boundary, not a layer. I decide where to put
one by counting the traffic across it and asking whether the two sides change for the same
reason."*

### Entity

**What it is:** something with an identity that stays the same even when all its data changes.

A product is still the same product after you rename it and change its price. So it is compared
by `Id`, not by its values.

```csharp
public abstract class Entity : IEquatable<Entity>
{
    public Guid Id { get; protected init; }

    public bool Equals(Entity? other) =>
        other is not null && other.GetType() == GetType() && other.Id == Id;
}
```

Note `other.GetType() == GetType()`. Without it, a `Product` and a `StockMovement` that happened
to share a Guid would compare equal.

`Guid` rather than `int` identity, because the domain can create a fully formed aggregate and
raise its events **before** the database is involved. With a database-generated `int`, the id is
zero until you save — so a domain event raised in a factory would carry `ProductId = 0`.

### Value object

**What it is:** something defined entirely by its values, with no identity. Compared by value,
and immutable.

$10 USD is $10 USD. There is no "which $10".

The real reason to use them is not equality — it is that **a value object cannot hold an invalid
value**. Validation happens once, in the factory, and after that every holder of the type knows
it is valid.

```csharp
public sealed class StockLevel : ValueObject
{
    public int Quantity { get; }

    private StockLevel(int quantity) => Quantity = quantity;   // private

    public static ErrorOr<StockLevel> Create(int quantity) =>
        quantity < 0 ? StockLevelErrors.Negative : new StockLevel(quantity);

    public bool CanFulfil(int requested) => requested > 0 && Quantity >= requested;
}
```

Private constructor, static factory returning `ErrorOr`. There is **no way to construct a
negative StockLevel**. "Stock never goes negative" is not a rule I have to remember to check —
it is not representable.

The value objects here and what each one buys:

| Value object | What it makes impossible | Legacy bug it closes |
|---|---|---|
| `Sku` | an unnormalised or malformed code | duplicate SKUs differing only in case |
| `StockLevel` | negative stock; a second definition of "enough" | three different availability rules that disagreed at the boundary |
| `ProductStatus` | an illegal lifecycle transition | no lifecycle at all — nothing could be withdrawn from sale |
| `Money` | an unrounded amount, or adding USD to AED | money held in `double`, totals like `83.096766` |
| `Country` | an unsupported country; a second copy of a tax rate | the same expiry date hardcoded in two files with two different values |

**On `Money`:** the legacy `Money` was a `record` with `{ get; set; }` — so it had value-type
semantics in name only. It was used exactly once, as `Money.Zero()` followed by
`shipping.Amount = ...`, which throws the currency away. Mine is immutable, rounds to cents on
every operation, and **throws** on a currency mismatch rather than returning an error, because
adding AED to USD is a programming bug, not a business outcome a caller could handle.

**Say out loud:** *"The point of a value object is not equality — it is that an invalid value is
unrepresentable. I put validation in a private constructor behind a static factory, so after
construction nobody has to check again."*

### Aggregate and aggregate root

**What it is:** a cluster of objects that must stay consistent together, treated as one unit.
The **root** is the only way in.

Two rules follow from that:

1. **Load and save the whole aggregate together.** It is one consistency boundary, so it is one
   transaction's worth of work.
2. **Nothing outside reaches past the root.** You call `product.SetPrice(...)`, never
   `product.Prices.Add(...)`.

That second rule is enforced, not just documented:

```csharp
private readonly List<ProductPrice> _prices = [];
public IReadOnlyCollection<ProductPrice> Prices => _prices.AsReadOnly();
```

The backing field is private; the public view is read-only. There is no way to add a price except
through `SetPrice`, which is where the rules live.

**The invariants `Product` protects:**

- Stock never goes negative.
- A product cannot be activated without a price in at least one country.
- Stock can only be deducted from an Active product.
- A price's currency must match its country's currency.
- At most one open price per country.
- An active product cannot have its last price withdrawn.

That second one is worth calling out. It means a product with no price can never reach a basket.
That is the structural version of "the price you see is the price you pay" — the legacy system
could charge from a price nobody had set.

**Say out loud:** *"An aggregate is a consistency boundary. The root is the only entry point, so
every change goes through a method that can check the invariants. I enforce that with a private
backing field and a read-only public collection, not with a comment."*

### Domain event

**What it is:** a record that something happened inside this context. Past tense, immutable, a
fact.

```csharp
public sealed record ProductPriceChangedEvent(
    Guid ProductId,
    string CountryCode,
    Guid? PreviousPriceId,
    decimal? PreviousAmount,
    Guid NewPriceId,
    decimal NewAmount,
    string CurrencyCode,
    DateTimeOffset EffectiveFrom,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;
```

Three deliberate choices:

**A `record`, so it is immutable.** A fact does not change after it happened.

**Ids and primitives only — never aggregate instances.** Both reference projects I studied pass
live aggregates (`GymAddedEvent(Subscription, Gym)`, `OrderCreatedEvent(Order)`). That breaks
three ways: the event cannot be serialised into an outbox; the handler can see state that moved
on after the fact was recorded; and every handler is coupled to the aggregate's full shape.

**Raised for every state change, even with no listeners.** `ProductRenamedEvent` and
`ProductCostPriceChangedEvent` have no handlers today. They fire anyway, because the event is
the record of the fact. When someone later wants a price-change notification or an audit trail,
they add a handler — they do not have to reopen the aggregate. There is a test asserting
`Rename` raises its event with nothing subscribed.

**Why `ProductPriceChangedEvent` carries both price ids:** a consumer holding a quote built on
the old price can then decide — honour it, or re-quote. One reference project cancels every
active checkout session touching a repriced product, which ejects customers mid-payment with no
explanation. Carrying both ids turns a blunt cancellation into a policy decision.

**Say out loud:** *"Domain events are facts, so they are records, past tense, carrying ids not
objects. I raise one for every state change even when nothing listens, because adding a listener
later should not mean editing the aggregate."*

### Repository

**What it is:** a collection-like interface for getting and adding aggregates. It hides the
query technology from the application layer.

The important thing about mine is what is **missing**:

```csharp
internal interface IProductRepository
{
    void Add(Product product);
    void AddMovement(StockMovement movement);
    Task<Product?> GetByIdAsync(Guid productId, CancellationToken ct = default);
    Task<List<Product>> GetByIdsAsync(IReadOnlyCollection<Guid> productIds, CancellationToken ct = default);
    Task<bool> SkuExistsAsync(Sku sku, CancellationToken ct = default);
    Task<bool> MovementExistsAsync(Guid productId, string reference, StockReason reason, CancellationToken ct = default);
}
```

**No `SaveChangesAsync`.** Both reference projects call `SaveChangesAsync` inside every
repository method — `AddGymAsync`, `UpdateAsync`, `AddSubscriptionAsync`. So a use case touching
two aggregates commits twice, and nothing can batch or roll back as one piece. The legacy system
made the same mistake without the abstraction: order creation committed N+2 separate times while
its architecture document claimed "a single logical transaction… call SaveChanges() once at the
end."

Saving is the unit of work's job. The repository tracks; the unit of work commits.

Also note `Add` is `void`, not `async`. Adding to a change tracker is an in-memory operation —
there is nothing to await.

### Unit of work

**What it is:** one object that knows about all the changes in a business operation and commits
them as one.

EF Core's `DbContext` already is one. I wrap it so the pipeline behaviour can stay
provider-agnostic:

```csharp
public interface IUnitOfWork
{
    bool HasActiveTransaction { get; }
    Task<ITransaction> BeginTransactionAsync(CancellationToken ct = default);
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
```

**Why wrap it at all?** Because `UnitOfWorkBehavior` must take exactly two generic parameters —
MediatR's open-generic registration requires `IPipelineBehavior<TRequest, TResponse>`. If the
behaviour depended on `CatalogDbContext` directly it would need a third type parameter and could
not be registered generically. The interface lets one behaviour serve every module, and each
module registers its own `EfUnitOfWork<TContext>`.

---

## 3. Why prices live inside Product but stock movements do not

This is the question that tests whether you understand aggregates, so it is worth being precise.

### The decision rule

> **Put it inside the aggregate if an invariant needs it in order to decide.
> Keep it outside if it only records what happened.**

### `ProductPrice` is inside

**Invariants need it.** Three rules cannot be checked without the prices in memory:

- "cannot activate without a price" — needs to see whether any price exists
- "at most one open price per country" — needs to see the existing open price
- "a new price must take effect after the current one" — needs the current one's dates

If prices were a separate aggregate, enforcing those would mean reading another aggregate inside
a command and hoping nothing changed in between. That is exactly the read-then-check-then-write
race we are trying to eliminate.

**The size is bounded.** Two countries, and a handful of historical versions per country. Tens
of rows. Loading them with the product costs nothing.

**They are only ever changed through the product.** `product.SetPrice(...)`. There is no use case
for "update this price" without the product's rules applying.

### `StockMovement` is outside

**No invariant needs the history.** To deduct stock you need the *current quantity*, which is on
the `Product`. You never need to read past movements to decide whether a new deduction is legal.

**The size is unbounded.** Every sale, restore, receipt and adjustment, forever. Thousands of
rows per product and growing. If movements were a collection on the aggregate, then loading a
product to sell one unit would load its entire trading history. That is a performance cliff built
into the model.

**It is append-only and immutable.** A movement is never modified, so it has no concurrency
concern of its own and nothing to protect.

**The reconciliation check is a background query, not a per-request rule.**
`SUM(Delta) == StockQuantity` runs as a sweep. It does not need to hold within a single command.

### How they connect without being one aggregate

The aggregate method **returns** the movement it implies:

```csharp
public ErrorOr<StockMovement> Deduct(int quantity, StockReason reason, string reference, DateTimeOffset nowUtc)
{
    if (!Status.IsActive)                 return ProductErrors.NotActive(Id);
    if (string.IsNullOrWhiteSpace(reference)) return ProductErrors.ReferenceRequired;

    var deducted = Stock.Deduct(quantity);
    if (deducted.IsError) return deducted.Errors;

    Stock = deducted.Value;
    RaiseDomainEvent(new StockDeductedEvent(Id, quantity, reason, reference, Stock.Quantity, nowUtc));

    return StockMovement.Create(Id, -quantity, reason, reference, Stock.Quantity, nowUtc);
}
```

Then the handler persists both in one save:

```csharp
var movement = product.Deduct(quantity, StockReason.Sale, command.Reference, nowUtc);
if (movement.IsError) return movement.Errors;

products.AddMovements(movements);
await unitOfWork.SaveChangesAsync(cancellationToken);
```

Three things this buys:

- **You cannot change stock without producing an audit row** — the method hands you one.
- **The aggregate stays free of persistence concerns** — it does not add anything to a DbSet.
- **The collection is never loaded** — the movement is created, not appended to a loaded list.

**What I deliberately did not do:** one reference project smuggles a newly created aggregate out
through a domain event handler purely to get it saved. That hides the fact that the command
writes two aggregates, and it uses an event for something that is not a fact but a unit-of-work
concern. If a command writes two aggregates, the handler should add both and save once.

**Say out loud:** *"Prices are inside because the product's invariants need them to decide, and
there are only a handful. Movements are outside because no invariant reads the history, and the
table grows forever — putting them in the aggregate would mean loading a product's whole trading
history to sell one unit. The aggregate method returns the movement so the audit row cannot be
forgotten."*

---

## 4. Application patterns

### CQRS (the light version)

Commands change state. Queries read it. They are separated because they have different needs.

| | Command | Query |
|---|---|---|
| Returns | success or an error | data |
| Goes through | the aggregate | a projection straight to a DTO |
| Transaction | yes | **no** |
| Marker | `ITransactionalRequest` | none |

This is **not** event sourcing and **not** separate read/write databases. Same tables, different
paths through them. Commands load tracked aggregates; queries use `AsNoTracking()` and project
server-side.

Note the query handler projects in SQL:

```csharp
return await context.Products
    .AsNoTracking()
    .Where(product => query.ProductIds.Contains(product.Id))
    .SelectMany(product => product.Prices.Where(/* effective now */),
        (product, price) => new ProductPricingResponse(/* … */))
    .ToListAsync(cancellationToken);
```

The legacy system's reporting job did `context.Orders.ToList()` and then filtered in memory —
linear in total order history, forever. Its customer list ran a `COUNT` query per customer inside
a `Select` over an already-materialised list, which is the classic N+1.

### Mediator (MediatR)

**What it does:** the caller sends a message; the mediator finds the handler. The caller never
references the handler's type.

**Why it matters here:** this is what lets modules talk without depending on each other's
internals. Sales sends `DeductStockCommand` — a type in `Catalog.Contracts`. The handler lives in
`BoomboCard.Catalog` and is `internal`. Sales cannot see it, cannot new it up, cannot reach past
the contract.

Two message shapes, and the difference matters a lot (see §7):

| | `IRequest<T>` → `Send` | `INotification` → `Publish` |
|---|---|---|
| Handlers | exactly one | zero or many |
| Returns | a value | nothing |
| Pipeline behaviours run? | **yes** | **no** |
| Used for | commands, queries | domain events |

### Pipeline behaviour

**What it is:** a wrapper around a handler. Same idea as ASP.NET middleware. Cross-cutting
concerns live here instead of being repeated in every handler.

```csharp
public sealed class UnitOfWorkBehavior<TRequest, TResponse>(
    IUnitOfWork unitOfWork,
    IDomainEventDispatcher dispatcher)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull, ITransactionalRequest
```

Registered once, as an open generic:

```csharp
services.AddTransient(typeof(IPipelineBehavior<,>), typeof(UnitOfWorkBehavior<,>));
```

**The generic constraint is the clever part.** `where TRequest : ITransactionalRequest` means
MediatR only applies this behaviour to requests implementing that marker. Commands get a
transaction; queries do not. No `if (isCommand)` check anywhere.

One reference project opens a transaction in ASP.NET middleware for **every** request, including
pure reads, and holds it for the whole request lifetime. The marker interface is how you avoid
that without writing conditionals.

### Result pattern (`ErrorOr`) instead of exceptions

```csharp
public ErrorOr<Success> Activate(DateTimeOffset nowUtc)
{
    if (!_prices.Any(/* … */)) return ProductErrors.CannotActivateWithoutPrice;
    // …
}
```

**Why not exceptions for business rules:**

- "No price set" is an expected outcome, not an exceptional one. Exceptions are for things you
  did not plan for.
- Exceptions are invisible in the signature. `ErrorOr<Success>` tells the caller, at compile
  time, that this can fail.
- Exceptions are expensive — stack capture and unwinding — and a failed business rule is a
  normal, frequent event.
- Errors carry a code (`Product.CannotActivateWithoutPrice`), which maps cleanly to an HTTP
  status and a problem-details response.

**When I still throw:** programming errors. `CurrencyMismatchException` when you add AED to USD.
No caller could sensibly handle that; it is a bug to fix, not a path to code.

Errors are grouped in one static class per aggregate (`ProductErrors`, `SkuErrors`,
`StockLevelErrors`) so the codes are discoverable and cannot drift.

### Marker interface

`ITransactionalRequest` has no members. Its only job is to carry information to the type system
so a generic constraint can act on it. That is a legitimate and cheap technique — it turns a
runtime decision into a compile-time one.

---

## 5. What `await next()` actually does

```csharp
var response = await next();
```

`next` is a **delegate** of type `RequestHandlerDelegate<TResponse>`. It represents *"the rest of
the pipeline"* — the next behaviour in the chain, or, if this is the last one, the actual handler.

Calling it runs everything downstream. `await` waits for that to finish. `response` is whatever
the handler returned, e.g. `ErrorOr<Guid>` from `CreateProductCommandHandler`.

The shape is a set of Russian dolls:

```
┌─ LoggingBehavior ──────────────────────────────────┐
│  code before await next()        ← on the way IN   │
│  ┌─ UnitOfWorkBehavior ─────────────────────────┐  │
│  │  BeginTransaction            ← on the way IN │  │
│  │  ┌─ CreateProductCommandHandler ──────────┐  │  │
│  │  │  load, mutate, SaveChangesAsync        │  │  │
│  │  └────────────────────────────────────────┘  │  │
│  │  DispatchUntilDrained        ← on the way OUT│  │
│  │  Commit                      ← on the way OUT│  │
│  └──────────────────────────────────────────────┘  │
│  code after await next()         ← on the way OUT  │
└────────────────────────────────────────────────────┘
```

**The key insight:** code *before* `await next()` runs on the way in. Code *after* it runs on the
way out. That is exactly why `BeginTransactionAsync` is before and `Commit` is after. The
behaviour brackets the handler.

**Say out loud:** *"`next` is a delegate for the rest of the pipeline. Awaiting it runs the inner
behaviours and then the handler. Anything I write before the await happens on the way in,
anything after happens on the way out — which is how one behaviour can open a transaction and
commit it around the handler."*

> **MediatR version note:** in MediatR 12 `RequestHandlerDelegate<TResponse>` takes no
> parameters, so it is `next()`. In MediatR 13 it takes a `CancellationToken`, so it becomes
> `next(cancellationToken)`. I hit that as a compile error and it is worth knowing which version
> you are on.

---

## 6. The transaction flow, step by step

### The requirement

A command's aggregate change **and** everything its domain event handlers do must commit together
or roll back together.

### The code

```csharp
public async Task<TResponse> Handle(
    TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
{
    if (unitOfWork.HasActiveTransaction)
    {
        return await next();                    // join the outer transaction, do not nest
    }

    await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

    var response = await next();                // the handler runs: load, mutate, SaveChanges

    await dispatcher.DispatchUntilDrainedAsync(cancellationToken);

    await transaction.CommitAsync(cancellationToken);

    return response;
}
```

### The trace, for `CreateProductCommand`

```
1. BeginTransactionAsync()
      a real database transaction opens

2. await next()  →  CreateProductCommandHandler
      Sku.Create(...)                     validate
      Product.Create(...)                 raises ProductCreatedEvent into _domainEvents
      products.Add(product)               tracked, not yet written
      products.AddMovement(opening)       tracked, not yet written
      unitOfWork.SaveChangesAsync()       INSERT rows — inside the transaction, not committed

3. DispatchUntilDrainedAsync()
      pass 1: change tracker holds Product with 1 pending event
              pop it → publish ProductCreatedEvent
              handlers run (they may mutate other aggregates)
              ChangeTracker.HasChanges() → SaveChangesAsync()
      pass 2: no pending events → return

4. CommitAsync()
      everything becomes durable, together
```

### The drain loop, and why it loops

```csharp
public async Task DispatchUntilDrainedAsync(CancellationToken cancellationToken = default)
{
    for (var depth = 0; depth < MaxCascadeDepth; depth++)
    {
        var pending = context.ChangeTracker
            .Entries<AggregateRoot>()
            .Where(entry => entry.Entity.DomainEvents.Count > 0)
            .SelectMany(entry => DomainEventAccess.Drain(entry.Entity))
            .ToList();

        if (pending.Count == 0) return;

        foreach (var domainEvent in pending)
            await publisher.Publish(domainEvent, cancellationToken);

        if (context.ChangeTracker.HasChanges())
            await context.SaveChangesAsync(cancellationToken);
    }

    throw new InvalidOperationException($"Domain event cascade exceeded {MaxCascadeDepth} levels…");
}
```

**Why a loop and not a single pass?** Because a handler can mutate another aggregate, and that
aggregate raises its own events. Example:

```
StockDeductedEvent
  → handler updates a Category aggregate's "items sold" counter
      → Category raises CategoryStatsChangedEvent      ← second-order event
```

A single pass never sees that second event. All three reference projects do one pass, and only
cascade by accident when a handler happens to call `SaveChanges` and re-enter a `SaveChanges`
override. My loop re-queries the change tracker, so it finds it.

**Why save inside the loop?** Handlers mutate aggregates but never save — that is the rule. So the
loop persists what they changed. This also leaves a clean change tracker for the next pass.

**Why the depth cap?** Two handlers that raise each other's events would loop forever while
holding a database transaction open. Ten levels is generous; exceeding it is a cycle, which is a
bug, so it throws loudly.

**Why pop the events before publishing?** `Drain` returns a copy and clears the list. If a handler
triggers another save, the already-in-flight events are gone from the aggregate, so they are not
published twice.

### Order of operations, and why

| Step | Why this order |
|---|---|
| Save, **then** publish | Handlers read persisted state. One reference project publishes *before* `SaveChangesAsync`, so a handler querying the database sees the state as it was before the command ran. |
| Publish **inside** the transaction | A failing handler rolls the command back. Another reference project dispatches after an autocommitted save, so the aggregate is already durable when a handler fails — and the caller gets a 500 for a write that stands. |
| Commit **last** | The response describes what was actually committed. A third project commits in `Response.OnCompleted`, after the client already received `200`. |
| Exceptions propagate | No swallowing. One project wraps dispatch in `catch (EventualConsistencyException) { }`, which rolls the whole transaction back and tells nobody. |

### What is deliberately *not* in the DbContext

```csharp
public sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
```

No `IPublisher`. No `IDomainEventDispatcher`. No `IHttpContextAccessor`. No `SaveChangesAsync`
override.

All three reference projects put event dispatch inside the DbContext. That turns a persistence
call into something that fans out into handlers, cross-module queries and even HTTP requests. In
one of them, calling `SaveChangesAsync` on a SQL Server context triggers a query against another
module's database and a write to MongoDB.

Here the DbContext persists. The behaviour owns the transaction. The dispatcher owns dispatch.
Three jobs, three classes.

**Say out loud:** *"One transaction per command, opened by a pipeline behaviour. The handler saves,
then I drain domain events in a loop inside that same transaction, then commit. So handlers see
saved state, their work is atomic with the command, and the HTTP response always reflects what
actually committed."*

---

## 7. Does the behaviour run again for an inner handler?

This is a sharp question with a precise answer. There are two cases.

### Case 1 — a handler listens to a domain event. **No, the behaviour does not run.**

Domain events are `INotification`, published with `publisher.Publish(...)`.

**MediatR pipeline behaviours only apply to `IRequest<T>` sent through `Send`.** Notifications go
straight to their `INotificationHandler<T>` implementations. There is no pipeline.

So if you add this:

```csharp
internal sealed class OnProductCreatedDoSomething : INotificationHandler<ProductCreatedEvent>
{
    public async Task Handle(ProductCreatedEvent notification, CancellationToken cancellationToken)
    {
        // runs with NO behaviour wrapped around it
        // → no new transaction
        // → no nested DispatchUntilDrained
        // → already inside the outer transaction
    }
}
```

…then `UnitOfWorkBehavior` does **not** run again. The handler executes inside the transaction the
outer behaviour already opened. Which is exactly what we want: its work is part of the same
commit.

```
Send(CreateProductCommand)
  └─ UnitOfWorkBehavior          ← runs (IRequest)
      └─ CreateProductCommandHandler
  └─ DispatchUntilDrained
      └─ Publish(ProductCreatedEvent)
          ├─ OnProductCreatedDoSomething        ← NO behaviour
          └─ OnProductCreatedSomethingElse      ← NO behaviour
  └─ Commit
```

### Case 2 — a handler sends a *command*. **Yes, the behaviour runs — and the guard matters.**

If a domain event handler calls `sender.Send(someCommand)`, that is an `IRequest`, so the
behaviour *does* wrap it. Without a guard it would call `BeginTransactionAsync` while a
transaction is already open, which EF does not support on one connection.

That is what this line is for:

```csharp
if (unitOfWork.HasActiveTransaction)
{
    return await next();
}
```

It detects the open transaction and **joins** it: run the inner handler, skip the nested
transaction, skip the nested dispatch, return.

Skipping the inner dispatch is correct, not lazy. Any events the inner command raised are still
sitting in the change tracker, and the **outer** drain loop picks them up on its next pass. One
loop, one place, no double-dispatch.

```
Send(OuterCommand)
  └─ UnitOfWorkBehavior          HasActiveTransaction = false → BeginTransaction
      └─ OuterHandler → SaveChanges
  └─ DispatchUntilDrained  pass 1
      └─ Publish(SomeEvent)
          └─ handler calls Send(InnerCommand)
              └─ UnitOfWorkBehavior   HasActiveTransaction = TRUE → return await next()
                  └─ InnerHandler → raises InnerEvent
  └─ DispatchUntilDrained  pass 2   ← finds InnerEvent here
  └─ Commit
```

**Say out loud:** *"Behaviours only wrap `IRequest`, not `INotification`. So a domain event handler
runs with no behaviour around it — it is already inside the command's transaction. If a handler
sends a nested command, the behaviour does run, and the `HasActiveTransaction` guard makes it
join the existing transaction instead of nesting. The inner dispatch is skipped on purpose,
because the outer drain loop will pick up its events."*

---

## 8. Concurrency: the version token

### Optimistic vs pessimistic

**Pessimistic:** lock the row, do the work, unlock. Correct but it serialises everything and can
deadlock.

**Optimistic:** do not lock. Read a version, and when writing, require the version to still be
what you read. If it changed, somebody beat you — fail and retry.

Optimistic is right here because conflicts on a single SKU are rare, and the cost of being wrong
is one cheap retry.

### What it replaces

The legacy system used a process-local `SemaphoreSlim` to guard stock, documented as preventing
overselling. Two problems: it protects one process, so it is void the moment a second instance
runs; and `Release()` sat after two possible exception exits with no `try/finally`, so **a single
throw leaked the semaphore permanently and deadlocked all stock operations for the process
lifetime.**

### Why we need to *increase* the version — the key question

You already have the first half: read the version, pass it in the `WHERE`, and if it does not
match, EF throws `DbUpdateConcurrencyException`.

So why increment it?

**Because if the version never changes, the `WHERE` clause always matches, and nothing is ever
detected.**

Walk it through with the increment removed. Stock is 3, version stays 7 forever:

```
A: SELECT → Stock=3, Version=7
B: SELECT → Stock=3, Version=7

A: UPDATE SET Stock=1 WHERE Id=@id AND Version=7   → 1 row. Version is STILL 7.
B: UPDATE SET Stock=1 WHERE Id=@id AND Version=7   → STILL MATCHES. 1 row.

Both succeeded. Stock reads 1. Four units sold from three. No error raised.
```

The predicate matched for B because nothing invalidated what B had read.

Now with the increment:

```
A: SELECT → Stock=3, Version=7
B: SELECT → Stock=3, Version=7

A: UPDATE SET Stock=1, Version=8 WHERE Id=@id AND Version=7   → 1 row.  Version is now 8.
B: UPDATE SET Stock=1, Version=8 WHERE Id=@id AND Version=7   → 0 ROWS. 7 no longer exists.
   → EF sees 0 rows affected → DbUpdateConcurrencyException
   → retry: SELECT → Stock=1, Version=8
            StockLevel.Deduct(2) on 1 → InsufficientStock → honest business error

Sold 2. Stock 1. Correct.
```

**The one-line answer:** the version is a *generation counter*. Reading captures a generation.
Writing requires that generation to still be current, and then **advances it**. Advancing it is
what makes every other reader's captured value stale. Without the advance there is nothing to go
stale, so the token detects nothing.

**Two analogies to use:**

- A door lock whose combination changes every time someone goes through. You may enter only if
  you know the current combination. If it never changed, everybody who ever learned it could get
  in forever.
- It is the same mechanism as HTTP `ETag` + `If-Match`. The ETag must change when the resource
  changes, or conditional requests are meaningless.

**Say out loud:** *"The version has to change on write, because the check is 'is the version still
the one I read'. If it never changes, that is always true and the token detects nothing. Reading
captures a generation; writing requires it to still be current and then advances it. The advance
is what invalidates every other reader's snapshot."*

### How the interceptor works

```csharp
public sealed class VersioningInterceptor : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
    {
        Bump(eventData.Context);
        return base.SavingChangesAsync(eventData, result, ct);
    }

    private static void Bump(DbContext? context)
    {
        var modified = context.ChangeTracker
            .Entries<AggregateRoot>()
            .Where(entry => entry.State is EntityState.Modified);

        foreach (var entry in modified)
            DomainEventAccess.IncrementVersion(entry.Entity);
    }
}
```

**The mechanism that makes it work:** `SavingChanges` runs *after* EF has taken its original-value
snapshot but *before* it generates SQL. So when I set `Version = Version + 1`:

- The **original** value (7) goes into the `WHERE` clause — from the snapshot.
- The **new** value (8) goes into the `SET` clause — from the current value.

Giving exactly `UPDATE … SET …, Version = 8 WHERE Id = @id AND Version = 7`.

**Why an interceptor rather than doing it in each aggregate method?** Because it cannot be
forgotten. Any method that mutates an aggregate gets this for free. Centralising it means there
is no code review rule to enforce.

**Why not use the database's own feature?** SQL Server has `rowversion`, PostgreSQL has `xmin`,
and both auto-increment. They are genuinely better — zero maintenance. But they are
provider-specific, and I wanted the module to carry no provider dependency so the host chooses
the database. A plain `uint` bumped in an interceptor is the portable equivalent.

**Both sync and async are overridden.** All three reference projects override only
`SaveChangesAsync`, so synchronous `SaveChanges` silently bypasses their event dispatch entirely.

### Where the retry goes — and why not inside the behaviour

The retry must wrap the **whole command dispatch, in a fresh DI scope**:

```
RetryPolicy (3 attempts on DbUpdateConcurrencyException)
  └─ new scope → new DbContext
      └─ UnitOfWorkBehavior → BeginTransaction
          └─ handler: reload product → Deduct → save → dispatch → commit
```

**Why not retry inside the behaviour?** After a concurrency exception the change tracker is in a
poisoned state, and worse — the aggregate instance has already had its domain events *popped* and
its state *mutated*. Reusing it would re-run `Deduct` against a stale in-memory quantity and
would lose the events. So: reload, never reuse.

(This policy is on the "not yet built" list — the mechanism it depends on is in place.)

---

## 9. Idempotency: the three-column unique index

```csharp
builder.HasIndex(movement => new { movement.ProductId, movement.Reference, movement.Reason })
    .IsUnique()
    .HasDatabaseName("UX_StockMovements_Idempotency");
```

### What idempotency means

An operation is idempotent if doing it twice has the same effect as doing it once.

We need it because outbox delivery is **at-least-once**. A message can be delivered twice — the
dispatcher published it, then crashed before marking it done. So "restore stock for session X"
*will* sometimes arrive twice. Without protection, stock inflates.

### Why these three columns, in this combination

| Column | Why it is in the key |
|---|---|
| `ProductId` | one checkout touches several products with the same reference, so each needs its own row |
| `Reference` | the correlation handle — the checkout session id. This is the actual idempotency key |
| `Reason` | **the subtle one** — see below |

**Why `Reason` must be in there.** The same session legitimately produces *two* movements for the
same product:

```
session-1, product-A, Sale          → -2     at checkout
session-1, product-A, PaymentFailed → +2     when payment fails
```

If the key were just `(ProductId, Reference)`, the restore would collide with the deduction and
be rejected. Including `Reason` lets both exist, while still blocking a **duplicate of either
one**.

### Why a database constraint and not just an application check

The handlers do check first:

```csharp
var alreadyRestored = await products.MovementExistsAsync(
    requested.Keys.First(), command.Reference, reason, cancellationToken);

if (alreadyRestored) return Result.Success;
```

That is for a clean, fast path — return success rather than throwing on a redelivery.

But **the check alone is a read-then-write race.** Two concurrent redeliveries both read "does not
exist", both proceed, both insert. Only the unique index actually prevents it. The application
check is the nice path; the constraint is the guarantee.

This is the same principle as the SKU uniqueness check — `SkuExistsAsync` for a good error
message, `UX_Products_Sku` for the actual guarantee.

### What this closes

The legacy payment flow retried the gateway three times with **no idempotency key at all**, and
its pull request asserted retries were safe because "the gateway call is idempotent on their
end" — while nothing in the request identified the payment. A timeout where the charge actually
succeeded, followed by a retry, charges the customer twice.

**Say out loud:** *"Outbox delivery is at-least-once, so handlers must be idempotent. The key is
product plus reference plus reason. Reason has to be in it because one session legitimately
produces both a sale and a restore for the same product. I check in the handler for a clean fast
path, but the unique index is the real guarantee — the check on its own is a read-then-write
race."*

---

## 10. Why TimeProvider

### The rule

**Time is an input, not an ambient fact.**

Every domain method takes the current time as a parameter:

```csharp
public ErrorOr<Success> SetPrice(Country country, Money amount, DateTimeOffset effectiveFrom, DateTimeOffset nowUtc)
```

Handlers get it from the injected `TimeProvider`:

```csharp
internal sealed class SetProductPriceCommandHandler(
    IProductRepository products, IUnitOfWork unitOfWork, TimeProvider timeProvider)
{
    var nowUtc = timeProvider.GetUtcNow();
```

### Three reasons

**1. Testability.** Price effective dates, session expiry, promo validity — none of it is
testable if the code reads the system clock directly. With `TimeProvider` you inject
`FakeTimeProvider` and advance the clock. Every test in this project uses a fixed instant:

```csharp
private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-23T09:00:00Z");
```

Those tests are deterministic. They will give the same answer in any timezone, on any day, in CI
or locally.

**2. The aggregate stays pure.** It is **told** the time, it never **asks**. One reference project
computes a session's status from `DateTime.UtcNow` inside a property getter — so the same object
returns different answers depending on when you look at it, and nothing about it is testable.

**3. UTC everywhere, explicitly.** `GetUtcNow()` returns `DateTimeOffset`, which carries its
offset. The legacy system wrote `DateTime.Now` — server *local* time — into a field named
`OrderDateUtc`, then compared it against a local day boundary in a report. Reporting days were
wrong by the UTC offset and ambiguous across daylight-saving transitions.

The legacy system actually had a `ClockHelper` wrapping `DateTime.UtcNow`. It was referenced in
**zero** places. An abstraction nobody uses is worse than none, because it looks like the problem
is solved.

**Why `TimeProvider` and not a hand-rolled `IClock`?** It is built into .NET 8+, `FakeTimeProvider`
ships in `Microsoft.Extensions.TimeProvider.Testing`, and it also abstracts timers and delays —
so background services become testable too.

**Say out loud:** *"Time is an input, not an ambient fact. Domain methods take `nowUtc` as a
parameter, and handlers get it from an injected `TimeProvider`. That means the aggregate is
deterministic and every time-dependent rule is testable — and it forces UTC, which the legacy
system got wrong by writing local time into a field named UTC."*

---

## 11. EF configuration, property by property

### `Products` table

```csharp
builder.ToTable("Products");
builder.HasKey(product => product.Id);
```

Explicit table name rather than relying on pluralisation conventions. `HasKey` on the `Guid`.

#### The concurrency token

```csharp
builder.Property(product => product.Version)
    .IsConcurrencyToken()
    .IsRequired();
```

`IsConcurrencyToken()` is what puts the column into the `WHERE` clause of every `UPDATE` and
`DELETE`, and makes EF compare rows-affected against rows-expected. Zero affected →
`DbUpdateConcurrencyException`. See §8.

#### Name

```csharp
builder.Property(product => product.Name)
    .HasMaxLength(Product.MaxNameLength)   // 200
    .IsRequired();
```

`HasMaxLength` matters for two reasons. It becomes `varchar(200)` instead of `nvarchar(max)` —
which can be indexed and does not get stored off-row. And the constant comes from the domain, so
the database limit and the validation rule cannot drift apart.

#### Sku — a **value converter**

```csharp
builder.Property(product => product.Sku)
    .HasConversion(sku => sku.Value, value => Sku.Create(value).Value)
    .HasColumnName("Sku")
    .HasMaxLength(Sku.MaxLength)
    .IsRequired();
```

Two lambdas: how to write it (`sku => sku.Value`) and how to read it back
(`value => Sku.Create(value).Value`).

**Why a converter rather than an owned type?** Because `Sku` is a single value, I need a **unique
index** on it, and I compare it directly in queries:

```csharp
context.Products.AnyAsync(product => product.Sku == sku, ct)
```

A converter keeps it as one plain column on the entity, so indexing and equality translate to SQL
cleanly.

**The honest trade-off:** `Sku.Create(value).Value` throws if the stored value is invalid, because
`ErrorOr.Value` on an error state throws. That is acceptable — data already in the database passed
validation on the way in — but it is a latent throw and worth naming rather than hiding.

#### The unique index

```csharp
builder.HasIndex(product => product.Sku)
    .IsUnique()
    .HasDatabaseName("UX_Products_Sku");
```

Combined with `Sku` normalising to upper case, "ONX-001" and "onx-001" are the same value and
collide. There is a test that asserts the database rejects the duplicate.

Named explicitly — `UX_` for unique, `IX_` for non-unique — so migrations are readable and an
index can be discussed by name.

#### Status and Stock — **owned types**

```csharp
builder.OwnsOne(product => product.Status, status =>
{
    status.Property(value => value.Value)
        .HasColumnName("Status")
        .HasConversion<string>()
        .HasMaxLength(20)
        .IsRequired();
});
builder.Navigation(product => product.Status).IsRequired();
```

`OwnsOne` means "this object has no identity of its own; map its properties into **this** table."
No join, no separate table — `ProductStatus` becomes a `Status` column on `Products`.

`HasConversion<string>()` stores the enum as `"Active"` rather than `1`. Deliberate: the table is
readable without a lookup, and inserting a new enum member in the middle cannot silently
reinterpret existing rows.

`builder.Navigation(...).IsRequired()` tells EF the owned object is never null, so the columns are
non-nullable.

```csharp
builder.OwnsOne(product => product.Stock, stock =>
{
    stock.Property(level => level.Quantity).HasColumnName("StockQuantity").IsRequired();
});
```

Same idea. `StockLevel` becomes a single `StockQuantity` integer column.

**Why `OwnsOne` here but a converter for `Sku`?** Honest answer: either works for a single-value
value object, and this is a judgement call. I used `OwnsOne` for `Status` and `Stock` for
consistency with `Money` — which has two properties and therefore *must* be an owned type — and
a converter for `Sku` because it needs a unique index and direct query equality. If an
interviewer pushes, the defensible position is: **converters for single values you index or
query; owned types for multi-property values.**

#### CostPrice — an optional owned type

```csharp
builder.OwnsOne(product => product.CostPrice, money =>
{
    money.Property(value => value.Amount).HasColumnName("CostPriceAmount");
    money.Property(value => value.CurrencyCode).HasColumnName("CostPriceCurrency").HasMaxLength(3);
});
```

Note there is **no** `.IsRequired()` on this navigation — `CostPrice` is nullable, so both columns
are nullable and EF treats all-null as "no value".

`Money` is two properties — amount and currency — so it genuinely needs an owned type. And the
currency is stored **next to** the amount rather than derived, so a historical row is
self-describing.

#### The prices collection

```csharp
builder.HasMany(product => product.Prices)
    .WithOne()
    .HasForeignKey(price => price.ProductId)
    .OnDelete(DeleteBehavior.Cascade);

builder.Metadata
    .FindNavigation(nameof(Product.Prices))!
    .SetPropertyAccessMode(PropertyAccessMode.Field);
```

`.WithOne()` with no argument: `ProductPrice` has no navigation back to `Product`. Deliberate —
a child inside an aggregate does not need to point at its root, and leaving it out prevents code
navigating upward from a price.

`Cascade` on delete is safe here precisely because prices are *inside* the aggregate. They have
no independent existence.

`SetPropertyAccessMode(PropertyAccessMode.Field)` is the line that makes encapsulation work.
`Prices` is `IReadOnlyCollection` over a private `_prices` field. Without this, EF would try to
write through the read-only property and fail. With it, EF reads and writes `_prices` directly
while application code still only sees the read-only view.

#### No category index, and the reason this paragraph used to claim otherwise

This section documented an `IX_Products_CategoryId` index, with a code snippet. `Product` has no
`CategoryId`, `ProductConfiguration` has no such index, and the snippet would not compile.

It was a **BoomboCard X-number** — a design document describing code nobody wrote — found while
deciding how promotion scope should work in Phase 2, which is the first thing that had a reason
to care whether products have categories. It is kept here rather than quietly deleted because
the point of X1–X15 is that this happens to everyone, including to a codebase whose whole thesis
is that it must not.

The fix was to correct the document, **not** to add a `Category` to `Product`. Categories are on
the capability-gap list in `ISSUES.md` §7 — a backlog item, not something built — and adding a
property purely to make a stale paragraph true would be the speculative generality **A45** is
about. Promotion scope is therefore a set of product ids, and category scope waits for Catalog
to actually have categories. See §21.

The general rule that *was* correct: foreign-key-like columns that get filtered on need an
index. Onyx had **no index anywhere** (**D11**), including on the email column used by every
single login — which was also executed as hand-built raw SQL (**S1**).

### `ProductPrices` table

```csharp
builder.Property(price => price.CountryCode).HasMaxLength(2).IsRequired();
builder.Property(price => price.EffectiveFrom).IsRequired();
builder.Property(price => price.EffectiveTo);               // nullable = currently open
```

`EffectiveTo` is deliberately nullable with no `IsRequired`. Null means "this is the current
price". See §12.

```csharp
builder.OwnsOne(price => price.Amount, money =>
{
    money.Property(value => value.Amount).HasColumnName("Amount").IsRequired();
    money.Property(value => value.CurrencyCode).HasColumnName("CurrencyCode").HasMaxLength(3).IsRequired();
});
```

`Money` as an owned type nested inside an entity that is itself a child of the aggregate. EF
handles the nesting; the columns land on `ProductPrices`.

#### The lookup index — and why the column order

```csharp
builder.HasIndex(price => new { price.ProductId, price.CountryCode, price.EffectiveFrom })
    .HasDatabaseName("IX_ProductPrices_Lookup");
```

This index exists for exactly one query — resolving "the price of these products, in this
country, right now":

```sql
WHERE ProductId IN (…) AND CountryCode = 'AE'
  AND EffectiveFrom <= @now AND (EffectiveTo IS NULL OR EffectiveTo > @now)
```

**The column order is the design.** The rule is **equality columns first, range column last.**

- `ProductId` — equality (`IN`)
- `CountryCode` — equality (`=`)
- `EffectiveFrom` — **range** (`<=`)

A composite index is sorted by its first column, then the second within that, then the third. So
the database can seek directly to the matching `(ProductId, CountryCode)` group and then scan a
contiguous range of `EffectiveFrom` within it.

Put the range column first and that breaks: the index is sorted by `EffectiveFrom` globally, so
the rows for one product in one country are scattered throughout it and the database has to read
far more than it needs.

This is also the **leftmost-prefix rule**: an index on `(A, B, C)` can serve queries filtering on
`A`, on `A+B`, or on `A+B+C` — but not on `B` alone.

### `StockMovements` table

```csharp
builder.Property(movement => movement.ProductId).IsRequired();
builder.Property(movement => movement.Delta).IsRequired();
builder.Property(movement => movement.QuantityAfter).IsRequired();
builder.Property(movement => movement.OccurredAtUtc).IsRequired();

builder.Property(movement => movement.Reason).HasConversion<string>().HasMaxLength(20).IsRequired();
builder.Property(movement => movement.Reference).HasMaxLength(100).IsRequired();
```

`Delta` is **signed** — negative for a deduction, positive for a restore. That is what makes
`SUM(Delta)` meaningful as a reconciliation check.

`QuantityAfter` is denormalised on purpose. It records the resulting quantity, so the log can be
audited at any single row without replaying the whole history from the beginning.

`Reason` as a string for the same readability reason as `Status`.

`Reference` is `IsRequired` — enforced in the domain too (`ProductErrors.ReferenceRequired`),
because it is both the audit trail and the idempotency key. A movement with no reference is
untraceable.

#### Three indexes, three jobs

```csharp
// 1. idempotency — see §9
builder.HasIndex(m => new { m.ProductId, m.Reference, m.Reason }).IsUnique();

// 2. the orphan-reconciliation sweep
builder.HasIndex(m => new { m.Reason, m.OccurredAtUtc });

// 3. "show me everything this session did"
builder.HasIndex(m => m.Reference);
```

Index 2 serves the background sweep looking for `Sale` movements older than the session lifetime
with no matching restore. Equality on `Reason` first, range on `OccurredAtUtc` second — same rule
as above.

Index 3 is a single-column index for support queries by session id. It is *not* redundant with
index 1, because of the leftmost-prefix rule: index 1 leads with `ProductId`, so it cannot serve a
query that filters on `Reference` alone.

### Conventions, applied once

```csharp
protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
{
    configurationBuilder.Properties<decimal>().HavePrecision(18, 2);
    configurationBuilder.Properties<string>().HaveMaxLength(500);
}
```

`HavePrecision(18, 2)` — 18 total digits, 2 after the decimal point — applied to **every**
`decimal` in the model, so no money column can be added without it.

The legacy system configured precision nowhere. SQLite tolerated that, and its pull request
claimed moving to PostgreSQL was "a one-line change in `Program.cs`" — where it would silently
have changed rounding behaviour on every monetary column.

The string default is a safety net: a forgotten `HasMaxLength` gets 500 rather than `max`.

### Schema per module

```csharp
modelBuilder.HasDefaultSchema("catalog");
modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());
```

Each module owns a schema. One physical database, logically separated — so a module's tables are
visibly its own, and extracting it later is a smaller job.

`ApplyConfigurationsFromAssembly` auto-discovers every `IEntityTypeConfiguration` in the
assembly, so adding an entity means adding one configuration file, not also editing
`OnModelCreating`.

---

## 12. Effective dating

```csharp
public DateTimeOffset EffectiveFrom { get; private init; }
public DateTimeOffset? EffectiveTo { get; private set; }

public bool IsEffectiveAt(DateTimeOffset at) =>
    EffectiveFrom <= at && (EffectiveTo is null || EffectiveTo > at);
```

**The pattern:** a row is valid over a half-open interval `[from, to)`. `null` for `to` means
"still current".

**Why half-open?** `EffectiveTo > at`, not `>=`. So when one price ends at exactly the moment the
next begins, there is no instant where two prices are both valid and no instant where none is.
Closed intervals produce an overlap bug at the boundary.

**Why supersede instead of overwrite?**

```csharp
existing.SupersedeAt(effectiveFrom);        // close the old one
var price = ProductPrice.Create(Id, country, amount, effectiveFrom);   // open a new one
_prices.Add(price);
```

An old price is a **historical fact**. Invoices, disputes and completed quotes all refer to it. If
you overwrite the row, you have retroactively changed what a customer was charged, and a
reprinted invoice from March shows today's price.

**Three things this gives for free:**

- **Price history** — "what did this cost in March?" becomes a query. The legacy system had none.
- **Scheduled price changes** — set `EffectiveFrom` in the future. No midnight job mutating rows.
- **A provable quote** — the checkout quote stores the `ProductPriceId` it used, so the system can
  *prove* what the customer was shown rather than asserting it.

That last point is the whole answer to the legacy system's worst bug: browsing read a
never-invalidated in-memory cache while ordering read the live row, so after any price change a
shopper saw one price and was charged another — and because order confirmation emails silently
failed, nobody ever found out.

**Say out loud:** *"Prices are effective-dated over a half-open interval, and changing a price
closes the old row and opens a new one rather than overwriting. That gives price history,
scheduled changes, and a price id the checkout quote can store — so we can prove what the
customer was shown."*

---

## 13. Cross-context work: outbox, saga, compensation

The outbox table, the writer and the dispatcher background service are all in — the
dispatcher is walked through in §16. The saga and its compensation layers are designed and
partly built.

### The rule

> **An invariant never crosses a context boundary.** If two contexts must change together
> atomically, either the invariant belongs to one of them, or the boundary is wrong.

Stock passes that test: "never oversell" is a statement about Catalog's own data. Catalog enforces
it alone, in its own transaction. The order's existence is irrelevant to whether stock is correct.

So instead of one distributed transaction: **sequenced transactions, a durable correlation handle,
idempotent steps, and a deadline.**

### The flow

```
TX 0 (Sales)    CheckoutSession, status Pending, ExpiresAt = now + 20m   ← the anchor
TX 1 (Catalog)  DeductStockCommand(reference: sessionId)                 ← oversell now impossible
TX 2 (Sales)    session.Confirm() + Order(PaymentPending) + outbox row
                → 202 Accepted { orderId, status: PaymentPending }
```

**The session must commit first.** This is the subtle part. If the session were created in TX 2,
then a TX 2 failure would leave stock deducted against a session id that exists nowhere — nothing
could ever find it. Invisible, permanent loss of sellable inventory. Committing the session first
makes every intermediate state findable.

### Four layers of recovery, cheapest first

| Layer | Mechanism | Covers |
|---|---|---|
| **0** | **Retry.** `Deduct` is idempotent on the session id, so resending the same checkout is safe and free. | most transient TX 2 failures |
| **1** | **Inline compensation** in a `catch`. Best effort — never relied on, because it can fail for the same reason TX 2 did. | fast cleanup |
| **2** | **Session expiry sweeper.** Fires `Restore(sessionId)` unconditionally; idempotency makes it a no-op when nothing was held. | **the actual guarantee** |
| **3** | **Orphan reconciliation.** `Sale` movements with no matching restore, older than the session lifetime. Should find nothing, and should **alert** when it does. | bugs, not failures |

Layer 2 is why the sweeper does not need to track whether stock was actually deducted. It restores
blindly, and idempotency absorbs the no-op. **Idempotency removes the need to track distributed
state** — that is the trick that keeps this simple.

### Outbox pattern

```csharp
public void Enqueue(IIntegrationEvent integrationEvent)
{
    var message = OutboxMessage.Create(
        integrationEvent.EventId,
        type.AssemblyQualifiedName ?? type.FullName ?? type.Name,
        JsonSerializer.Serialize(integrationEvent, type, SerializerOptions),
        integrationEvent.OccurredAtUtc);

    context.Set<OutboxMessage>().Add(message);     // same change tracker, same transaction
}
```

**The problem it solves:** you need to change state *and* tell someone. Two separate systems. If
you write the database then publish, a crash in between loses the message. If you publish then
write, a failed write means you announced something that did not happen.

**The solution:** write the message into **the same database, in the same transaction**, as a row.
A background dispatcher reads and publishes it afterwards. The state change and the intent to
publish are now atomic.

**Why co-location matters:** one reference project keeps its email outbox in MongoDB while the
aggregate is in SQL Server. Two stores, no shared transaction — so an order can commit with no
queued email, or an email can be queued for an order that rolled back. That is a durable queue,
not an outbox. The defining property of an outbox is the shared transaction.

**Attempt tracking and dead-lettering:**

```csharp
public void MarkFailed(string error, DateTimeOffset nowUtc, int maxAttempts)
{
    AttemptCount++;
    LastError = error.Length > 2000 ? error[..2000] : error;

    if (AttemptCount >= maxAttempts)
    {
        Status = OutboxStatus.DeadLettered;      // stop blocking the queue, become visible
        ProcessedAtUtc = nowUtc;
        NextAttemptAtUtc = null;
        return;
    }

    var backoffSeconds = Math.Min(Math.Pow(2, AttemptCount), 300);
    NextAttemptAtUtc = nowUtc.AddSeconds(backoffSeconds);
}
```

That exists because a reference project's poller takes the first unprocessed row and never marks
it on failure. So **one permanently failing message is retried forever and blocks every message
behind it.** Exponential backoff capped at five minutes, then dead-letter after the attempt budget
so it becomes visible rather than invisible.

### Domain event vs integration event

| | Domain event | Integration event |
|---|---|---|
| Scope | inside one context | across contexts |
| Visibility | `internal`, in `Domain/` | `public`, in `*.Contracts` |
| Carries | ids and primitives | flattened DTOs |
| Delivery | in-process, **same transaction** | **outbox** row, published after commit |
| On failure | rolls the command back | retried, then dead-lettered |
| Guarantee | exactly once, atomic | at-least-once → **handlers must be idempotent** |

And an explicit **translator** bridges them:

```csharp
internal sealed class OnProductPriceChangedEnqueueIntegrationEvent(IOutboxWriter outbox)
    : INotificationHandler<ProductPriceChangedEvent>
{
    public Task Handle(ProductPriceChangedEvent notification, CancellationToken cancellationToken)
    {
        outbox.Enqueue(new ProductPriceChangedIntegrationEvent(/* flattened */));
        return Task.CompletedTask;
    }
}
```

The internal domain event can change freely; the published contract is stable. The translator
does two things only — flatten, and enqueue. **No I/O, no calls into other contexts**, because it
runs inside the command's transaction. The legacy system held a database transaction open across
three payment-gateway HTTP calls, which on a single-writer database blocked every write in the
system for up to five minutes per request.

### Command or event?

> **Command when the caller needs the answer to decide what to do next. Event when it does not.**

| | `DeductStock` | `RestoreStock` |
|---|---|---|
| Shape | **command** | **event** → outbox |
| Why | customer is waiting; out-of-stock is the common outcome; needs an immediate `409` | nobody waits; cannot fail for a business reason; must be guaranteed, not fast |

**And the dependency direction:** Catalog **owns** `DeductStockCommand` in its Contracts, so Sales
referencing it points Sales → Catalog. If Catalog instead *subscribed* to a
`CheckoutSessionCreated` event, the dependency would point Catalog → Sales — making the module
everything else depends on depend on one of its own consumers. Catalog accepts commands and
publishes events. It listens to nobody.

### Responses describe what is committed

`202 PaymentPending`, never `200 Paid`.

The pattern works because of what the response *means*. "We have received your order" is true and
durable at the moment it is said. Payment then happens asynchronously, and failure moves the order
to a **modelled state** — `PaymentFailed` — that the customer can see. Nothing acknowledged is
ever revoked.

Contrast a reference project that returns `200` with the created resource and then, in
`Response.OnCompleted`, silently rolls the whole transaction back. The client holds an id that
will `404` forever.

Also: a failure notification must be an **outbox row**, not a call. "We will email them if it
fails" is only true if the email is durable.

---

## 14. Export: streams, async streams, and the generator

### The problem, stated plainly

An export is **work whose size is set by the data, not by the request.** Five products is a
20 ms response. Fifty thousand products with prices in two countries is a few hundred thousand
values, several seconds to several minutes of CPU and I/O, and tens of megabytes of text.

Onyx generated the file inline inside the request handler. That has four separate failure modes,
and they are all the same mistake wearing different hats:

| Failure | Why |
|---|---|
| **Timeout** | The client, the reverse proxy and the load balancer each have an idle timeout (often 30–60 s). A 90-second export fails at the proxy, and the server keeps working on a response nobody will read. |
| **Memory** | A synchronous build means the whole CSV exists in RAM before the first byte is sent. Fifty thousand rows of string concatenation is also O(n²) allocation, because every `+=` copies everything accumulated so far. |
| **No retry** | If the connection drops at 85 s, the work is gone. Nothing to resume, nothing to re-fetch, and no record that it was ever attempted. |
| **Deploy kills it** | A rolling deploy drains in-flight requests. Anything long-running is cancelled by definition. |

So the export is split in two: a **record** created synchronously, and **work** that happens later.

```
POST /api/catalog/exports                → 202 Accepted { exportId }    (one INSERT, ~5 ms)
GET  /api/catalog/exports/{id}           → { status, rowCount, isDownloadable }
GET  /api/catalog/exports/{id}/download  → 200 text/csv
```

The 202 is the correct status code and it is doing real work: it means *accepted, not completed*.
`Location: /api/catalog/exports/{id}` tells the client where to look. The response describes
something **already committed** — the `ProductExport` row exists before the client is told its id,
which is the same rule the rest of this codebase follows (§13, "responses describe what is
committed").

### Why `ProductExport` is an aggregate and not a filename

Onyx returned a filename and had a second endpoint that read it back by that filename. That one
decision produced five bugs, and modelling the export as an aggregate with a lifecycle closes all
five:

- **Path traversal (S6).** `Path.Combine(dir, fileName)` with the raw query string, so
  `?fileName=../../appsettings.json` returned the file holding the payment key, the production
  database password and the JWT signing key. Here the client holds an opaque `Guid`; `StoragePath`
  is server-owned and **no part of it is ever accepted from a request**.
- **Collisions (A38).** `products-{yyyyMMddHHmmss}.csv` means two exports in the same second
  overwrite each other. The id is the identity now, and the filename embeds it.
- **Unbounded growth (A38).** Files accumulated forever inside the deployment directory — which is
  also wiped on redeploy, invalidating every filename a client was holding. `ExpiresAtUtc` gives a
  reaper something to act on, and `StorageDirectory` is configured *outside* the app directory for
  exactly that reason.
- **No status.** Generation was inline, so a slow export was a slow request.
- **No failure state.** An export that threw left nothing behind — the user simply never got a
  file and had no way to find out why.

The lifecycle is a state machine, and every transition is guarded inside the aggregate:

```
Request()  →  Requested
                 │  BeginGenerating()   — only from Requested
                 ▼
             Generating
                 ├─ Complete(file, rows)  → Ready   (sets ExpiresAtUtc = now + 7d)
                 └─ Fail(reason)          → Failed
             Ready
                 └─ Expire()              → Expired (carries StoragePath for the reaper)
```

`IsDownloadable(at)` is the single predicate the download endpoint asks: `Ready`, **and** a storage
path exists, **and** it has not expired. One method, used by both the status response and the
download, so the two can never disagree.

### Streams — what they actually are

A `Stream` in .NET is an abstraction over **a sequence of bytes you move through a small window at
a time.** It is not a container. It has no length you must respect, no indexer, and no requirement
that the whole thing exists anywhere at once. That is the entire point.

Three stream types matter here, and they compose:

```csharp
await using (var file = File.Create(storagePath))          // Stream — bytes to disk
{
    rowCount = await ProductCsvWriter.WriteAsync(file, StreamRowsAsync(context, ct), ct);
}
```

```csharp
await using var textWriter = new StreamWriter(destination, leaveOpen: true);  // chars → bytes
await using var csv        = new CsvWriter(textWriter, configuration);        // fields → chars
```

| Layer | Converts | Buffer |
|---|---|---|
| `FileStream` (`File.Create`) | `byte[]` → disk sectors | ~4 KB, then one write syscall |
| `StreamWriter` | `char`/`string` → `byte[]` via an encoding (UTF-8 here) | ~1 KB of chars |
| `CsvWriter` | fields → one quoted, escaped, delimited line | one record |

**Each layer holds a small buffer and hands the layer below a full buffer at a time.** So the
memory footprint of writing a 400 MB CSV is roughly: one row in flight, plus a few kilobytes of
buffers. Constant, not proportional to the catalogue. That is the property Onyx's
`result += $"{p.Sku},{p.Name}…"` destroyed in two ways at once — the whole file lived in memory,
*and* every row re-copied everything written so far.

Two details in that code that are easy to get wrong:

**`leaveOpen: true`.** By default, disposing a `StreamWriter` also disposes the stream underneath
it. `ProductCsvWriter.WriteAsync` does **not** own the `Stream` it was handed — the caller opened it
and the caller's `await using` closes it. `leaveOpen: true` says "flush me, do not close my
destination". Without it the writer closes a `FileStream` the caller then disposes again, and —
worse — a caller that passed an HTTP response body or a `MemoryStream` it still needed would find it
closed underneath them. **Rule: whoever opens a stream closes it.**

**The two flushes, in order.**

```csharp
await csv.FlushAsync();                          // CsvWriter's pending record → StreamWriter
await textWriter.FlushAsync(cancellationToken);  // StreamWriter's chars      → FileStream
```

Buffered layers must be flushed **outermost first**, because each one pushes into the next. Flush
the `StreamWriter` before the `CsvWriter` and you flush an empty buffer, then leave the last rows
sitting in the CSV writer. The `FileStream` itself is flushed by its own `await using` on the way
out. This is also why a truncated export file is almost always a missing or mis-ordered flush, not
a disk problem.

### Async streams — `IAsyncEnumerable<T>` and `yield return`

The other half of constant memory is the **source** of rows. The generator does not load products
and then write them; it writes them as they arrive from the database.

```csharp
private static async IAsyncEnumerable<ProductExportRow> StreamRowsAsync(
    CatalogDbContext context,
    [EnumeratorCancellation] CancellationToken cancellationToken)
{
    var query = context.Products
        .AsNoTracking()
        .OrderBy(product => product.Sku.Value)
        .Select(product => new { /* flat projection, prices included */ })
        .AsAsyncEnumerable();

    await foreach (var row in query.WithCancellation(cancellationToken))
    {
        yield return new ProductExportRow { /* mapped */ };
    }
}
```

**What `yield return` does.** The compiler rewrites this method into a state machine class. The body
does not run when you call it — it runs in slices, one slice per `MoveNextAsync()` from the
consumer. Each `yield return` hands one item back and **suspends**, remembering exactly where it was
(which locals, which point in the loop). The next `MoveNextAsync` resumes there.

Combined with `async`, `IAsyncEnumerable<T>` is "a sequence where *getting the next item* is itself
awaitable". That is precisely a database cursor: each `MoveNextAsync` may need another network round
trip for the next block of rows.

So the shape of the whole export is:

```
CsvWriter asks for a row
   → StreamRowsAsync resumes, awaits the EF reader
      → SQL Server sends the next row off an open cursor
   → one ProductExportRow is materialised, mapped, written
   → buffers fill; every ~4 KB one write syscall hits the disk
   → everything is garbage, collectable immediately
```

**One row of the catalogue is alive at a time.** Not one page, not one batch — one row.

The pieces that make that work, each doing something specific:

- **`AsNoTracking()`** — without it, EF's change tracker keeps a reference to every entity it
  materialises, so the "streaming" read quietly becomes a full load into the identity map. This
  single call is the difference between O(1) and O(n) memory, and it is the most common way a
  streaming export turns out not to be streaming.
- **A flat anonymous `Select`** — the projection runs in SQL. Only the needed columns are read, and
  `Prices` comes back with the row rather than as an N+1 lazy load per product. Compare Onyx A19:
  `context.Orders.ToList()` and then filter in memory.
- **`AsAsyncEnumerable()`** — tells EF *not* to buffer. `ToListAsync()` drains the reader into a
  list; `AsAsyncEnumerable()` leaves the reader open and yields as you pull.
- **`OrderBy(Sku)`** — a stable, deterministic order, so two exports of unchanged data are
  byte-identical and diffable, and the Excel round trip is predictable. It is served by the unique
  index on `Sku`, so the sort is free.
- **`[EnumeratorCancellation]`** — the subtle one. An iterator method's `CancellationToken`
  parameter is captured *when the method is called*, but `await foreach` supplies a token *when
  enumeration starts* (via `WithCancellation`). This attribute tells the compiler to wire the
  caller's token into the parameter. Without it, `WithCancellation` on the outside has **no effect
  on this method's body** — a cancelled export that keeps reading the database.

**The cost of streaming, stated honestly:** the database connection and its cursor stay open for the
entire write. A 10-minute export holds a connection for 10 minutes. That is an acceptable trade for
a single background generator capped at 10 exports per pass; it would not be acceptable for a
per-request export, which is another reason the work lives in a background service. If it became a
problem, the fix is keyset pagination over `Sku` — read 5,000 rows, close the reader, write them,
open a new reader after the last SKU — trading a slightly larger footprint for a connection that is
free between pages.

### CSV writing: the three bugs that quoting fixes

Onyx built its CSV with `result += $"{p.Sku},{p.Name},{p.Price},{p.StockQuantity}\n"`.

1. **No quoting (A33).** A product named `Widget, large` shifts every column after it — for that row
   and silently for the whole file if anything downstream counts columns. Product names came from
   import, so they were free text. `ShouldQuote = _ => true` quotes everything: slightly larger
   files, zero ambiguity.
2. **Formula injection (A33).** A product named `=cmd|'/c calc'!A1` **executes** when the file is
   opened in Excel. `Neutralise` prefixes a leading `=`, `+`, `-`, `@`, tab or CR with an apostrophe
   so Excel treats the cell as text. This is the one that turns "our export is ugly" into "whoever
   can name a product can run code on the laptop of whoever opens the export".
3. **Ambient culture.** `89.99` in a German locale formats as `89,99` — a decimal comma inside a
   comma-separated file. `CultureInfo.InvariantCulture` everywhere, plus an explicit `"0.00"`
   format, so money always has two decimals and the file does not depend on the server's regional
   settings. **Invariant culture for machine-readable formats, always.**

The column layout is fixed and includes **one price column per configured country** (`Price_US`,
`Price_AE`), derived from `Country.All`. Adding a country changes the export and the import at once,
because the importer reads the same list. The two are mirror images on purpose: *export → edit in
Excel → re-import* is advertised, so it has to actually work in both directions.

### `GenerateOneAsync`, line by line

This is the method worth walking through, because almost every line is a specific failure being
designed out.

```csharp
await using var scope = scopeFactory.CreateAsyncScope();
```

**A scope per export.** `BackgroundService` is a **singleton**; `DbContext`, repositories and
`IUnitOfWork` are **scoped**. A singleton cannot take a scoped dependency in its constructor — it
would capture one `DbContext` for the lifetime of the process, which is a non-thread-safe object
with an ever-growing change tracker. So the service takes `IServiceScopeFactory` and creates a scope
per unit of work. A scope *per export* rather than per pass also means one export's failure cannot
poison another's change tracker, and each gets a clean identity map.

(Contrast Onyx's `StockReconciliationJob`:
`Parallel.ForEach(products, p => ReplenishAsync(context, p))` over a **single shared `DbContext`**
from an `async void` method — concurrent use of a non-thread-safe object, a connection disposed while
work was still using it, success logged before any work ran, and any exception terminating the whole
worker process. D6, D7, A22.)

```csharp
var claim = export.BeginGenerating(timeProvider.GetUtcNow());
if (claim.IsError) return;
try { await unitOfWork.SaveChangesAsync(ct); }
catch (DbUpdateConcurrencyException) { /* claimed by another generator */ return; }
```

**The claim, and why it is atomic.** With two instances running, both generators see the same
`Requested` export. Both would generate it, both would write a file, and one would overwrite the
other's `Complete`. The fix is not a lock — it is the **version concurrency token** every aggregate
already has (§8). The UPDATE is `… WHERE Id = @id AND Version = @expected`, so the loser affects zero
rows and EF throws `DbUpdateConcurrencyException`. The loser logs at `Debug` and returns; this is a
normal race, not an error. **The existing concurrency mechanism doubles as a work-claiming
mechanism — no new machinery.**

```csharp
var fileName = $"products-{export.RequestedAtUtc:yyyyMMdd-HHmmss}-{export.Id:N}.csv";
```

A human-readable timestamp **and** the id. The timestamp is for whoever is looking in the directory;
the id is what makes it unique (A38).

```csharp
await using (var file = File.Create(storagePath))
{
    rowCount = await ProductCsvWriter.WriteAsync(file, StreamRowsAsync(context, ct), ct);
}
var completed = export.Complete(fileName, storagePath, rowCount, now);
await unitOfWork.SaveChangesAsync(ct);
```

**The file is closed before the aggregate is marked Ready.** The explicit block scope on the
`await using` is deliberate: `Ready` must never be observable while the file still has unflushed
bytes in a buffer, or a client polling at exactly the wrong moment downloads a truncated CSV. Order
of operations: **finish the side effect, then publish the fact.**

**Three transactions, not one.** Claim, generate, complete. This is the opposite of the rule for
commands (§6: one command, one transaction) and the difference is the long-running I/O in the
middle. Holding a database transaction open across a multi-minute file write would hold locks and a
connection for the whole generation, and a crash would roll the *claim* back — so the export would
be picked up and regenerated from scratch, every time, forever. Separate transactions make each
state durable the moment it is true. **The cost is that the aggregate can be `Generating` with no
process generating it** — which is exactly why the maintenance job in §16 exists. The trade is
explicit: a recoverable stuck state in exchange for not holding locks across I/O.

```csharp
catch (Exception exception)
{
    logger.LogError(exception, "Export {ExportId} failed.", exportId);
    export.Fail(exception.Message, timeProvider.GetUtcNow());
    await unitOfWork.SaveChangesAsync(CancellationToken.None);
    TryDeletePartialFile(storagePath);
}
```

**`CancellationToken.None` on the failure save, and only there.** If the host is shutting down the
stopping token is already cancelled, so passing it would cancel the save that records *why* the
export failed — leaving it stuck in `Generating` for the very reason we are in this `catch`.
Recording a failure must not itself be cancellable. Everywhere else the token is honoured.

**The partial file is deleted, and the deletion failure is logged, not swallowed.** A failed export
with a half-written CSV on disk is a file nothing will ever reference; the reaper only knows about
files recorded on `Ready` exports. `TryDeletePartialFile` catching `IOException` and **logging** it
is the deliberate opposite of Onyx A16's empty `catch {}` blocks, which are how its payment failures
became invisible.

**The failure is recorded on the aggregate, not just logged.** This is the difference between "the
user sees `Failed` with a reason" and "the user polls `Generating` forever while an operator reads
logs". A status endpoint that can only ever say "still working" is not a status endpoint.

### The download endpoint

```csharp
if (export is null)                    return 404 Export.NotFound;
if (!export.IsDownloadable(now))       return 409 Export.NotDownloadable;
if (!File.Exists(export.StoragePath))  return 404 Export.FileMissing;
var stream = File.OpenRead(export.StoragePath);
return Results.File(stream, "text/csv", export.FileName);
```

- **Addressed by id, never by filename.** The caller supplies a `Guid` the route constrains to
  `:guid`; the path comes from the record. The entire S6 class of bug is unreachable, not
  validated-against.
- **`FileName` is offered to the browser as a `Content-Disposition` name and never used to resolve a
  path.** Two properties for two jobs, which is why one cannot be mistaken for the other.
- **`FileMissing` is a distinct error.** `Ready` with no file means the directory was wiped or the
  disk swapped — a real operational condition that deserves its own message rather than a 500.
- **`Results.File` streams.** The framework owns the lifetime of that `FileStream` and copies it to
  the response body in chunks. Onyx A37 read the whole file into a `byte[]` through an undisposed
  `FileStream`, ignoring `Read`'s return value (CA2022 — `Read` is not required to fill the buffer,
  so that code could silently return a partially-filled array).

---

## 15. Import: all-or-nothing, upsert, and why it does not reuse the product handlers

### The three decisions

**1. All or nothing.** The whole file is parsed and validated, *then* applied, in one save.

Onyx A35 called `SaveChangesAsync` **inside its row loop**. One malformed cell threw on row 57 and
rows 1–56 were already committed: a half-loaded catalogue, no record of where it stopped, no way to
resume, no way to undo. And the person re-uploading the corrected file then hit A36 and duplicated
the 56 rows that *did* land.

Two phases:

```
phase 1  parse + validate    → every error collected, nothing touched
phase 2  apply to aggregates → one SaveChangesAsync for the whole file
```

Phase 1 reports **every** error at once, so one upload tells the user everything wrong with their
spreadsheet instead of making them fix row 3, re-upload, discover row 7, re-upload. Phase 2 is a
single save inside the transaction `UnitOfWorkBehavior` already opened, so every product, price,
stock movement and outbox message from the import commits together or not at all.

**2. Upsert on SKU.** Onyx A36 always did `new Product`, so re-importing a corrected spreadsheet
duplicated the entire catalogue — defeating the whole export/edit/re-import loop it advertised. SKU
is the natural key the business actually uses, and it has a unique index, so "does this already
exist" is a question the database answers cheaply.

**3. Stock is honoured on create, ignored on update — with a note.** This is the one that surprises
people, and the most defensible decision in the file.

Stock moves **only** through evented `StockMovement`s with a stated reason, so that
`SUM(Delta) == Product.StockQuantity` holds for every product — the invariant the integrity job in
§16 asserts. If an update overwrote stock from a spreadsheet, the movement log would no longer
explain the number and the invariant would break. Stock changes through goods-in, a stocktake or a
write-off: things with a cause. Not a column in a file someone edited on a train.

On **create** the column *is* honoured, because there is no history to contradict — and the importer
writes an opening-balance `Adjustment` movement for it, so the invariant holds from the very first
row:

```csharp
movements.Add(StockMovement.Create(
    product.Id, stock.Value.Quantity, StockReason.Adjustment,
    StockReferences.OpeningBalance, stock.Value.Quantity, nowUtc));
```

On **update** the row's `Notes` says what was ignored and why. **Silently dropping a value the user
typed is worse than rejecting it**; a note is how you honour an invariant without lying about it.

### Parsing: four classes of bug, closed

| Onyx | What went wrong | Here |
|---|---|---|
| **A34** | Split the whole file on `'\n'`, leaving a stray `'\r'` on the last field of every row for any file saved on Windows. | `CsvReader` handles CRLF and LF, quoted fields containing delimiters *or newlines*, and ragged rows. |
| **A35** | `decimal.Parse` / `int.Parse`, ambient culture, no `TryParse` — one bad cell threw, mid-loop, after partial commits. | `TryParse` with `InvariantCulture`, a per-row `RowError`, nothing written until the whole file passes. |
| — | A UTF-8 BOM (which Excel writes by default) corrupted the first header name. | `detectEncodingFromByteOrderMarks: true`. |
| — | Duplicate SKUs **inside one file** were silently inserted twice. | A `seenSkus` dictionary; the second occurrence reports the line number of the first. |

Deliberate tolerances, because a spreadsheet touched by a human will have surprises:
`TrimOptions.Trim`, `MissingFieldFound = null`, `HeaderValidated = null`, `BadDataFound = null`.
These turn "throw on the whole file" into "report it on the row", which is the behaviour the
two-phase design needs. Only `Sku` and `Name` are required columns; everything else is optional, and
absent means *leave alone*.

Three guards worth naming:

- **`MaxRows = 10_000`**, and the message says what to do instead ("split it, or use the bulk
  pipeline") rather than just refusing. Paired with `MaxImportBytes = 10 MB` at the endpoint, which
  rejects before anything is read.
- **Currency is derived from the country, never read from the file.** `Price_US` is dollars because
  `Country.US.CurrencyCode` says so. A price can never be stored in a currency that country does not
  trade in, no matter what someone typed in a spreadsheet.
- **The impossible combination is caught in phase 1.** `Status=Active` with no price is rejected at
  parse time, with the real reason, rather than being discovered halfway through applying the file
  when the aggregate refuses to activate.

### Dry run, and the bug it exposed

`?dryRun=true` validates and applies everything **in memory**, reports exactly what would happen,
and writes nothing.

```csharp
if (command.DryRun) unitOfWork.DiscardChanges();
else { products.AddMovements(movements); await unitOfWork.SaveChangesAsync(ct); }
```

**Not calling `SaveChangesAsync` is not enough**, and finding that out was a real bug. The aggregates
are tracked by the `DbContext` and carry pending domain events. The domain event dispatcher saves
whatever the change tracker holds — its check is "is anything pending?" — so a dry run that built
three products and left them tracked was **committed by the dispatcher a moment later**, while the
response cheerfully said `committed: false`. Hence `DiscardChanges()`, which detaches everything
explicitly. The lesson generalises: *with a change tracker, "do nothing" is an action you have to
take.*

### Why the import does **not** reuse the product command handlers

This is the right question to ask, and the answer has two halves: what *is* reused, and what
deliberately is not.

**What is reused is the domain model — which is the reuse that matters.** `ApplyCreate` and
`ApplyUpdate` call exactly the same methods the single-product handlers call:

```
Product.Create     product.Rename        product.SetPrice     product.ChangeCostPrice
product.Activate   product.Discontinue   StockLevel.Create    Sku.Create
Money.Create       Country.FromCode      StockMovement.Create
```

Every invariant lives in the aggregate and the value objects, **not** in the handler. So the importer
gets "cannot activate without a price", "price must be positive", "SKU format", "name length", "no
transition back to Draft", "currency must match the country" — all of it, for free, and it **cannot
bypass any of them even by accident**. If a rule changes, it changes in one place and both paths move
together. A handler, by contrast, contains only orchestration: load, call one method, save.
**Reusing the aggregate is reuse of the thing with the rules in it; reusing the handler would be
reuse of the plumbing.**

Now, concretely, why sending `RenameProductCommand`, `SetProductPriceCommand`,
`SetProductCostPriceCommand`, `ActivateProductCommand` … once per row per field would be wrong:

**1. N+1, by construction.** Every handler begins with
`await products.GetByIdAsync(command.ProductId, ct)`. A 500-row file touching four properties each is
**2,000 commands and 2,000 primary-key round trips**. The importer does one query for the whole file:

```csharp
var skus = parsed.Rows.Select(row => row.Sku.Value).ToList();
var existing = (await products.GetBySkusAsync(skus, ct))
    .ToDictionary(p => p.Sku.Value, StringComparer.OrdinalIgnoreCase);
```

One `WHERE Sku IN (…)`, then a dictionary lookup per row. This is Onyx A26's exact shape, avoided.

**2. Identity mismatch.** Handlers are addressed by `ProductId` (a `Guid`). A CSV is keyed by **SKU**,
and for a *new* row there is no id at all yet. Going through handlers would mean resolving SKU → id
first — another query per row — and no handler can make the create-or-update decision, because that
decision *is* the import's job.

**3. Error semantics are inverted.** A handler returns `ErrorOr<Success>` and stops at the first
problem — correct for one user action. The import must collect **every** error across the whole file,
attribute each to a **line number and column**, and report that nothing was written. A command result
cannot say "row 57, `Price_US` is not a valid number". And collecting results by catching 2,000
individual command failures would mean 2,000 commands had already run — the A35 shape again.

**4. Per-row outcomes and notes have nowhere to live.** The import's unit of output is
`ImportRowResult(LineNumber, Sku, Created|Updated|Unchanged|Failed, Error, Notes)`. `Unchanged` alone
is impossible to express through handlers: `product.Rename` to the same name is neither an error nor
a change, and only a caller comparing before and after can tell. So `ApplyUpdate` tracks `changed`
itself and compares `product.Prices.Count` before and after.

**5. Dry run is impossible through handlers.** Every one of them ends with
`await unitOfWork.SaveChangesAsync(ct)`. "Save" is part of the handler's contract. The import needs
*build, inspect, then decide* — and then `DiscardChanges()`.

**6. The rules genuinely differ.** "Honour the stock column on create, ignore it on update and say
so" is an **import** rule. No single-product command has it, and none should: a human calling
`POST /products/{id}/stock/goods-in` means that number, with a reason attached. Pushing the import's
rule into a shared handler would either weaken the single-product path or require an `isImport: true`
flag — a parameter that changes behaviour, which is how one handler becomes two wearing one name.

**7. Transaction and save granularity.** Each command is `ITransactionalRequest`. The
`HasActiveTransaction` guard (§7) means nested commands would *join* the outer transaction rather
than nest, so atomicity would actually survive — but each handler still calls `SaveChangesAsync`, so
one file becomes 2,000 flushes inside one transaction, each a round trip, each extending the time
locks are held. One save per file is one round trip's worth of batched writes.

**8. Handlers calling handlers is a coupling smell.** It turns the mediator into a general-purpose
service layer: use cases depend on use cases, the call graph stops being discoverable from the
endpoint, transaction boundaries become implicit, and performance characteristics (like that N+1)
become invisible at the call site. **Handlers are entry points for use cases, not reusable
functions.** When two use cases need the same logic, the shared piece belongs in the domain — which
is where it already is.

**The internal sharing that does exist** is `ApplyPricesAndStatus`, a private method both
`ApplyCreate` and `ApplyUpdate` call. Prices, cost price and status transitions behave identically
whether the product is new or existing, so that logic is written once, inside the importer, at the
level where it is genuinely shared. If a third caller ever needed it, it would be promoted to a
domain service — not to a command.

### HTTP status codes on the import

`Committed == false` with failures comes back as **400 with the full per-row body**, not 200.

Onyx A23 returned `200 OK` with an error object inside it, so every client that checked the status
code treated a rejected request as a completed one — including its own retry logic, which therefore
never retried. The rule: **the status code must agree with what happened.**
`Results.BadRequest(import)` keeps the detailed body, so the client gets both the correct code *and*
every row reason. `413 Payload Too Large` for the size cap, and a `multipart/form-data` upload rather
than Onyx's `filePath` string parameter — which was the same path-traversal shape as its export
download (S6), waiting to happen on the write side.

---

## 16. Background jobs and the job runner

### Why background work exists at all

A request handler is the wrong place for work with any of four properties:

| Property | Example here |
|---|---|
| **Takes longer than a client will wait** | Generating a 50,000-row export. |
| **Has no requester** | Publishing outbox messages; expiring old files; checking an invariant. |
| **Must be retried until it succeeds** | Delivering an integration event. Retrying inside the request means the client waits for the retries, and a client that gives up takes the work with it. |
| **Must happen even if nobody logs in** | A stuck export must be failed, and a disk must not fill, on a quiet Sunday. |

Background work is also the only place *verification* can live. A request can enforce an invariant on
the data it touches; only a job can ask "is the invariant still true for **everything**?" That
distinction is why `StockIntegrityCheckJob` exists.

This codebase has four background services, one per category:

| Service | Interval | Job |
|---|---|---|
| `OutboxDispatcher<CatalogDbContext>` | 2 s | Publish committed integration events; retry with backoff; dead-letter after 8 attempts. |
| `ProductExportGenerator` | 5 s | Claim `Requested` exports, generate CSVs, mark `Ready`/`Failed`. |
| `StockIntegrityCheckJob` | 15 min | Assert `SUM(Delta) == StockQuantity` for every product. Reports; never corrects. |
| `ExportMaintenanceJob` | 30 min | Expire and delete old export files; fail exports stuck in `Generating`. |

### `BackgroundService` and the lifecycle

`AddHostedService<T>()` registers a **singleton** whose `StartAsync` the host calls at startup and
whose `StopAsync` it calls on shutdown, passing a `stoppingToken` cancelled when the host begins
shutting down. `BackgroundService` is the base class that turns that into one overridable
`ExecuteAsync(CancellationToken)`.

Three consequences follow from "it is a singleton", and each is a classic bug:

**1. It cannot inject scoped services.** `DbContext` is scoped, not thread-safe, and accumulates
tracked entities. A singleton holding one for the lifetime of the process is a memory leak with a
concurrency bug attached. Hence `IServiceScopeFactory` and `CreateAsyncScope()` per unit of work,
everywhere in this codebase.

**2. Everything before the loop is outside every `try`.** This is subtle and it bit me. Setup code in
`ExecuteAsync` that throws is unprotected, and under the default
`BackgroundServiceExceptionBehavior.StopHost` it **takes the whole host down** — Onyx A18/A21. I
reproduced it deliberately by leaving an empty `StorageDirectory` in `appsettings.json`. Two fixes,
both applied:

```csharp
while (await timer.WaitForNextTickAsync(stoppingToken))
{
    try
    {
        Directory.CreateDirectory(_options.StorageDirectory);  // INSIDE the boundary
        await ProcessPendingAsync(stoppingToken);
    }
    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
    catch (Exception ex) { logger.LogError(ex, "Pass failed. Continuing on the next tick."); }
}
```

`CreateDirectory` is idempotent, so doing it every pass costs nothing — and it is **self-healing** if
someone deletes the directory while the app is running. The second fix is in the options themselves:

```csharp
public string StorageDirectory
{
    get => _storageDirectory;
    init => _storageDirectory = string.IsNullOrWhiteSpace(value) ? DefaultStorageDirectory : value;
}
```

A blank configured value falls back to the default instead of overriding it with nothing. An empty
placeholder left in `appsettings.json` is a realistic thing for someone to do, and it should not be
able to break startup.

**3. The host-level backstop.**

```csharp
builder.Services.Configure<HostOptions>(options =>
    options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);
```

`Ignore` means a faulting service stops being retried but **the host and every other service
survive**. The default, `StopHost`, is why an unhandled `DivideByZeroException` in Onyx's nightly
report terminated the entire worker process and took the unrelated stock job with it — with nothing
to restart either, and a status panel that had never worked (A7). This setting is the **last** of
three boundaries, not the first: each job already has a per-pass boundary, and `JobRunner` adds a
per-run one.

### `PeriodicTimer`, not `Task.Delay`

```csharp
using var timer = new PeriodicTimer(_options.PollInterval, timeProvider);
while (await timer.WaitForNextTickAsync(stoppingToken)) { … }
```

Onyx A20 used `await Task.Delay(TimeSpan.FromHours(24))` in a loop. Two bugs in one line:

- **Drift.** The real interval is *delay + work time*, so a "nightly" job that takes 20 minutes runs
  20 minutes later every day. After a month it is running ten hours off schedule.
- **Phase reset.** The schedule is measured from process start, so a restart at 14:00 makes the
  "nightly" job run at 14:00 forever after.

`PeriodicTimer` ticks on a **fixed cadence** measured from the previous tick, not the previous
completion, so work duration does not accumulate. `WaitForNextTickAsync(token)` returns `false` on
shutdown, which makes the loop condition and the cancellation check the same expression — no
`if (token.IsCancellationRequested) break;` to forget.

Taking `TimeProvider` in the constructor is what makes these jobs **testable**: a `FakeTimeProvider`
can advance an hour instantly, so "does the maintenance job expire a 7-day-old export" is a unit test
that runs in milliseconds instead of a week (§10).

**What `PeriodicTimer` is not:** a scheduler. It cannot express "02:00 UTC daily" or "weekdays only",
and it does not coordinate across instances — with three replicas, three timers tick. For
interval-based work that is idempotent and claims its items (all four of these are) that is fine. For
genuine *cron* semantics the answer is Quartz.NET or Hangfire with a persistent store, or a
distributed lock with leader election. Listed in §20 as something I would add, not something I
pretended to have.

### `JobRunner<TContext>` — the thing that makes jobs observable

Every job body goes through one place:

```csharp
await runner.RunAsync(JobName, CheckAsync, stoppingToken);
```

and that place **guarantees** three things rather than leaving them to be remembered per job:

**1. A run row exists *before* the work starts.**

```csharp
var run = JobRun.Start(jobName, timeProvider.GetUtcNow());
context.Set<JobRun>().Add(run);
await context.SaveChangesAsync(cancellationToken);
```

So a job that dies mid-pass leaves a row stuck in `Running` — **visible**, rather than invisible.
Absence of evidence becomes evidence. Written at the end instead, a job that crashes every pass would
look identical to a job that had never been scheduled.

**2. The outcome is recorded *after* awaiting the work, with real counts.**

Onyx A22 logged `"pass complete for {Count} products"` **before** any work had run, because the work
was `async void` — a method the compiler cannot await, so the caller continued immediately and any
exception inside it was raised on the thread pool with no handler. The message was byte-identical
whether the pass succeeded, failed, or never happened.

`JobRunner` awaits the body, then records `Succeeded` with `ItemsProcessed` / `ItemsFailed`, and only
logs at `Information` when something actually happened — so a quiet job is quiet, and a log line means
a real event.

**3. Nothing escapes.** An exception is recorded against the run and swallowed **at this boundary**,
so one bad pass cannot stop the loop or the host. And if recording the failure itself fails, *that* is
logged too:

```csharp
catch (Exception recordingFailure)
{
    logger.LogError(recordingFailure, "Could not record the failure of {JobName}.", jobName);
}
```

Never an empty `catch {}` — Onyx A16's empty catches are how its payment failures became invisible.
`OperationCanceledException` during shutdown is **rethrown**, not recorded as a failure: a clean stop
is not an error, and conflating the two means every deploy writes a false failure row.

### Why `JobRun` is a table — my favourite Onyx bug

Onyx had an admin panel at `GET /api/admin/worker-status` showing when the overnight jobs last ran.
The jobs wrote their status into a **`static` class in the Worker process**. The endpoint read the
**same `static` class in the API process.** Two processes, two separate statics, no shared memory.

**The panel returned `null` and `0` from the day it shipped and always would** — and the architecture
document described it as "a good example" of shared state that "reflects reality".

It is also the monitoring that would have caught A18 and A21: the jobs could have been dead for weeks,
the panel would have looked exactly as it always had, and the last log line said everything completed
fine. Three independent defects covering for each other.

So: **rows in a table, in the same database as everything else.** Any instance can read them, they
survive a restart, they are queryable, and `GET /api/catalog/jobs/runs` is backed by the same data the
jobs write. The index is the shape of the question:

```csharp
builder.HasIndex(run => new { run.JobName, run.StartedAtUtc }).HasDatabaseName("IX_JobRuns_History");
```

"When did *this* job last run, and did it work?" — newest first, per job. `DurationMs` is stored rather
than computed on read, so a trend ("this pass takes 4× longer than last month") is a query, not a
log-scraping exercise.

**The general principle: a background job's state belongs in shared durable storage, never in process
memory.** Statics, in-memory caches and singleton fields are all the same bug once there is more than
one process — and there is always more than one process eventually.

### The four jobs, and what each one teaches

**`OutboxDispatcher`** — see §13 for the pattern. Its contribution here is **per-message error
handling**: a failing message is marked and skipped, and the rest of the batch still goes out.
RiverBooks' poller takes the first unprocessed row and never marks it on failure, so one permanently
broken message is retried forever and **blocks every message behind it** — head-of-line blocking with
no escape. Backoff via `NextAttemptAtUtc`, and dead-lettering after `MaxAttempts = 8`, are the
difference between "retries" and "retries forever".

**`ProductExportGenerator`** — the work-claiming job. Walked through in §14.

**`StockIntegrityCheckJob`** — the **verification** job, and the one Onyx never had.

```csharp
.Where(row => row.Recorded != row.Movements).Take(100)
```

It asserts `SUM(StockMovement.Delta) == Product.StockQuantity` for every product, computed in SQL as a
`GROUP BY` against an index — not a table load (Onyx A19 materialised whole tables and filtered in
memory).

Note what it deliberately does **not** do: **it does not correct anything.** A discrepancy means a
*defect* — some code path moved stock without recording a movement — and silently healing it is
precisely the mistake Onyx made. Its replenishment job (D5) found anything under 5 in stock and added
50 units from nowhere: no supplier, no purchase order, no delivery, no movement record. That job also
**concealed** D1: an oversell drove stock low, the job topped it up, and the discrepancy vanished
before anyone could see it. Overselling was undetectable from the data.

So this job reports, loudly, and a human decides. `ItemsFailed` is set to the discrepancy count, so
`JobRuns` shows a non-zero failure count on any pass that found one — **queryable without reading
logs**, which means it can drive an alert. An empty result is the only acceptable result.

**`ExportMaintenanceJob`** — the **reaper**, and the counterpart to §14's three-transaction design.
Two passes:

1. *Expire and delete.* `Ready` exports past `ExpiresAtUtc` → `Expire()`, then delete the file. If the
   delete fails, the row is still expired and no longer downloadable — **the part that matters** — and
   the pass counts it as failed. A leftover file is a disk problem, not a correctness problem; the
   distinction is worth making explicitly in the code.
2. *Recover stuck exports.* `Generating` for longer than `StuckExportThreshold` (15 min) →
   `Fail("Generation did not complete within … Request a new export.")`. This is the cost of separate
   transactions, paid: the generator can die mid-write, and without this the user polls a status that
   will never change. Marking it `Failed` makes it **visible and retryable**.

Both passes are capped (`Take(50)`, `Take(100)`) so one bad backlog cannot turn a maintenance pass
into a multi-hour transaction.

### Multi-instance safety, which is not optional

With two or more replicas, every job runs on every instance. Three mechanisms keep that correct, and
none of them is a lock:

- **Atomic claim via the version token.** `BeginGenerating()` plus a `WHERE Version = @expected`
  update. The loser gets `DbUpdateConcurrencyException`, logs at `Debug`, and moves on. §8's
  concurrency token, reused.
- **Idempotent work.** The outbox marks messages before publishing effects; stock movements are
  idempotent on the three-column unique index (§9). A job that runs twice costs double and breaks
  nothing.
- **Read-only verification.** The integrity check writes nothing, so running it three times is three
  times the reads and zero risk.

What is **not** covered: nothing stops three instances doing the same `GROUP BY` every 15 minutes.
That is wasted work, not incorrect work, and the fix — a distributed lock or leader election — is in
§20 rather than pretended-to-exist here.

### In-process versus a separate worker

These jobs run **inside the API process**. That is a deliberate trade for a modular monolith, and it is
worth being able to defend both directions:

**For in-process:** one deployable, one configuration, one set of connection strings, one place to look
at logs. No risk of the A7 bug, where two processes had two views of the same "shared" state. Jobs can
be tested in the same integration test host as the endpoints.

**Against:** job work competes with request work for threads, the connection pool and memory — a heavy
export can degrade API latency. Jobs cannot be scaled independently of the API. Scaling the API to five
instances also multiplies the job timers by five.

**When I would split them:** when export volume starts affecting p99 request latency, or when a job
needs materially different resources (more memory, a longer timeout) than a request does. The code is
already ready for it — every job is a `BackgroundService` behind `AddHostedService`, taking only
`IServiceScopeFactory`, `TimeProvider` and options, so moving them into a worker host is a registration
change, not a rewrite. That is the real payoff of the `JobRunner` abstraction: *where* a job runs stops
being part of its design.

---

## 17. Polling versus push: the transport for "is my export ready?"

### The cost of polling, measured

Today the client polls `GET /api/catalog/exports/{id}`. Every poll is a full HTTP request: TLS record
processing, routing, authentication and authorisation, a MediatR pipeline, a primary-key query, JSON
serialisation, logging. The query itself is the cheap part.

A 90-second export polled every 2 seconds is **45 requests, 44 of which say "still working"**. At 200
concurrent users exporting, that is roughly 100 requests/second of pure status noise — and it does not
scale with *useful* work, it scales with *waiting*. On mobile it is worse than the server cost
suggests: each poll wakes the radio, one of the largest battery costs on a phone.

So the instinct is right: **the pattern is wasteful.** The protocol in question is **Server-Sent
Events (SSE)** — `Content-Type: text/event-stream`, a response that never ends, server → client only,
with the browser's `EventSource` API on the other side. "The server can push events to the caller; the
caller cannot push back" is exactly SSE's shape.

### Recommendation: a ladder, in this order

I would **not** start with SSE. The rungs below are ordered by cost-to-benefit, and each is
independently worth doing.

**Rung 1 — make polling cheap. Do this first; it is nearly free.**

- **`Retry-After: 2`** on the 202 and on every `Generating` status response. This puts the poll interval
  under *server* control: under load it can say `Retry-After: 10` and every well-behaved client backs
  off at once. No client deploy needed to change the cadence.
- **Client-side backoff**, not a fixed interval: 1 s, 2 s, 4 s, 8 s, capped at 10 s. Most exports finish
  in the first few seconds; a slow one should not be polled at the same rate as a fast one. This alone
  removes roughly 70% of the requests on a 90-second export.
- **`ETag` + `If-None-Match`.** The status response changes only on a state transition, so an unchanged
  status is a **304 with no body**. The `ETag` can simply be the aggregate's `Version` token — which
  already exists and already increments on exactly the transitions that matter. Cheaper serialisation,
  far less bandwidth, pure HTTP: no new protocol, no new infrastructure, and old clients keep working.
- A read path that is only a primary-key lookup. Already true here.

Rung 1 takes 45 requests down to roughly 8, of which 7 are 304s with empty bodies. **That is usually
where this problem should stop.**

**Rung 2 — long polling, if a few seconds of latency still matters.**

`GET /api/catalog/exports/{id}?wait=30`. The server holds the request open until the status changes or
30 seconds elapse, then responds. One request per 30 seconds instead of fifteen, and the client learns
about completion within milliseconds of it happening.

Implementation, in increasing order of effort:

- *Single instance:* an in-process registry keyed by export id holding a `TaskCompletionSource`.
  `ProductExportCompletedEvent` already exists as a domain event — a handler signals the TCS. Then
  `await Task.WhenAny(tcs.Task, Task.Delay(wait, ct))`.
- *Multi-instance:* the waiter is on instance A, the generator ran on instance B. The simplest honest
  answer is to re-read the row every second inside the wait loop. That moves 15 WAN round trips (with
  TLS and auth) into 30 LAN round trips of a single indexed primary-key read — **much** cheaper despite
  being more queries, and it needs no new infrastructure.

Cost: each waiting request occupies a connection and a request slot (not a thread — it is fully async),
so the server's concurrent-request limits and the load balancer's idle timeout both need to be above 30
seconds. Kestrel handles tens of thousands of these.

**Rung 3 — SSE, when there is a dashboard with many concurrent waiters.**

```
GET /api/catalog/exports/{id}/events
Accept: text/event-stream

HTTP/1.1 200 OK
Content-Type: text/event-stream
Cache-Control: no-cache
X-Accel-Buffering: no

: keep-alive

id: 1
event: status
data: {"status":"Generating","rowCount":null}

id: 2
event: ready
data: {"status":"Ready","rowCount":48213,"isDownloadable":true}
```

…then the server closes the stream. The browser side is three lines:

```js
const es = new EventSource(`/api/catalog/exports/${id}/events`);
es.addEventListener("status", e => render(JSON.parse(e.data)));
es.addEventListener("ready",  () => { es.close(); download(id); });
```

**Why SSE and not WebSockets, for this problem:**

| | SSE | WebSockets / SignalR |
|---|---|---|
| Direction | Server → client only — **exactly** what a status feed needs | Bidirectional; the upstream half is unused weight |
| Protocol | A plain HTTP response. Works through any proxy that handles chunked HTTP/1.1 | Requires an `Upgrade` handshake; some corporate proxies and older load balancers break it |
| Reconnect | **Built into `EventSource`** — automatic, with `Last-Event-ID` replay | You write it, including backoff and resubscribe |
| Auth | Normal cookies/headers, normal middleware, normal `[Authorize]` | A separate handshake path to secure |
| Payload | UTF-8 text only (JSON is fine) | Binary too |
| Infrastructure | None | A hub, usually a backplane (Redis) for scale-out |
| Debugging | `curl` shows you the frames | Needs a WebSocket client |

For "tell me when this one thing changes", SSE is strictly the better fit. WebSockets earn their
complexity when the client also sends a stream — live collaboration, chat, cancel-mid-stream.

**What SSE does *not* solve, and this is the important part:**

- **Scale-out fan-out.** The export is generated on instance B; the SSE connection is held by instance
  A. Either the SSE endpoint polls the database internally (the same as rung 2, and perfectly
  respectable — one LAN read per second per open stream), or you add a real fan-out: Redis pub/sub,
  Postgres `LISTEN/NOTIFY`, or a message bus. **SSE moves the cost; it does not remove it** — someone
  still has to find out that the state changed.
- **Connection limits.** Over HTTP/1.1 a browser allows about six connections per origin, and a
  held-open SSE stream consumes one. Three export tabs plus the app's normal traffic will stall the
  page. HTTP/2 multiplexes and the limit effectively disappears — so **SSE at scale assumes HTTP/2**.
- **Intermediaries.** Response buffering (nginx's `proxy_buffering`, IIS, some CDNs) holds your events
  until the buffer fills, which for a 60-byte status event is never. You need `X-Accel-Buffering: no`,
  response compression disabled on that route, a `: keep-alive` comment every ~15 s to defeat idle
  timeouts, and the route excluded from any "buffer the whole response" middleware.
- **Durability.** A dropped connection loses the notification. Which is why the ladder has a floor:
  **the `ProductExport` row remains the source of truth and the status endpoint never goes away.**

**Rung 4 — webhooks, for machine clients.**

For a B2B integration there is no browser to hold a connection. The caller supplies a `callbackUrl` on
the request; completion POSTs the status to it. The infrastructure for this **already exists in this
codebase**: `ProductExportCompletedEvent` → an outbox row → the `OutboxDispatcher` delivers it with
backoff and dead-lettering after eight attempts. What would need adding: HMAC-SHA256 signing of the
body with a per-subscriber secret, a timestamp inside the signed payload to stop replay, a URL
allow-list to prevent SSRF, and an `EventId` the receiver can deduplicate on (`IIntegrationEvent`
already carries one). Webhooks are the only option on this ladder that survives the client being
offline when the work finishes.

### The decision, and the sentence worth saying out loud

> **The state machine is the contract; the transport is an optimisation.**

Because `ProductExport` is a durable aggregate with `Requested → Generating → Ready/Failed/Expired` and
one `IsDownloadable` predicate, all four transports are interchangeable and additive. Polling, long
polling, SSE and webhooks are four ways to *learn* about a transition that is recorded whether anyone is
listening or not. A design where SSE *is* the notification — where the only record of completion is a
frame on a socket — loses the result when the laptop lid closes, and no amount of reconnect logic fixes
that.

So: **rung 1 now** (`Retry-After`, client backoff, `ETag`/304 keyed on the existing `Version` token) —
an afternoon's work, no new infrastructure, and it removes most of the cost. **Rung 2 when a few seconds
of latency becomes a complaint.** **Rung 3 when there is a dashboard watching many things at once and
HTTP/2 in front of it.** **Rung 4 the moment a machine, not a human, is waiting.** And in every case the
status endpoint stays, because a push you missed has to be recoverable by asking.

---

## 18. Smaller decisions

### Why `internal` almost everywhere

`Product`, `ProductRepository`, the command handlers, the configurations — all `internal`.

The only public surface is `BoomboCard.Catalog.Contracts`. So the boundary is enforced by the
compiler, not by a convention someone has to remember in a code review. Another module
*physically cannot* reference `Product`.

### Why `SharedKernel` is split in two

| Project | Contents |
|---|---|
| `BoomboCard.SharedKernel` | `Entity`, `AggregateRoot`, `ValueObject`, `Money`, `Country`, the abstractions. **No EF Core, no ASP.NET Core.** |
| `BoomboCard.SharedKernel.Persistence` | unit of work, dispatcher, behaviour, interceptor, outbox |

Because the legacy system's architecture document claimed its domain had "no EF Core, no ASP.NET
Core, no knowledge of HTTP or SQL" while the project file referenced EF Core and the pricing
service took an `IQueryable` and called `EF.Functions.Like`. Splitting the projects makes the
claim structural instead of aspirational — the compiler enforces it.

Next step: a `NetArchTest` assertion so it stays true as the codebase grows.

### Why `DomainEventAccess`

```csharp
public static class DomainEventAccess
{
    public static IReadOnlyList<IDomainEvent> Drain(AggregateRoot aggregate) => aggregate.PopDomainEvents();
    public static void IncrementVersion(AggregateRoot aggregate) => aggregate.IncrementVersion();
}
```

`PopDomainEvents` and `IncrementVersion` are `internal` on `AggregateRoot`. This static class
lives in the same assembly, so it can reach them, and it is `public`, so the persistence project
can call it.

**Why bother?** Clearing events and forging a version are infrastructure concerns. If they were
public on `AggregateRoot`, any application or domain code could silently discard pending events.
One reference project exposes `PopDomainEvents()` publicly, and exactly that is possible. Another
achieves the same protection by implementing `ClearDomainEvents()` explicitly against its
interface.

### Why `Country` is a hardcoded smart enum

```csharp
public static readonly Country UnitedStates = new("US", "United States", "USD", 0.08m);
public static readonly Country Emirates = new("AE", "United Arab Emirates", "AED", 0.05m);
```

A **smart enum** is a class that behaves like an enum but carries data and behaviour — here a
currency, a tax rate, and `TaxOn(Money)`.

**Why hardcoded?** Two countries, and the requirement was explicitly to keep tax simple. No table,
no migration, no lookup service.

**Why it is still safe:** it is in **one place**. The legacy system held the same promo code's
expiry as `2026-12-31` in the pricing service and `2026-11-30` in the reporting job — so from
December the report would have shown zero uptake on a code still costing margin. One definition
makes that class of divergence impossible.

**When it graduates:** the moment a rate must vary by category, or change over a date range, it
becomes a table with effective dating. Building that now would be speculative — the legacy system
was full of exactly that, with four "seams" left unused for a next pull request.

### Why no cache

The legacy system put a **singleton** cache holding a **scoped** `DbContext` in front of the
product lookup. That is a captive dependency — one connection and one change tracker shared by
every concurrent request, for the process lifetime. The DI container detects this and refuses to
start, so **the scope validator was switched off** with a comment saying nothing scope-sensitive
was happening. It was never invalidated by anything. Its dictionary was read inside a lock and
**written outside it**, which can hang a request thread permanently. And its "warmer" no-opped
after the first run.

All of that for a five-product catalogue served by an indexed primary-key lookup.

So: no cache. A cache goes in when a measurement asks for one, behind `IMemoryCache`, with a TTL,
invalidated by `ProductPriceChanged`.

### Why central package management

`Directory.Packages.props` holds every version once; project files just say
`<PackageReference Include="MediatR" />`.

The legacy build emitted `NU1903` on every run — a known high-severity advisory in a transitive
package — and shipped anyway. Here:

```xml
<NuGetAudit>true</NuGetAudit>
<NuGetAuditMode>all</NuGetAuditMode>
<WarningsAsErrors>$(WarningsAsErrors);NU1903;NU1904</WarningsAsErrors>
<WarningsAsErrors>$(WarningsAsErrors);CS4014;CS1998</WarningsAsErrors>
```

A vulnerable package fails the build. So does a forgotten `await` (`CS4014`) and an `async` method
with no `await` (`CS1998`) — the legacy system's worst bug was `async void` fire-and-forget, so
those two are build failures now.

### Why the real provider in tests, not SQLite and not InMemory

Onyx's tests used `UseInMemoryDatabase`, which enforces **no unique indexes, no constraints, no
transactions and no concurrency tokens** (**T6**). It could not have detected the missing SKU
uniqueness (**D10**) or the stock race (**D1**) even in principle.

SQLite was the first answer here, and it is a real relational engine — the tests genuinely proved
the unique indexes reject duplicates. It still was not good enough, and
[BUGS-AND-LESSONS.md](BUGS-AND-LESSONS.md) is why: **three of the eight bugs were provider
behaviour rather than logic**, and one of them was SQLite refusing to order a `DateTimeOffset`,
a limitation that does not exist on SQL Server at all. A suite that is green on a database nobody
deploys is testing a different system.

So the integration tests run on SQL Server LocalDB — the same engine as the host, no Docker, no
setup on Windows — one fresh database per test class, dropped afterwards. See `SqlServerFixture`.
Pure unit tests (the aggregate, the value objects, the architecture rules) touch no database and
need none.

The test fixture applies `UseBoomboCardConventions()` too, so a query that would throw in
production throws in tests rather than passing here and failing on deployment.

### Why `Guid` ids

So the domain can build a complete aggregate and raise its events **before** the database is
involved. With a database-generated `int`, the id is `0` until save — so an event raised in a
factory would carry `ProductId = 0`.

The trade-off is index fragmentation from random Guids. If that shows up, sequential Guids
(`NEWSEQUENTIALID`, or ULIDs) are the fix.

---

## 19. Likely questions, short answers

**"What is an aggregate, in one sentence?"**
A cluster of objects that must stay consistent together, with one root as the only way in, loaded
and saved as a unit.

**"How do you decide what goes in an aggregate?"**
Put it in if an invariant needs it in order to decide. Keep it out if it only records what
happened. And keep aggregates small — unbounded collections are the warning sign.

**"Entity or value object?"**
Does identity survive a change of all its values? If yes, entity. If it *is* its values, value
object.

**"Why does the version need to increase?"**
Because the check is "is the version still the one I read". If it never changes, that is always
true and the token detects nothing. Writing advances the generation, which is what makes every
other reader's snapshot stale.

**"What does `await next()` do?"**
Runs the rest of the pipeline and then the handler. Code before it runs on the way in, code after
it on the way out — which is how one behaviour brackets a handler with a transaction.

**"Does the behaviour run again when a domain event handler fires?"**
No. Behaviours wrap `IRequest` sent through `Send`. Domain events are `INotification` published
through `Publish`, and notifications have no pipeline. If a handler sends a nested *command*, the
behaviour does run, and the `HasActiveTransaction` guard makes it join the existing transaction.

**"Why dispatch events after the save rather than before?"**
So handlers read persisted state. Publishing first means a handler querying the database sees the
state as it was before the command ran.

**"Why inside the transaction?"**
So a failing handler rolls the command back. Dispatching after a commit means the aggregate is
already durable when a handler fails, and the caller gets an error for a write that stands.

**"Why does the dispatcher loop?"**
A handler can mutate another aggregate, which raises its own events. One pass drops those.

**"Why is the unique index three columns?"**
Product plus reference plus reason. Reason must be in it because one session legitimately produces
both a `Sale` and a `PaymentFailed` movement for the same product.

**"You already check in the handler — why the database constraint too?"**
The check is a read-then-write race. Two concurrent redeliveries both read "does not exist" and
both insert. Only the constraint actually prevents it.

**"Why is `StockMovement` not in the `Product` aggregate?"**
No invariant reads the movement history, and the table grows forever. Putting it inside would mean
loading a product's whole trading history to sell one unit.

**"Why is `ProductPrice` in it, then?"**
Three invariants need the prices in memory to decide, and there are only a handful of rows.

**"Why `TimeProvider`?"**
Time is an input, not an ambient fact. Domain methods are told the time, so they are deterministic
and every time-dependent rule is testable.

**"Why `ErrorOr` instead of exceptions?"**
Business rule failures are expected outcomes. They belong in the return type where the compiler
can see them. I still throw for programming errors like a currency mismatch.

**"Why no reservations?"**
The invariant is Catalog's alone, and the database can enforce it with a concurrency token. A
reservation would be a second mechanism for something the token already guarantees. In effect,
deduct-plus-compensate *is* a reservation — the expiry sweeper is the part you cannot skip either
way.

**"What happens if the stock deduction succeeds and the order creation fails?"**
The checkout session is committed before either, so it is a durable handle. The session expires,
the sweeper fires an idempotent restore, and a reconciliation sweep catches anything the sweeper
missed. Retrying the checkout is also safe, because the deduction is idempotent on the session id.

**"Why one transaction per command and not per aggregate?"**
A command is the unit of business intent. If it touches two aggregates, both changes are part of
one decision and should succeed or fail together.

**"Why is the export not generated in the request?"**
Because its size is set by the data, not the request. A large catalogue becomes a proxy timeout,
an O(n²) string build in memory, and work that cannot be retried or resumed. The request creates a
record and returns 202; a background generator does the work.

**"Why three transactions in the generator instead of one?"**
There is multi-minute file I/O in the middle. One transaction would hold locks and a connection
across it, and a crash would roll the *claim* back so the export regenerated from scratch forever.
The cost is an export that can sit in `Generating` with nothing generating it — which is exactly
what the maintenance job recovers.

**"How do two instances not generate the same export twice?"**
`BeginGenerating()` plus a save. The update is `WHERE Id = @id AND Version = @expected`, so the
loser affects zero rows and gets `DbUpdateConcurrencyException`. The concurrency token doubles as a
work-claiming mechanism.

**"What makes the export constant-memory?"**
`AsNoTracking()` + a flat `Select` + `AsAsyncEnumerable()` on the way in, and a `CsvWriter` over a
`StreamWriter` over a `FileStream` on the way out. One row is alive at a time; every layer holds a
few kilobytes. Drop `AsNoTracking()` and the change tracker silently turns it back into a full load.

**"Why `IAsyncEnumerable` and not `List`?"**
Because `List` is the whole catalogue in memory. `IAsyncEnumerable` is "a sequence where getting the
next item is awaitable", which is exactly a database cursor — the writer pulls, the reader yields.

**"Why does the import not reuse the product command handlers?"**
It reuses the *domain* — the same `Rename`, `SetPrice`, `Activate` calls, so every invariant applies.
It does not reuse the handlers because each one loads by id and saves (2,000 round trips and 2,000
flushes for a 500-row file), is keyed by `ProductId` rather than SKU, stops at the first error when
the import must report all of them, cannot express `Unchanged` or a per-row note, and cannot do a dry
run because saving is part of its contract. Handlers are entry points for use cases, not reusable
functions.

**"Why is the stock column ignored on update?"**
Because `SUM(StockMovement.Delta) == Product.StockQuantity` has to hold. Overwriting stock from a
spreadsheet would leave a number the movement log cannot explain. It *is* honoured on create, with an
opening-balance movement written for it, and an ignored column says so in the row's notes.

**"Why does a rejected import return 400 and not 200?"**
Because nothing was written. Onyx returned 200 with an error object inside, so every client checking
the status code — including its own retry logic — treated a rejection as a success. The body still
carries every row reason.

**"Why is `JobRun` a table and not a static?"**
Onyx wrote job status to a `static` in the Worker process and read it from a `static` in the API
process. Two processes, two statics; the admin panel returned null from the day it shipped. A job's
state belongs in shared durable storage, always.

**"Why `PeriodicTimer` instead of `Task.Delay`?"**
`Task.Delay` in a loop makes the interval *delay + work time*, so a nightly job drifts later every
day, and the phase resets on restart. `PeriodicTimer` ticks on a fixed cadence and its
`WaitForNextTickAsync` returns false on shutdown, so the loop condition is also the cancellation
check.

**"Isn't polling for export status wasteful?"**
Yes. The cheap fix is `Retry-After`, client backoff and `ETag`/304 — 45 requests become about 8, most
of them empty. SSE (`text/event-stream`) is the server-push protocol and is the right one here over
WebSockets, because the feed is one-directional and `EventSource` reconnects by itself — but it moves
the fan-out problem rather than removing it, and a dropped connection loses the notification. So the
status record stays the source of truth and the transport is an optimisation on top of it. See §17.

---

## 20. Making the monolith multi-module

Catalog was built alone, and three pieces of plumbing were correct for exactly as long as that
was true.

### The bug that was one commit away

`AddModulePersistence<TContext>` registered the three per-module services unkeyed:

```csharp
services.AddScoped<IUnitOfWork, EfUnitOfWork<TContext>>();
services.AddScoped<IDomainEventDispatcher, DomainEventDispatcher<TContext>>();
services.AddScoped<IOutboxWriter, OutboxWriter<TContext>>();
```

The second call to it — `AddCustomersModule`, two minutes of work — would have replaced all
three for **every handler in the process**, because the last registration for a service type
wins. A Catalog handler would then be handed `EfUnitOfWork<CustomersDbContext>`, call
`SaveChangesAsync` on a context holding none of its changes, get `0` back, and the behaviour
would commit a transaction over a context with nothing in it.

No exception. No log. No rows. A `201 Created` with a product id in it, and no product.

It is worth noticing what class of bug that is. Every other defect in this codebase's history
announced itself — [bug 1](BUGS-AND-LESSONS.md) threw, bug 6 killed the host, bug 5 lied in a
response body that could be checked. This one produces a system that looks completely healthy
and silently discards writes: Onyx **D3** ("multi-write use cases are not transactional")
reached by a different road, inside the plumbing written specifically to prevent **D3**.

### Typing the registration on the context

```csharp
public interface IUnitOfWork<TScope> : IUnitOfWork;

services.AddScoped<IUnitOfWork<TContext>, EfUnitOfWork<TContext>>();   // per module
```

and **nothing is registered against the bare interface**. A handler asks for
`IUnitOfWork<CatalogDbContext>`; asking for a context its module cannot see does not compile.
`TScope` is unconstrained because `BoomboCard.SharedKernel` has no EF Core reference —
Persistence constrains the implementation to `DbContext`.

Keyed services (`[FromKeyedServices("catalog")]`) were the alternative and are worse: a
forgotten attribute resolves the wrong module with no error, which relocates the failure mode
rather than removing it.

### Why the pipeline behaviour had to become abstract

MediatR's open-generic pipeline registration permits exactly two type parameters —
`IPipelineBehavior<TRequest, TResponse>` — so the module cannot be a third one. But the
behaviour needs a concrete unit of work, and that is per module.

So `ModuleUnitOfWorkBehavior<TRequest, TResponse>` is abstract, and each module closes it over
its own context and constrains it to its own request marker:

```csharp
internal sealed class CatalogUnitOfWorkBehavior<TRequest, TResponse>(
    IUnitOfWork<CatalogDbContext> unitOfWork,
    IDomainEventDispatcher<CatalogDbContext> dispatcher)
    : ModuleUnitOfWorkBehavior<TRequest, TResponse>(unitOfWork, dispatcher)
    where TRequest : notnull, ICatalogRequest;
```

**The constraint is what does the routing.** The container skips an open-generic registration
whose constraints the closed type would violate, so a Customers command never picks up Catalog's
transaction. Same mechanism that already kept queries out of a transaction, used one level finer.

### The second-order problem, which is the interesting half

That fix moved the failure rather than removing it. A command that forgets its module marker
still compiles, still runs, and now gets **no transaction at all** — quieter than what it
replaced.

A fix that relies on remembering something is not finished. So the convention became an
assertion. `ModuleWiringTests` fails unless:

- every `ITransactionalRequest` in every module carries **exactly one** module marker;
- every marker has a registered behaviour;
- nothing is ever registered against a bare per-module interface again;
- a real command resolves exactly one transaction boundary, and a real query resolves none;
- the container builds with `ValidateScopes` and `ValidateOnBuild` on.

Both mutations were reintroduced to confirm both tests fail. The last item deserves a note:
`Program.cs` had claimed since it was written that "there is a test asserting the container
builds". There was not. That is a BoomboCard X-number, and it is fixed.

### Where the shared services live now

`AddBoomboCardSharedServices` was in `BoomboCard.Catalog.DependencyInjection`, documented as
"registered once by the host, not per module" — a host-wide concern implemented inside a module,
which worked only because there was one module. The second module made it visible: Customers
would have had to reference Catalog to compose the host's shared services, which is precisely
the cross-module reference `Modules_SeeEachOtherOnlyThroughContracts` forbids. It moved to
`SharedKernel.Persistence`.

### The inbox, and why at-least-once needed a mechanism

`OutboxDispatcher` publishes a message through MediatR, which fans out to **every** subscribed
handler, and then marks the message as a whole. If the second of three consumers throws, the
message is retried — and the first consumer, which already succeeded, runs again. Marking per
consumer instead would mean tracking partial progress in the publishing module, which has no
business knowing who is listening.

`IIntegrationEvent` has always said "delivery is at-least-once, so every handler MUST be
idempotent". That was a rule with no mechanism. The mechanism is a per-module `InboxMessages`
table keyed `(EventId, Consumer)`:

```csharp
if (!await inbox.TryClaimAsync(notification, GetType(), ct))
{
    return;                 // already handled; this is a redelivery
}
```

Two details matter. The claim is **added, not saved** — it commits with the handler's own work,
because the alternative allows a crash between the two, leaving an event marked consumed with
none of its effects applied, permanently, since the claim suppresses every retry. And the
`false` path is a read, so two parallel deliveries can both pass it: the composite primary key
rejects the second insert, the transaction rolls back, the message is retried, and the read then
sees the committed row. **The constraint is the guarantee; the read is the fast path.** Same
division of labour as Catalog's `(ProductId, Reference, Reason)` index.

### Architecture tests as a theory over a registry

The layering rules lived in `BoomboCard.Catalog.Tests`. Keeping them there would have meant
either re-stating every rule in each new module's test project or quietly not asserting them —
and a rule asserted for one module out of seven is not a rule.

They moved to `tests/BoomboCard.ArchitectureTests`, driven by `Modules.All`. Adding a module is
one entry, and it immediately inherits every layering, encapsulation, event-shape and wiring
rule. Namespaces are *derived* from the module name rather than configured, which makes the
naming convention itself load-bearing: a module that puts its aggregates somewhere other than
`BoomboCard.X.Domain` fails the tests instead of silently escaping them.

`SourceConventionTests` had the same problem in miniature — a hardcoded five-element array of
project names, correct on the day it was written. A new module would have been free to write
`DateTime.Now`, `.Result`, `async void` and `catch { }` with the suite still green. It now globs
`src/` at run time. **A stale array enforces exactly as much as a stale document**, which is the
whole subject of Onyx X1–X15.

---

## 21. Customers: identity without an identity framework

### The decision, stated once

**The aggregate is ours. The hashing algorithm is theirs.**

`Microsoft.Extensions.Identity.Core` is referenced for exactly one class,
`PasswordHasher<T>`. Not `Microsoft.AspNetCore.Identity.EntityFrameworkCore`: no
`IdentityDbContext`, no `IdentityUser`, no `UserManager`, no stores, no migrations full of
tables this system does not use.

What that one class provides, and `HashHelper` did not (**S7**):

| | Onyx `HashHelper` | `PasswordHasher<T>` |
|---|---|---|
| Algorithm | SHA-256, one pass | PBKDF2-HMAC-SHA512 |
| Iterations | 1 | 100,000 |
| Salt | none | 128 bits, random, per password |
| Comparison | `computed == storedHash` | `FixedTimeEquals` |
| Upgradeable | no format marker | self-describing, so the work factor can rise |

The last row is the one people skip. A hash with no format marker cannot be distinguished from a
hash with a different work factor, so a system that stores one is pinned forever to whatever it
launched with. Here `VerifyHashedPassword` can return `SuccessRehashNeeded`, so **a correct
password against an outdated hash is transparently re-hashed on sign-in** — the KDF cost moves
with hardware, with no migration and no forced reset.

Deliberately *not* recorded as a password change: nothing the customer controls changed, so
claiming otherwise would put a misleading date on the account and send them a notification they
cannot explain.

### The domain never sees a password

`PasswordCredential` holds an opaque `Verifier` string and compares nothing. The comparison
lives in the application layer because it must be constant-time and must understand the stored
format — both infrastructure concerns — and because the plaintext should exist only inside one
handler's stack frame.

The aggregate is *told* whether the verifier matched, and decides what that means:

```csharp
var verification = passwordVerifier.Verify(customer.Credential.Verifier, command.Password);
var attempt = customer.RecordSignInAttempt(
    verification is not PasswordVerificationOutcome.Failed, fingerprint, nowUtc);
```

### State versus log, for the second time

The split is the same one §3 made for `Product.Stock` and `StockMovement`, and the reasoning
transfers exactly:

| | State, on the aggregate | Log, its own table |
|---|---|---|
| Catalog | `Product.Stock` | `StockMovement` |
| Customers | `PasswordCredential.FailedAttemptCount`, `LockedOutUntilUtc` | `AuthAttempt` |

The counter is state an invariant is enforced against, so it belongs on the aggregate and is
read under the concurrency token. The per-attempt rows grow without bound, so they cannot be a
collection on the aggregate. Deriving "is this account locked" with a `COUNT` over the log on
every sign-in would be both slower and racier.

And the concurrency token is not decoration here. There is a test driving two independent
contexts through one failed sign-in each: without the token both store
`FailedSignInCount = 1`, and an attacker running parallel requests never reaches the threshold.
That is Onyx **D1**'s exact shape — read, compare, write with no token, so two writers both
"succeed" — applied to a counter that is a security control rather than stock.

### Returning the attempt rather than an `ErrorOr`

`RecordSignInAttempt` returns `AuthAttempt`, never an error. The tempting signature is
`ErrorOr<AuthAttempt>`, and it is wrong: the failing path then has no room to carry the audit
row, and **the row for a failure is the one that matters**. So the outcome is data on the
attempt, the handler maps it to a response, and there is no path on which an attempt happens and
nothing records it.

Onyx's login looked the customer up, compared a hash, and returned `Unauthorized()` — leaving no
trace that anyone had tried (**S12**). A password-guessing campaign against it would have been
invisible both while it ran and afterwards, and the SQL injection on the same endpoint
(**S1**) would have left no evidence either.

### One error for every failure, and the one exception

Wrong password, unknown address, suspended account: all `Password.Incorrect`, same wording, same
401. Distinguishing them turns the login endpoint into an account-enumeration oracle, which is
the first step of every credential-stuffing run. The distinction is recorded in the log, where it
is useful to us and not to them.

Lockout returns a distinct 403, and that is a deliberate trade rather than an oversight. It does
confirm the account exists — but a customer locked out after mistyping their password needs to
be told that waiting will fix it, or they will try again immediately, extend their own lockout,
and contact support. The leak is small (an attacker five attempts in already knows the address is
worth attacking) and the cost of hiding it is paid entirely by real customers.

The registration endpoint leaks the same fact and cannot avoid it: a sign-up form has to tell
the person in front of it that they already have an account. The mitigation there is the rate
limiter, not a vaguer error.

### Two throttles, because one is not enough

| | Bounds | Mechanism |
|---|---|---|
| Rate limiter | a **client** | fixed window, 10/minute, partitioned by client address |
| Lockout | an **account** | 5 failures, then doubling 1 → 30 minutes, cleared on success |

An attacker distributing guesses across many addresses still hits the lockout; one hammering a
single account still hits the limiter. Partitioning the limiter by client rather than globally
matters: a global limit would let one attacker lock every honest user out, which is a worse
outcome than the attack.

The lockout is capped on purpose. Uncapped doubling turns a modest guessing attempt into a
permanent denial of service against a real customer, which hands the attacker a different win.
And it is checked **before** the password is considered, so a locked-out account rejects even a
correct password — otherwise it throttles only wrong guesses, and the attacker who finally
guesses right is let straight in.

The limiter sits before authentication in the pipeline, so a flood is rejected before any PBKDF2
work happens. An unlimited login endpoint backed by a deliberately expensive KDF is a CPU
amplifier as well as a guessing oracle.

### Single-use tokens, and where the guarantee actually lives

`PasswordResetToken` and `EmailVerificationToken` are 256 bits from the OS CSPRNG, stored as a
SHA-256 hash, expiring, and single-use.

**SHA-256 here, PBKDF2 for passwords, and the asymmetry is the point.** A password is
low-entropy and human-chosen, so an offline attacker can enumerate the plausible candidates —
hence a slow salted KDF. A 256-bit random token has nothing to enumerate: there is no dictionary
of likely values, so iteration count and per-row salt buy nothing, and a fast hash keeps lookup
an indexed equality match. Using PBKDF2 there would be cargo-culting the password rule into a
problem that is not the password problem.

Looking up *by hash* has a second benefit: the comparison happens inside the database on an
indexed column, so nothing in application code ever holds the stored secret next to the
presented one. There is no comparison for a later edit to "simplify" into a non-constant-time
`==`, which is exactly what `HashHelper.VerifyPassword` did.

**Single use is enforced by `AggregateRoot.Version`, not by the `IsRedeemed` check.** Two
requests presenting the same token both read it, both see `IsRedeemed` false, and both pass. What
makes exactly one of them win is the version predicate: the loser affects zero rows, raises
`DbUpdateConcurrencyException`, and `ResilientCommandDispatcher` retries in a fresh scope, where
the reload finds the token spent. Same argument as §8's oversell prevention, applied to a
different resource — and there is an integration test driving it with two contexts.

Onyx's reset token was eight characters from `new Random()` over `A-Z0-9`: about 41 bits, from a
clock-seeded non-cryptographic generator, stored in plaintext, with no expiry and no single-use
marker (**S14**). Its payment confirmation code was `Rng.Next(100000, 999999)` — twenty bits
(**S13**).

The two tokens have separate tables rather than one with a discriminator. They could share, and
sharing a table invites sharing a lookup, and a lookup that accepts either kind would let a
**verification link reset a password**. The duplication is thirty lines; the coupling would be a
privilege-escalation path.

### The plaintext has to travel, so it travels through the outbox

The database stores only a hash, so after issuance nobody — this system included — can recover
the token. Notifications cannot send a link it does not have. So `ICredentialTokenIssuer`
generates the token, stores its hash, and enqueues an integration event carrying the plaintext,
all in one method, because those three steps have to happen together or not at all.

Two things it is deliberately **not**:

- **Not a domain event handler**, which would be the natural shape. The plaintext is a bearer
  credential, and putting it on a domain event hands it to every present and future listener of
  that event — the opposite of a secret that exists once.
- **Not a direct call to a mail provider.** That would be Onyx **A12** (a transaction held open
  across external HTTP) and **A8** (fire-and-forget into a method that threw) simultaneously.

The residual risk is honest: a plaintext token sits in an outbox row for the seconds before it
is published. It is bounded — `CustomersMaintenanceJob` deletes processed messages on a
seven-day window, much shorter than the 90 days auth attempts get, and the token expires
regardless.

### Erasure is one-way, and the event is half the work

`Anonymise` overwrites the name, the email, the credential and every address line, and keeps the
row — because orders and payments reference it, and a financial record pointing at a deleted
customer is not a record of anything.

`Anonymised` is a terminal status. A system that can restore the data it promised to delete has
not deleted it.

Two details that only appear once you try it:

- The placeholder email carries the customer id (`erased-{id}@erased.invalid`). A constant would
  collide on the unique index, so the **second** erasure in the system would fail. There is a
  test.
- `CustomerAnonymisedIntegrationEvent` carries no personal data at all — publishing the erased
  name in the event announcing its erasure would leave it sitting in an outbox payload, a row in
  the same database.

Onyx's version was `_context.Customers.Remove(customer)` on an anonymous endpoint (**S2**,
**D13**): either a foreign-key failure or an orphaned order history, and not erasure either,
since the name had already been copied into every notification and report. Which is exactly why
the integration event exists — telling other modules to erase their copies is the half a
`DELETE` cannot do.

### `CreditLimit` is deleted

Onyx carried the column and never read it (**M13**). Credit terms are a real capability or they
are not; a field with no check is the speculative seam **A45** is about. Recorded in
[ROADMAP.md](ROADMAP.md) so the omission reads as a decision.

### Ownership, and the branch that fails closed

Onyx **S4** was not a missing `[Authorize]` — authentication worked. Every customer-facing action
took an id from the route (`GetById(int id)`, `GetOrders(int id)`, `Update(int id, ...)`) and
never compared it to the authenticated principal. Any signed-in customer could read or edit any
other customer's profile and order history by changing a number in the URL.

The `SelfOrAdmin` policy compares the route's `customerId` to the principal's claim. The branch
worth being deliberate about is the one where there is no route value:

```csharp
if (!Guid.TryParse(routeCustomerId, out var target) || target == Guid.Empty)
{
    context.Fail(...);      // NOT Succeed
    return Task.CompletedTask;
}
```

The tempting alternative — "no route id means this requirement does not apply, so succeed" —
turns a route that forgot its parameter, or spelled it `id` instead of `customerId`, into an
endpoint with no ownership check at all. That is **S4** reintroduced by a typo, and it would
pass every test that only exercised the happy path. So it fails, and a theory covers null,
empty, unparseable and `Guid.Empty`.

The rule is also enforced one layer in: `UpdateAddress` looks the address up inside the
customer's own collection, so an id belonging to someone else is a 404 rather than an edit. The
policy protects the route; the aggregate protects everything that is not a route.

### Why the request contract is the fix for S3, not the handler

Onyx's profile update was `Update(int id, [FromBody] Customer updated)` followed by
`Entry(existing).CurrentValues.SetValues(updated)`. The request could carry `Role`,
`PasswordHash`, `LoyaltyPoints` and `CreditLimit`, and every one of them was written.

No amount of validation inside that handler would have been safe, because **the next property
added to the entity would have been silently assignable too**. `UpdateProfileCommand` carries one
field. A contract that cannot express "make me an administrator" cannot be made to say it,
whatever an authorization policy does or does not do. Verified by sending
`{"name": "...", "role": "Admin", "status": "Active", "passwordVerifier": "x"}` at the live
endpoint: 204, name changed, role still `Customer`.

Role changes then get three independent guards — the `Admin` policy on the endpoint, the
aggregate refusing a change where the actor is the subject, and the published event carrying the
actor. The middle one matters because it holds when the command is called from a seeder, a job,
or a future endpoint whose author forgets the policy.

### The default address is a shape, not a constraint

First attempt: `Address.IsDefault`, plus a filtered unique index to enforce one per customer.

```csharp
builder.HasIndex(a => new { a.CustomerId, a.IsDefault })
    .IsUnique()
    .HasFilter("[IsDefault] = 1");      // SQL Server syntax
```

That is a provider leaking into a module whose entire seam is that it references no provider.
Making the filter conditional on the provider name would have compiled — it is what the
`DateTimeOffset` converter does — but it is the wrong trade for a *constraint*: the converter
closes a capability gap, whereas this would mean the invariant is enforced on some databases and
not others.

So the default moved to `Customer.DefaultAddressId`. One column cannot hold two values, so "at
most one default per customer" stops being a rule to check and becomes a shape that cannot
represent the violation. No index, no provider syntax, nothing to get wrong.

It is held without a foreign key, deliberately: a constraint in both directions is a circular
reference the provider has to be coaxed through on insert, and it buys nothing for a reference
*within* an aggregate — there is one writer, it always loads the whole collection, and
`RemoveAddress` is the only place a row disappears, which is why it clears the id when it does.

### PII, in logs and in the audit table

`EmailAddress.ToString()` returns `a***@example.com`. This is not decoration: `ToString()` is
what an interpolated log message reaches for, so **the unsafe thing has to be the thing that is
hard to do by accident**. Code that genuinely needs the address asks for `.Value` and says so.
`PasswordCredential.ToString()` and `SecretToken.ToString()` are the same idea.

Onyx **A42** logged
`$"Fetched customer profile: {customer.Email}, credit limit {customer.CreditLimit:C}"` — an
interpolated string, so the values were baked into the message template and structured logging
was bypassed entirely, while a full email address and a financial detail went into plaintext
logs on every profile read.

`AuthAttempt` stores a **keyed HMAC** of the client address, truncated to 16 bytes, not the
address. The operational question is "is this the same client again", which a hash answers
exactly as well, and keeping the plaintext means retaining an identifier for everyone who has
ever mistyped a password — in a table that by design also records which addresses they tried.

The key is why it is *keyed* rather than just hashed: the IPv4 space is small enough to
enumerate completely, so an unkeyed hash of an address is reversible in minutes by anyone holding
the table. And the key is **required configuration with no default**, because a security control
with a convenient fallback is one that is off in production without anybody noticing — the same
fail-fast stance `Program.cs` already takes on a missing connection string, against Onyx **S5**.

Both the attempt rows and the processed outbox messages have retention windows, enforced by
`CustomersMaintenanceJob`. A retention period nothing deletes against is a policy document, not a
control.

### One predicate, defined once, tested on every combination

`CanTransact` is `Status.IsActive && IsEmailVerified`. Sales needs the answer, so it is published
as `CustomerSummaryResponse.CanTransact` rather than left for each consumer to re-derive.

It exists twice in this module, unavoidably: as a C# property on the aggregate, and as a SQL
expression in the query handler, because a property cannot be translated. That duplication is
precisely the shape of Onyx **M18** — one availability rule written as `<=` in one place and `<`
in another, so two screens disagreed about the same product and staff raised supplier orders
they did not need. So it gets a theory over all four combinations of status and verification,
asserting the projection and the property agree, rather than a comment asking people to be
careful.

---

## 22. Promotions: nine lines of legacy code, six findings

### What is being replaced

Onyx's entire promotions capability:

```csharp
private static readonly Dictionary<string, DateTime> PromoCodes = new(StringComparer.OrdinalIgnoreCase)
{
    ["WELCOME10"] = new DateTime(2026, 12, 31),
    ["SUMMER25"]  = new DateTime(2026, 9, 1),
};

private static decimal GetPromoDiscountRate(string? promoCode)
{
    if (string.IsNullOrWhiteSpace(promoCode)) return 0m;
    if (!PromoCodes.TryGetValue(promoCode, out var expiresOn)) return 0m;

    // Codes remain valid through the end of the listed day.
    if (DateTime.Now > expiresOn) return 0m;

    return promoCode.ToUpperInvariant() switch
    {
        "WELCOME10" => 0.10m,
        "SUMMER25"  => 0.25m,
        _ => 0m
    };
}
```

Six findings, and they are worth separating because the fixes are different:

| | |
|---|---|
| **M16** | Creating a campaign meant editing C#, opening a pull request and deploying. And `SUMMER25` shipped with an expiry of 2026-09-01 — **before the audit that found it** |
| **M14** | The expiry is in a dictionary; the rate is in a `switch` keyed on the same string. Two places had to agree about one code. They did not: `WELCOME10`'s expiry was `2026-12-31` here and `2026-11-30` in the nightly report, so from December the report showed zero uptake on a code still costing margin |
| **M17** | The comment says "through the end of the listed day" and the code compares against **midnight at the start** of it, so the code dies a day early — in `DateTime.Now`, server local time, against an unspecified-kind `DateTime`, so the cutover instant moved with the deployment's timezone |
| **M15** | An unrecognised code returns `0m` and the order goes through as though a valid code had been used. A typo is silently no discount, with no error and no log |
| **§7 gaps** | No minimum spend, no usage caps, no per-customer limits, no start dates, no scope, no stacking rules, no campaign reporting |
| **A11** | The one promo endpoint handed a caller-supplied wildcard to `EF.Functions.Like` from inside a *domain service* taking an `IQueryable`, so a client could send `%` and table-scan every order |

### The window is a value object, and both ends are instants

`M17` is the most instructive of the six, because it is three bugs wearing one coat: a comment
that disagrees with the code, local time in a UTC comparison, and a boundary off by a day. All
three come from storing a **date** and reading it through a convention.

```csharp
public bool Contains(DateTimeOffset instant) =>
    instant >= StartsAtUtc && instant < EndsAtUtc;
```

Half-open, `[start, end)`, stated once in `ValidityWindow` and nowhere else. "Valid through 31
December" is expressed by the caller as `EndsAtUtc = 2027-01-01T00:00:00Z` — the instant it
means, rather than a date plus a rule about how to read it. Nothing anywhere adds a day, and
there is no second comparison to disagree with this one.

Half-open rather than closed because it composes: consecutive windows abut with no gap and no
overlap, which a closed interval cannot do without arguing about the shared instant. Same choice
`ProductPrice` makes for effective dating (§12).

### One row holds everything about a code

`M14` is not "a wrong date". It is **two places that had to agree**, which is a structural
property rather than a mistake. The fix is structural too: `UX_Promotions_Code` is unique, and
one row carries the code's rate, window, minimum spend, caps, scope and stacking policy
together. There is no second location to diverge from.

`PromotionCode` normalises to upper case and strips whitespace — every kind, not just the ends,
because customers paste codes out of emails and a trailing non-breaking space should not be a
support ticket. With the unique index, `welcome10` and `" WELCOME10 "` are the same promotion.

### Failures name their reason — the opposite of Phase 1

Phase 1 collapsed every authentication failure into one response, because distinguishing them
turns a login endpoint into an account-enumeration oracle. Here the rule is inverted, and the
contrast is the point:

**A promotion code is not a credential.** The customer typed it themselves, they know they typed
it, and "your basket is below the $50 minimum" is the entire value of the response. Being vague
would cost a support ticket and protect nothing.

So `Evaluate` returns `CodeNotRecognised`, `NotStartedYet`, `Expired`, `MinimumSpendNotMet`,
`UsageCapReached`, `CustomerCapReached`, `NotApplicableToBasket` or `CurrencyMismatch` — each its
own code, each its own message. `M15` returned `0m` for all of them.

One exception: a **Draft or Paused** campaign reports as `CodeNotRecognised` rather than as its
real status. Internal campaign state is not something a shopper can act on, and a code that is
not yet live should not be confirmable as existing before its start date.

### Two caps, two mechanisms, because they have different races

This is the part of the module worth the most attention. A usage cap is a count, and
`if (count < cap)` has a gap between the look and the leap.

**The total cap: the concurrency token.** `RedemptionCount` is state on the aggregate — the same
state-versus-log split as `Product.Stock`/`StockMovement` and
`PasswordCredential`/`AuthAttempt`. Two checkouts racing for the last use of a campaign both
read 99, both pass the check, and the version predicate makes exactly one win: the loser's
`UPDATE` affects zero rows, `ResilientCommandDispatcher` retries in a fresh scope, and the reload
returns `UsageCapReached`. Identical in shape to Catalog's oversell prevention (§8); Onyx **D1**
is the same read-compare-write with no token, where both writers "succeeded" and four units sold
from three.

**The per-customer cap: a unique index over an ordinal.** A count cannot be a unique index — so
the ledger row carries one:

```
UX_PromotionRedemptions_CustomerOrdinal  (PromotionId, CustomerId, CustomerOrdinal)
```

`CustomerOrdinal` is this customer's n-th use, computed by the handler as "uses so far plus
one". Two concurrent reservations both read the same count, both compute the same ordinal, and
collide at the database. One wins; the loser is retried and reads the new count.

The obvious alternative is a filtered unique index on `(PromotionId, CustomerId)` for the
cap-of-one case. It needs provider-specific SQL in a module that deliberately references no
provider, **and it only works for a cap of one**. That exact mistake was made and reverted in
Phase 1's address defaulting, which is why it was recognised on sight here. The ordinal
generalises to any N and stays provider-agnostic.

**The third index** is `(PromotionId, CheckoutSessionId)`, unique — idempotency on the
reference, the same role `StockMovement`'s three-column index plays in Catalog. A retried
checkout is a no-op rather than a second use, which is what makes retry the cheapest recovery
layer in the checkout saga. Onyx **M5** is the counter-example: its payment retry sent no
idempotency key at all, while the PR claimed the gateway call was idempotent (**X9**).

### Reserve and release are separate from validate

A quote is shown many times and consumed once.

- `ValidatePromotionQuery` — a query. No transaction, no write. A basket page calls it on every
  change.
- `ReservePromotionCommand` — claims a use, on checkout confirmation only.
- `ReleasePromotionCommand` — gives every use on a session back.

If validation reserved, the last uses of a limited campaign would be burned by people browsing.

`Release` is deliberately **safe to call unconditionally** and takes a session id rather than a
promotion id. Sales' expiry sweeper is told a session and fires blind — it does not know, and
should not have to ask, whether that session reserved anything. A session holding nothing is a
success returning zero releases, exactly as `RestoreStockCommand` is a no-op when no stock was
held. That property is why it is the layer the saga actually relies on: inline compensation can
fail for the same reason the step it is compensating for failed; a sweeper that can be run
repeatedly cannot.

Making the caller enumerate which promotions were on the session would mean Sales keeping its own
copy of that list — two modules holding one fact, which is how **M14** happened.

And a release **does not reopen a closed campaign.** The slot returns to the count, so a limited
campaign is not permanently short because somebody abandoned a basket, but a human decides
whether to run it again. Automatic reopening would make a "first 100 customers" campaign's end
date unpredictable.

### Scope changes the basis, not just the eligibility

"20% off headphones" against a basket of headphones and a laptop must discount the headphones —
not a fifth of the laptop. So a scoped promotion computes against the sum of **its own** matching
lines, and `Promotion.ScopedSubtotal` lives on the aggregate because that is the scope's meaning
rather than the caller's.

Onyx applied every discount to the whole subtotal, because the whole subtotal was the only number
it had.

The minimum spend is tested against the **whole** merchandise subtotal even for a scoped
promotion: "spend 100, get 20% off headphones" is the normal reading, and testing it against only
the scoped lines would make it a different offer. Two bases, each named, each tested.

**There is no category scope**, and finding out why produced [bug 11](BUGS-AND-LESSONS.md).
`ISSUES.md` §7 lists "product/category scope" as a gap, and `Product` has no category — Catalog's
own gap list says so. Building it would have meant either inventing a category concept Promotions
cannot ask Catalog about, or adding a property to `Product` so the enum could have a third case.
The second is what had already happened to the documentation: `CONCEPTS.md` §11 described an
`IX_Products_CategoryId` index, with a code snippet, for a column that does not exist.

### Currency, and the FX question not answered

A **percentage** discount is currency-agnostic: 10% off is 10% off in USD and AED. A
**fixed-amount** discount is denominated, and "50 off" does not mean the same thing in two
currencies — applying a USD code to an AED basket would hand out roughly 3.7 times the intended
discount.

So `Discount.AppliesToCurrency` refuses the mismatch, and a minimum spend is denominated even
when the discount is a percentage, because "over 50" also means two different things.

Converting at an exchange rate is deliberately not built. A rate has to come from somewhere, be
effective-dated and be auditable, and `Country` carries one currency per country with no FX at
all (§18). A fixed-amount campaign that should run in both currencies is two promotions — which
is also how a merchandiser would want to price it.

### The arithmetic, and the capping

```csharp
var raw = Kind switch
{
    DiscountKind.Percentage => basis.MultiplyBy(Rate),
    _ => Amount!
};

return raw.Amount > basis.Amount ? basis : raw;
```

Everything is `Money` — `decimal`, immutable, rounded half-up to cash on every operation — so
15% of 10.10 is 1.52, once, rather than 1.515 carried into the next step. Onyx cast `decimal` to
`double`, accumulated in floating point, cast back, and never rounded; a real order total was
`83.096766` (**M6**, **M7**).

**Capped at the basis**, which matters more than it looks. A "50 off" code on a 30 basket yields
30, not 50 — the alternative is a negative subtotal flowing into the shipping threshold and the
tax base, producing a total nobody can explain. Capping in the value object means every caller
gets it, and there is a boundary test for the exactly-equal case.

One configuration detail worth the note it carries in the code: the rate column is
`precision(9,4)`, not the model-wide `(18,2)`. The money convention is right for money and wrong
for a multiplier — it would have rounded `0.075` to `0.08` and quietly turned a 7.5% campaign
into an 8% one. Same class of mistake as the 500-character string convention landing on the
outbox payload ([bug 9](BUGS-AND-LESSONS.md)), caught this time by asking what the column
actually holds.

### Stacking is enforced at reservation, in both directions

`ISSUES.md` §7 names "stacking rules" as a gap, and this is the whole of it: a promotion either
combines with others or it does not.

It is checked at **reservation** time, where the ledger can see what else the session holds — so
it works regardless of how many codes the checkout UI chooses to accept, and does not depend on
a decision Sales has not made yet. Both directions are checked: an `Exclusive` code already on
the session rejects a `Combinable` newcomer, because "exclusive" has to mean nothing else may
join it, not merely that it may not join others.

`Exclusive` is the default on creation. A merchandiser who has not thought about stacking has
almost certainly not intended "combine this 30% code with every other 30% code", and the failure
direction matters: the strict default costs a conversation, the permissive one costs margin.

### Draft, and why live terms are frozen

`Promotion.Reconfigure` refuses anything that is not a `Draft`. That is a business rule rather
than caution: a live campaign's terms are something customers may already have been shown and
acted on, and silently changing the minimum spend under them is the same class of wrong as
**A6** — showing one price and charging another. To change a live campaign, close it and create
its successor; the ledger then attributes each redemption to the terms that were actually in
force.

`Closed` is terminal for a related reason. Reopening an exhausted campaign would let a "first 100
customers" promotion serve 150, which is quiet promise-breaking and worse than refusing.

`Activate` also refuses a window that has **already ended** — which is **M16** stated as an
invariant rather than a warning. Onyx had no state that could have refused, so `SUMMER25` was
live and already expired from the moment it was written.

### The expiry job records a fact; it does not create one

`Promotion.Evaluate` already refuses an expired promotion, so customers cannot use one whether
or not the sweeper runs. **The customer-facing path does not depend on the job.** What the job
buys is an accurate *status*: without it, every finished campaign sits in the admin list as
`Active` forever and "which campaigns are running?" has no readable answer.

That distinction is worth stating because it is the exact opposite of Onyx **D5**, where the
background job was load-bearing in the worst way — its stock reconciliation added 50 units from
nowhere every 15 minutes, silently healing every oversell before anyone could see one. Here the
job only writes down something already true, and invents nothing.

Writing it is also what surfaced [bug 10](BUGS-AND-LESSONS.md): background jobs were raising
domain events that nothing dispatched, in four places across two Catalog jobs, harmlessly only
because nothing yet subscribed to them.

### Reporting, which Onyx could not have built

`ISSUES.md` §7 lists campaign performance reporting as a missing capability, and it was not
missing by oversight — it was **unbuildable**. A promo code was a nullable string on the order,
and nothing recorded whether it had been applied, abandoned or refunded.

A release marks its ledger row rather than deleting it, which makes three numbers separable:

| | |
|---|---|
| `liveRedemptions` | uses currently held |
| `releasedRedemptions` | uses given back |
| `distinctCustomers` | how many people |

`releasedRedemptions` is the one Onyx could never have produced. It is the difference between
"100 people used this" and "140 tried and 40 abandoned".

Aggregated in SQL, in one round trip, with the empty case guarded — a campaign nobody has used
yet is the normal state of one on its first day, and **A18** is what happens when a report
assumes otherwise: an unguarded divide by a count of zero that terminated the whole worker
process and took the unrelated stock job down with it.

---

## 23. Loyalty: a counter with no log, and what replaces it

### What is being replaced

Onyx's entire loyalty scheme was one `int` on `Customer` and one method:

```csharp
public async Task RedeemLoyaltyPointsAsync(int customerId, int points)
{
    var customer = await _context.Customers.FirstOrDefaultAsync(c => c.Id == customerId);
    if (customer.LoyaltyPoints < points) throw new InvalidOperationException("Not enough loyalty points");
    customer.LoyaltyPoints -= points;
    await _context.SaveChangesAsync();
}
```

called from `OrdersController.Create` like this:

```csharp
order.Total = _pricing.CalculateTotal(order);     // priced WITHOUT reference to the points
await _context.SaveChangesAsync();                // committed

if (request.RedeemPoints is > 0)
{
    await _inventory.RedeemLoyaltyPointsAsync(request.CustomerId, request.RedeemPoints.Value);
}
```

Three findings, and each one is a different kind of wrong:

| | |
|---|---|
| **M11** *(Critical)* | The order is priced and committed **before** the points are subtracted, so the points buy nothing. They are destroyed. And the number redeemed is written nowhere — not on the order, not anywhere — so, in the register's words, "there is no way to answer 'where did my points go?'" |
| **M12** | No code path anywhere **increases** the column. The only balances in existence were typed into `DbSeeder` — 500, 120, 40. A new customer started at zero and stayed there permanently: a scheme with a spend side and no earn side |
| **D4** | The sufficiency check throws **after** the order and every stock decrement are committed. The caller gets a 500; the order and the inventory movement stand |

Plus five §7 capability gaps: earning, points with actual monetary value, expiry, a statement, and reversal on refund.

### The balance is a column — and the roadmap said it should not be

This is the one place in the build so far where implementing the plan changed it, so it is worth
being explicit.

The roadmap for this phase said *"the balance is a `SUM`, never a column (M11, M12)"*. That
instinct was aimed at Onyx's counter-with-no-log, and it is half right. Building it exposed the
other half: **a `SUM` cannot be the subject of a concurrency token.** Two simultaneous
redemptions of the last 500 points both compute the same `SUM`, both pass the sufficiency check,
and both commit — spending 1,000 points from a balance of 500. That is Onyx **D1**'s exact shape
applied to money instead of stock.

So the balance is state on the account row, under `AggregateRoot.Version`, and the ledger is the
immutable log — the same arrangement Catalog uses for `Product.Stock` against `StockMovement`,
and that Promotions uses for `RedemptionCount` against `PromotionRedemption`. Consistency with
the rest of the codebase turned out to be the correct answer rather than merely the tidy one.

**What makes the counter trustworthy is not that it is the source of truth — it is that something
proves it matches the log.** `PointsIntegrityCheckJob` asserts `SUM(Delta) == Balance` on every
pass, reports discrepancies into `JobRun.ItemsFailed`, and **never corrects them**. That last
word is the whole difference from Onyx: it had a counter, no log, and nothing checking anything.
Its one reconciliation job (**D5**) silently healed every discrepancy it found by adding 50 units
from nowhere, so the oversells it was papering over were invisible to everybody.

There is an integration test that performs eight movements of six different kinds and asserts the
two agree:

```
500 + 300 - 400 + 400 - 200 - 300 - 100 + 50 = 250
```

### Points are worth money, which is the whole of M11

`PointsValue` is the only place that converts, and the rate is **100 points to one unit of
currency** — so one point is exactly one cent.

That rate is chosen so **the conversion can never round**. Both supported currencies are
2-decimal, so every whole number of points maps to an exact cash amount and back: 150 points is
1.50, never 1.4999. A rate like "3 points per unit" would make 100 points worth 33.333…, and
that fraction then has to be rounded somewhere, in a direction somebody has to decide, on a
number the customer can check. Onyx rounded nowhere and produced order totals like `83.096766`
(**M7**); the cheaper fix is to pick a rate with no fraction to round. A test round-trips every
value from 0 to 999.

A point is worth one cent of **whatever the basket is priced in** — a deliberate simplification
of the same kind `Country` makes about tax, not an oversight. It means a point is worth more in
one market than another; the alternative needs an effective-dated, auditable FX rate this system
does not have (§18), and this is the one place a per-currency rate would go.

### Earning, and what counts as qualifying spend

`EarnRule`: one point per whole unit of qualifying spend, truncated. A 49.99 order earns 49
points, worth 0.49 back — a 0.5% effective return.

**Truncated rather than rounded**, in the house's favour by at most one point. Rounding up means
49.50 earns 50, which is fine until somebody notices that splitting an order into halves earns
more than placing it once — and gaming a rounding rule is a real thing customers do.

The interesting half is the definition of qualifying spend: **merchandise after discounts,
excluding tax and shipping.** Each exclusion is a decision:

- **Tax** is not revenue. It is money collected on behalf of a government, and paying loyalty on
  it means funding a reward out of somebody else's money.
- **Shipping** is a cost passed through, so earning on it rewards buying from further away.
- **Discounts come off first**, because earning on the pre-discount figure means a promotion
  costs margin twice — once as the discount, and again as points earned on money nobody paid.
  Onyx handed out a 5% volume discount and promo codes with no cost price recorded anywhere, so
  it could not have noticed either.

### Redemption in two steps, which is the D4 fix

| | |
|---|---|
| `ValidatePointsRedemptionQuery` | A query. No transaction, no write. The quote calls it, gets a priced discount or a reason |
| `RedeemPointsCommand` | The debit. Commits in the transaction that confirms the session, idempotent on the session id |

Onyx's failure was the ordering: it checked sufficiency *inside* the debit, which ran after the
order was committed, so an insufficient balance was a 500 with the order and all its stock
decrements standing.

Both paths call **the same rule**, `PointsAccount.CanRedeem`. There is a test that walks every
combination of four balances and seven requested amounts and asserts the check and the debit
agree on all 28 — because a quote that promises a discount the debit then refuses is Onyx
**M18**, one availability rule written twice as `<=` and `<`, with two screens disagreeing.

The debit also **re-checks** sufficiency, because the quote was shown earlier and the customer
may have spent points elsewhere since. The concurrency token is what makes that second check
safe against a simultaneous one.

### Expiry without tracking individual earning batches

Points expire twelve months after being earned, consumed oldest-first. The obvious
implementation is lot tracking: a `PointsLot` child entity per earning with a `RemainingPoints`
column, decremented on redemption. That is what most schemes do, and it adds a third thing that
has to stay consistent with the other two.

FIFO consumption makes it unnecessary. **Because points are consumed oldest-first, whatever is
left in an account is always the most recently credited** — so the amount still alive from before
a cutoff is exactly:

```csharp
public static int ExpirableBefore(int creditedBeforeCutoff, int consumedTotal) =>
    Math.Max(0, creditedBeforeCutoff - consumedTotal);
```

Two indexed sums over the ledger. No lot entities, no per-batch counters, and nothing mutable on
a ledger row. A theory covers the six interesting cases including both clamps.

Expiry writes a **negative ledger entry** rather than removing the earning it lapses, and is
clamped to the balance — so a miscomputed cutoff cannot drive the balance negative even if the
job's arithmetic were wrong.

One consequence worth naming: a `Returned` credit counts as credited *now* rather than when the
points were originally earned, so points given back after a failed checkout get a fresh twelve
months. That is slightly generous and deliberately so — the customer is being handed back points
they tried to spend on a checkout that failed for our reasons, and also shortening their life
would be charging them for our failure. There is a test asserting exactly that case.

### The expiry job is load-bearing, and the integrity job is not

Worth contrasting, because the two jobs in this module sit on opposite sides of a line the
previous phase drew.

`PromotionExpiryJob` only records a status that `Evaluate` already enforces: the
customer-facing path is correct whether or not it runs. `PointsExpiryJob` is different —
**nothing else writes off lapsed points**, so if it never runs the liability never ends.

That makes it the first genuinely load-bearing background job in the codebase, and it is the
opposite of Onyx **D5** in the direction that matters: that job *invented* stock from nowhere and
hid a defect; this one only ever removes value the scheme already said had a deadline, and writes
a ledger row saying it did.

### The first real use of the inbox

Phase 0 built a per-module inbox for at-least-once delivery and nothing consumed a cross-module
event until now. Loyalty is the first module to subscribe to another's:

| Event | Handler | What it does |
|---|---|---|
| `CustomerRegisteredIntegrationEvent` | `OnCustomerRegisteredOpenPointsAccount` | Opens the account |
| `CustomerAnonymisedIntegrationEvent` | `OnCustomerAnonymisedClosePointsAccount` | Closes it, forfeiting what remains |

Both follow the same three-step shape, and all three steps matter:

```csharp
if (!await inbox.TryClaimAsync(notification, GetType(), ct)) return;   // 1
// ... the work                                                        // 2
await unitOfWork.SaveAndDispatchAsync(ct);                             // 3
```

1. The claim is **added, not saved**, so it commits with the work below — the alternative allows
   a crash between the two, leaving an event marked consumed with none of its effects applied,
   permanently, because the claim suppresses every retry.
2. Two guards, deliberately: the claim catches the ordinary redelivery, and
   `UX_PointsAccounts_CustomerId` catches two deliveries racing past the claim's read.
3. `SaveAndDispatchAsync`, **not** `SaveChangesAsync` — an integration event handler is not
   dispatched through the MediatR request pipeline (the outbox publishes it as a notification),
   so no transaction is open and nothing would drain the events the aggregate raised. That is
   [bug 10](BUGS-AND-LESSONS.md), and the guard added there now throws rather than letting it
   pass silently.

Verified live: registering a customer produced a points account in another module's database
within two seconds, with the claim row naming the consumer; anonymising them closed it.

The second handler is also the other half of an erasure request. Customers can overwrite its own
name and address columns and cannot reach into another module's database — Onyx **D13** deleted
the customer row and left every copy standing elsewhere. Note what is *not* erased: the ledger.
It is a financial record of value given and taken, and it holds a customer id and nothing else
personal, so it survives while the balance goes to zero through a recorded `Forfeited` entry.

### The statement, which Onyx could not have built

`ISSUES.md` §7 lists a points statement as a missing capability, and it was unbuildable rather
than merely unbuilt: the balance was a single mutable `int` with no history, so there was nothing
to list.

Every ledger row names its reason — `Earned`, `Redeemed`, `Returned`, `Expired`, `Reversed`,
`Adjusted`, `Forfeited` — the thing that caused it, and the balance it left behind. **Nothing is
ever deleted or updated**: expiry writes a negative row rather than removing the earning it
lapses, and a reversal writes one rather than editing the earning it reverses. A balance that can
be reconstructed by replaying the log is a balance a customer can be shown an explanation of —
and `PointsIntegrityCheckJob` asserts that it reconstructs.

### Clamping, and the concept this scheme does not have

Three operations can legitimately ask for more points than exist, and all three clamp:

- **Reversal on refund.** A customer who earned 500 and spent 400 has 100 left; a refund cannot
  claw back what is already gone.
- **Expiry**, as the structural backstop described above.
- **A negative adjustment**, so a mistyped correction cannot go through the floor.

Each clamps to the balance rather than carrying a shortfall as a debt, because **a negative
loyalty balance is a concept this scheme does not have**, and inventing one to handle a refund
edge case would be worse than absorbing the difference. The private `Debit` primitive refuses
anything over the balance outright, so a caller that forgets to clamp fails loudly instead of
producing one.

### Two things deliberately not built

**No endpoint grants, redeems, releases or reverses.** Those are module-to-module commands the
checkout and refund flows send. An HTTP route that credits points would be the same category of
mistake as Onyx **M2**, where the client supplied the amount to charge.

**Earning was a command here and became an event subscription in Phase 4.** This project's rule
is *command when the caller needs the answer, event when it does not* — and earning is squarely
the second case: nobody waits, it cannot fail for a business reason, and it must be guaranteed. So
the shape it wanted was Loyalty subscribing to Sales' `OrderPaidIntegrationEvent` through the
inbox, exactly as it already subscribes to Customers'. Sales did not exist, and a module cannot
subscribe to a contract that has not been written. `GrantPointsCommand` was published anyway
because it is needed regardless — an administrator granting goodwill points uses the same path —
and the prediction recorded here and in `ROADMAP.md` was that Phase 4 would add a thin
inbox-guarded handler calling it, and that the handler would be the only new code.

**That is exactly what it took.** `OnOrderPaidGrantPoints` is one handler, no new rule, and
`OnOrderRefundedReverseEarnedPoints` closes the reversal half of **M12** the same way. The earn
side needing nothing but a subscription is the clearest evidence this seam was drawn in the right
place — and it is worth noticing that the gap was *recorded* rather than implied, which is why
closing it was a five-minute job rather than an archaeology exercise.

---

## 24. Selling: the seven-step quote and the three-transaction sale

Everything in the four phases before this one was building toward one endpoint. This is it.

### What is being replaced

Onyx had **one** selling endpoint — `POST /api/orders`, with a `CreateOrderRequest` carrying
product ids, quantities, a promo code, a number of loyalty points to redeem *and* a shipping
address. Its handler created the order row, then priced it, then deducted stock, then charged the
card, committing after each step. There was no way to see a price before committing to buy, no way
to retry anything, and a failure at step four left the first three standing.

Fourteen findings live in that one method and the pricing service it called. The two that matter
most are both arithmetic:

```csharp
// OrderPricingService.CalculateTotal
double subtotal = 0;
foreach (var line in order.Lines) subtotal += (double)line.UnitPrice * line.Quantity;
if (subtotal > 75) subtotal *= 0.95;              // volume discount,  > 75
subtotal -= subtotal * discountRate;              // promo
var shipping = Money.Zero();
shipping.Amount = subtotal >= 75 ? 0m : 5.99m;    // free shipping,   >= 75, on the DISCOUNTED figure
var taxed = subtotal * 1.08;                      // shipping excluded from tax
return (decimal)taxed + shipping.Amount;
```

Six findings in nine lines: **M6** (decimal cast to double and back), **M7** (never rounded — a
real order total in the register is `83.096766`), **M8** (shipping decided after the discount),
**M9** (the same `75` with `>` in one rule and `>=` in the other), **M10** (shipping outside the
tax base, undocumented), **A43** (`Money.Zero()` then `shipping.Amount = …`, discarding the
currency).

### The three calls, and why the split is the design

```
POST /api/checkouts                  prices the basket, returns a session id. Holds NOTHING.
GET  /api/checkouts/{id}             the quote, with every adjustment and its basis.
POST /api/checkouts/{id}/confirm     holds stock + promotion + points, creates the order. 202.
```

The first call can be made as many times as the customer likes and costs nothing. The second
answers "why is my total this?" **before** anyone commits to anything. Only the third touches
another module. Onyx's single call could not offer any of that, which is why "see the price first"
was not a feature it was missing so much as a shape it could not express.

**202 Accepted, not 201 Created.** A confirmed checkout is an order that exists and is not yet
paid. Onyx **A9** dispatched its payment-succeeded notification before the payment row was even
added to the context, so a rollback left a customer holding a confirmation for a transaction that
never happened. The status code now says what is true.

### The seven steps

`QuoteCalculator.Calculate` is a **pure static function** — no repository, no `IQueryable`, no
clock, no I/O. Onyx's "domain service" took an `IQueryable<Order>` and called
`EF.Functions.Like` (**A11**), so no rule in it could be exercised without a database and
consequently none of them were. Purity is why every rule below is property-testable.

| # | step | basis | closes |
|---|---|---|---|
| 1 | line extended = unit × quantity, **rounded per line** | — | M6, M7 |
| 2 | `MerchandiseSubtotal` = Σ lines | — | M6 |
| 3 | promotion discount | the merchandise subtotal | — |
| 4 | loyalty discount, floored at zero | the merchandise subtotal | M11 |
| 5 | shipping | **the pre-discount merchandise subtotal** | **M8**, **M9** |
| 6 | tax | discounted merchandise **+ shipping where the policy says shipping is taxable** | **M10** |
| 7 | total = discounted merchandise + shipping + tax | — | M19 |

**Step 5 is the whole of M8, and it is one argument.** Onyx tested the free-shipping threshold
against the *post-discount* figure. Evaluating it against the pre-discount merchandise subtotal
fixes it because that figure rises monotonically with what the customer puts in the basket, and no
later step can pull it back below the threshold.

**Step 6 is the whole of M10, and it is one named boolean.** The register's complaint is precise:
Onyx applied tax to goods and added shipping afterwards, and "this may be intentional for the
jurisdiction, but it is undocumented and untested, so it cannot be distinguished from a bug." So
`ShippingPolicy.ShippingIsTaxable` is a stated per-country decision with the jurisdiction rule
written next to it — most US states tax the delivery charge when the goods are taxable; UAE VAT
treats delivery as part of the consideration — and the tax step reads it rather than assuming
either way.

### Stating M8's property correctly was most of the work

M8's obligation line asks for "a monotonicity property test over the total". The obvious phrasing
is *"the total never falls as the basket grows"*, and **that property is false by design**: at the
free-shipping threshold the total drops by the delivery charge, because that is what a free-
delivery offer *is*. $74.99 costs $87.46 and $75.00 costs $81.00, and both figures are correct.
The first version of the test asserted the obvious phrasing and failed immediately — on correct
code.

What actually distinguishes the fix from the bug is piecewise. Onyx's two rules shared the figure
`75` with different operators against different bases, so the discount switching on at $76 pushed
the basket back under the shipping threshold and the total went **up and then came back down**:

```
$75.00 -> $81.00
$76.00 -> $83.97     <- up
$80.00 -> $82.08     <- and back down
```

A bump, not a step. Every subtotal in `(75.00, 78.95)` fell into it, so the free-shipping
incentive actively worked against the business there. Three properties, all exhaustive over
0–500 in 1-cent steps and all three in the suite:

1. **Within a shipping band the total never falls.** A bump violates this; a step does not.
2. **The total drops in exactly one place, and that place is the stated threshold** — bounded by
   the delivery charge plus the tax on it, so a drop cannot be concealing a second defect.
3. **A basket ships free if and only if its pre-discount merchandise reaches the threshold** — for
   every discount, including one large enough to take the basket to nothing. This is the one that
   *is* M8, stated without reference to monotonicity at all.

Exhaustive stepping rather than randomised sampling, because 50,000 baskets through a pure
function take well under a second and leave nothing to luck.

### The sale: three transactions, no distributed transaction

```
TX 0 (Sales)     CheckoutSession, Open, ExpiresAt = now + 20m        <- the anchor
TX 1 (others)    DeductStock / ReservePromotion / RedeemPoints       <- all named by session id
TX 2 (Sales)     Order(PaymentPending) + session.Confirm(orderId)    <- 202 Accepted
```

**The session commits first**, and that is the subtle part. If it were created in TX 2, a TX 2
failure would leave stock held against a session id that exists nowhere — invisible, permanent
loss of sellable inventory, with nothing able to find it because the only handle to it was never
written. Committing first means every intermediate state is enumerable.

**Order before confirm, within TX 2.** `Order.PlaceFrom` requires the session to be `Holding`, not
`Confirmed`, and `session.Confirm(orderId)` takes the order's id. The first draft had it the other
way round and the handler had to call `session.Confirm(Guid.Empty, …)` before an order id existed
— a sentinel standing in for a fact that had not happened yet, which is always a sign the ordering
is wrong.

**No invariant crosses a boundary.** "Never oversell" is a statement about Catalog's own data;
Catalog enforces it alone, in its own transaction, and the order's existence is irrelevant to
whether stock is correct. What makes the sequencing safe is not atomicity but the four layers in
§13 — and layer 2, the expiry sweeper, is the one the design actually leans on, because inline
compensation runs in the process that may be the thing that just died.

### The order number

`BC-00000123`, from a **singleton sequence row under a concurrency token**. Two processes
allocating at the same instant both read the same value; the loser's update matches zero rows, EF
throws, and `ICommandDispatcher` retries in a fresh scope with a fresh context.

**Gaps are tolerated deliberately.** A retried transaction consumes a number it does not use, so
the sequence has holes. The alternative — guaranteeing density — means a second write to reclaim
numbers, under contention, for a cosmetic property nobody has asked for. Onyx's version was a
`static int` that reset to 1000 on every restart and was per-process, so numbers repeated across
restarts and across instances — and nothing would have noticed, because the number was never
written to the order at all, only logged (**D8**).

### The state machine, and the endpoint that does not exist

Onyx exposed `PUT /api/orders/{id}/status` taking an unvalidated string with no ownership check,
so any authenticated caller could move their own order to `Paid` (**S7**). Here the status is
reached only through named transitions on the aggregate, and **`MarkOrderPaidCommand` has no HTTP
route at all** — that absence is **M1** ("the payment aggregate transitions on the provider's
answer and on nothing else") and **M4** ("the provider reference is nullable and set only from a
provider response") enforced structurally rather than by validation.

Partial shipment is per-item `QuantityShipped`, and the order status is **derived** from the items
rather than stored beside them: anything outstanding means `Fulfilling`, nothing outstanding means
`Shipped`. Onyx's single order-level string could not represent a two-parcel order, which is why
`ISSUES.md` §7 lists partial shipment as a missing capability.

### A nullable unique column, and why the capture became a row

`PaymentReference` started as a nullable column on `Order`, and the idempotency check in front of
it was a bare read — the exact shape this project criticises everywhere else. It needs a unique
index, and a nullable unique column does not work: SQL Server treats two `NULL`s as equal in a
unique index, so **exactly one unpaid order could exist**. The usual workaround is a filtered
index (`WHERE [PaymentReference] IS NOT NULL`), which is provider-specific SQL written into a
module whose whole seam is `AddSalesModule(Action<DbContextOptionsBuilder>)`.

So the capture became `OrderPayment` — a row that either exists or does not, with a non-nullable
reference and a plain unique index that works on any provider. It is the third time this project
has reached for that move: `Customer.DefaultAddressId` replaced a filtered index on `IsDefault`,
`CustomerOrdinal` replaced one in Promotions, and now this. **When a nullable flag needs a
constraint, remodel it rather than filter the index.** It also sets Phase 5 up properly: failed
and partial captures are more rows.

### What the quote response carries, and why

Onyx stored one `decimal Total` computed by a single expression with four interleaved rules, so
nothing could say which rule produced which part of it (**M19**). The response here carries the
lines, the adjustments, **and each adjustment's basis** — so the total is reconstructible by
adding the rows up, and "why is my tax $6.14?" is answerable from the response itself rather than
from the source.

The order copies all of it **verbatim** rather than recomputing it. Re-running the pipeline at
the sale would be a second place that prices a basket, and the two could disagree — Onyx **A6**'s
shape ("browsing and buying read prices from different sources") moved one layer in. The session
is the authority for what this costs; the order records what it cost. The copy is one-directional
and nothing ever writes back, which is also what lets a receipt reprinted in two years not depend
on a swept session row.

### The release fan-out: who subscribes, and who is told

Two shapes appear here and the difference is principled rather than stylistic.

**Sales orchestrates the releases.** `CheckoutAbandoned` and `OrderCancelled` are consumed by
Sales itself, which then sends `RestoreStockCommand`, `ReleasePromotionCommand` and
`ReleasePointsCommand`. Sales is the only module that knows what a checkout *held* — which lines,
whether a promotion applied, how many points. Putting a `CheckoutAbandoned` subscriber in Catalog
would mean Catalog referencing `Sales.Contracts` and then asking Sales what the lines were. This
way the dependency arrows all point *out* of Sales, and Catalog, Promotions and Loyalty stay
completely ignorant that Sales exists.

**Loyalty subscribes directly** to `OrderPaid` and `OrderRefunded`. A grant or a reversal needs
nothing but the customer and the order, both carried on the event, and Loyalty's own ledger holds
the rest. Where a consumer can act on the event alone, it subscribes to the event alone. The
asymmetry tracks **who owns the missing information**, not taste.

**And the earn rule finally has a trigger.** Loyalty shipped in Phase 3 with `EarnRule`, the
ledger, the idempotency index and `GrantPointsCommand` fully tested — and nothing that called
them, because a module cannot subscribe to a contract that has not been written. Closing it took
**one inbox-guarded handler and no new rule**. That the earn side needed nothing but a
subscription is the clearest evidence the seam was drawn in the right place.

### Recovery layer 3: the handshake neither module could make alone

`sales.orphan-reconciliation` finds stock held against a session that does not exist — the case
layer 2 structurally cannot see, because it enumerates *sessions*.

Neither module can answer it alone. Catalog can list references whose stock nets negative; it
cannot tell a completed sale from an orphan, because the reference is a session id it does not
own. Sales knows what every session id means; it cannot enumerate holds, because it does not own
stock. So **Catalog reports, Sales decides, and the decision is a command back into Catalog** —
no shared table, no module reaching into another's schema, and the
`UnreconciledStockHold`/`CheckoutSessionOutcome` pair is the whole of the coupling. Catalog's
README has carried this sweep as "deferred until Sales exists" since Phase 0; this is it.

Its verdicts are where the care goes, because a job like this can destroy real inventory data if
it guesses: a **confirmed** session's stock is meant to be gone, so leave it; a **live** one is a
customer checking out right now, so leave it and log the surprise; a **finished** one that was
never released is layer 2 having failed silently, so release it and warn; **no session at all** is
the true orphan, so release it and log an error, because it is impossible under the designed
ordering and a human needs to know the ordering assumption is wrong.

### The bug this phase found in its own code

The end-to-end run caught a release handler restoring a basket's lines for a session that had
never held anything — **two units of inventory created out of a cancelled basket**. Onyx **D5**,
by a different road, inside a handler that cites D5.

Idempotency did not catch it and could not have: the unique index stops an operation being
*repeated*, and this was an operation being performed *wrongly*, once. The integrity check could
not have either, because the counter and the log agreed — both said 10. Agreement between a
counter and its log proves consistency; it says nothing about whether either is right.

Fixed in both modules: Sales records `HoldsPlacedAtUtc` and does not ask, and
`RestoreStockCommandHandler` caps every release at `SUM(Delta)` for that reference and so cannot
comply. The second is the one that matters, and it belongs in Catalog for a reason worth stating:
**"never invent stock" is Catalog's own invariant, and an invariant enforced by trusting the
caller is not enforced.** Full write-up in [BUGS-AND-LESSONS.md](BUGS-AND-LESSONS.md) section 13.

**Say out loud:** *"Pricing is seven named steps in one pure function, and the shipping threshold
is evaluated against the pre-discount merchandise subtotal — that single argument is the fix for
the non-monotonic totals. The sale is three sequenced transactions anchored on a checkout session
that commits first, so every intermediate state is enumerable, with four layers of recovery
instead of a distributed transaction. And the end-to-end run found a release that invented
inventory, which is why Catalog now caps every restore at what its own log says was taken."*

---

## 25. Payments: four Critical findings in one method

### What is being replaced

`PaymentsController.Submit`. Nine findings, four of them Critical, and they are all visible in
twelve lines:

```csharp
using var tx = _context.Database.BeginTransaction();          // A12

PaymentGatewayResult result = null;
for (var attempt = 0; attempt < 3; attempt++)                 // M5: no idempotency key
{
    try { result = _gateway.ChargeAsync(...).Result; if (result.Success) break; }
    catch { }                                                 // A16
}

// result may be null, or Success == false — neither is checked
var payment = new Payment
{
    Status = "Paid",                                          // M1, A39
    GatewayReference = result?.GatewayReference ?? confirmationCode   // M4, S13
};
order.Status = OrderStatus.Paid;
tx.Commit();
```

And inside `PaymentGatewayClient.ChargeAsync`:

```csharp
var handler = new HttpClientHandler                           // A14: per call, in a retry loop
{
    ServerCertificateCustomValidationCallback = (_, _, _, _) => true   // S8
};
var client = new HttpClient(handler);
await client.PostAsync("https://payments.onyx-gateway.test/charges", ...);  // A15, S17
```

The register's note about **M1** is the one that stays with you: *"the configured gateway host
does not resolve, so every payment ever taken by this system is in this state"* — where "this
state" is a row reading `"Paid"` for money that never moved. Combined with **S17** (the API key
bound from configuration and never injected), this system could not have taken a single real
payment, and nothing in it would have noticed.

### The one insight the whole module is built on

**Onyx's gateway result was `class PaymentGatewayResult { bool Success; string GatewayReference; }`,
and that single boolean is the root cause of two Critical findings.**

A two-state result cannot express *"we do not know"*. So the code had to pick one — and it picked
both, in different places. The controller read a lost response as success (**M1**). The retry loop
read the same lost response as failure and charged again (**M5**). The same ambiguity, resolved
in opposite directions ten lines apart.

So `AttemptOutcome` has four cases, and the caller must handle each:

| outcome | what it means | retried? | status |
|---|---|---|---|
| `Succeeded` | the provider answered yes, with a reference | — | `Captured` |
| `Declined` | the provider answered no | **never** | `Declined` |
| `TransportFailure` | the provider never saw the request | yes, same key | `Failed` |
| `Indeterminate` | we sent it and lost the reply | **no** | `Failed` + flagged |

`Indeterminate` is the one that earns its keep. A timeout against a payment provider is not a
failure, it is an **unanswered question**, and there are only three things you can do with one:

1. **Assume it failed and charge again.** How a customer gets billed twice. Onyx's choice.
2. **Assume it succeeded.** A payment row reading "Paid" for money that never moved. Also Onyx's
   choice, in a different method.
3. **Go and ask.** Which requires having sent something the provider can recognise the request by.

Option 3 is the only correct one, and it is why `IdempotencyKey` is generated once, committed
before the first attempt, and sent unchanged on every one. `payments.reconciliation` then asks
`"what happened to the request carrying this key?"` and takes the answer from the system of
record — which for *"did this card get charged?"* is the provider, and never us.

**Everything in this module exists to make option 3 possible.** `IdempotencyKey`,
`AttemptOutcome.Indeterminate`, `IPaymentGateway.EnquireAsync` and `Payment.IndeterminateSinceUtc`
have no other purpose.

### M1 and M4 are one type, not two rules

A settled status requires a `ProviderReference`. `ProviderReference` has one factory,
`FromProvider(string?)`, which rejects null and blank. There is no `New()`, no default, no
implicit conversion, and nothing anywhere near it that could supply a fallback.

So a caller holding no provider answer has **nothing to pass** to `Payment.Capture`. Onyx's
`result?.GatewayReference ?? confirmationCode` is not a line somebody would be talked out of
writing here — it is a line that does not compile.

That matters more than the rule it replaces, because the rule was known. The register's
obligation line already says *"the provider reference is nullable and set only from a provider
response"*. Onyx's author would have agreed with it. The difference is whether the agreement is
load-bearing.

**Three identifiers for three audiences**, and Onyx collapsed two of them:

| | whose | for | nullable |
|---|---|---|---|
| `PaymentReference` | ours | the customer and support | no |
| `ProviderReference` | theirs | reconciliation and refunds | **yes** |
| `IdempotencyKey` | ours, sent to them | retry safety and enquiry | no |

Storing a local confirmation code in the provider's field cost all three of its uses at once:
reconciliation became guesswork, refunds were impossible because there was nothing to refund
against, and chargebacks were undefendable.

### A12: the transaction and the HTTP call

Onyx held a database transaction across up to three gateway calls at the default `HttpClient`
timeout of 100 seconds each. On its single-writer SQLite database that blocked **every write in
the entire system** for up to five minutes per payment. One slow provider was a complete outage.

The fix is three steps on two transactions, and the ordering is the design:

```
TX A   InitiatePaymentCommand    the intent, the amount, the idempotency key.  No provider.
 —     SubmitPaymentCommand      the provider call.  NO TRANSACTION AT ALL.
TX B   RecordPaymentOutcome      the attempt row and the status change.  No provider.
```

**`SubmitPaymentCommand` deliberately carries no `IPaymentsRequest` marker.** Every marked
command in this system gets a transaction from its module's pipeline behaviour, so the way to not
have one is to not be marked — and `ModuleWiringTests` asserts that every `ITransactionalRequest`
carries exactly one module marker, which makes the absence a visible decision rather than an
oversight that silently loses atomicity.

Step 3 is a separate dispatched command rather than inline code for a reason worth stating: if
recording the outcome fails, `ICommandDispatcher` retries it in a fresh scope **without
re-calling the provider**. The expensive, non-idempotent half and the cheap, retryable half are on
opposite sides of the boundary.

### M3: a conditional uniqueness constraint, as a row

"At most one payment per order" is wrong. A declined card must be followed by a second attempt
with a different one, so an order legitimately accumulates several payments. What must never exist
is **two payments both able to take money** — a predicate over the *status*, not over the order.

A filtered unique index (`WHERE Status IN ('Pending','Authorized','Captured')`) expresses it
exactly and is provider-specific SQL inside a module whose entire seam is
`AddPaymentsModule(Action<DbContextOptionsBuilder>)`.

So the predicate becomes a row's existence. `PaymentClaim`'s **primary key is the order id**:
initiating inserts it and the key rejects a second; reaching a dead end deletes it, which is what
lets the customer try another card; a captured payment keeps its claim forever.

**Third time this project has made that move**, after `Customer.DefaultAddressId` replaced a
filtered index on `IsDefault` and `OrderPayment` replaced a nullable unique column in Phase 4.
Stable enough to state as a rule: **when a conditional uniqueness constraint is needed, make the
condition the existence of a row.**

### A17: every interaction is a row

> "Nothing records that three attempts were made, what each returned, or why they failed. When a
> customer disputes a charge there is no history to consult."

The only trace of a payment failure in Onyx was a `Console.WriteLine` inside an otherwise empty
catch block, which no logging pipeline captured.

`PaymentAttempt` is written for **every** provider interaction, whatever the outcome — including
one the state machine then refuses, because a provider telling us something impossible is worth
being able to see. It carries the request id, the idempotency key, the attempt number, the
provider's reference and code, the latency and our correlation id. Nothing is ever updated: a
second attempt is a second row, which is what lets the history answer *"what changed between
asks?"*.

It is the payment equivalent of `StockMovement` and `PointsLedgerEntry` — the log beside the
state — and `payments.integrity-check` asserts the pairing, plus two invariants that should be
structurally unreachable: a settled payment with no provider reference (**M4**), and a payment
with submission attempts and no attempt rows (**A17**).

### The job that corrects, and why that is not D5

This project is strict about jobs that fix things. `sales.integrity-check`,
`catalog.stock-integrity-check` and `loyalty.points-integrity-check` all **report and never
correct**, because a job that quietly repaired a discrepancy would erase the only evidence of the
defect that caused it — Onyx **D5**, where a replenishment job healed every oversell before anyone
could see one.

`payments.reconciliation` corrects. The distinction is not a compromise:

- **D5 computed a figure it had no authority over** and imposed it. The job decided what the stock
  level should be.
- **Reconciliation retrieves the answer from the system of record.** For *"did this card get
  charged?"*, the provider is the authority and we are not. The job has no opinion at all — it
  asks, and writes down what it is told.

A job that resolves an unknown by *asking the owner* is the opposite of one that resolves it by
*guessing*. And where the provider cannot say either, the payment stays visibly unknown rather
than being quietly settled in some direction.

### S8: the three tokens that must not come back

```csharp
ServerCertificateCustomValidationCallback = (_, _, _, _) => true
```

On the payment gateway client. That callback is the one control preventing an attacker
impersonating the provider; with it returning `true`, card tokens and charge requests can be read
and altered by anyone positioned on the wire.

**It is banned by a source-convention test**, not by a comment, and the reasoning for that is
worth being explicit about: it is three tokens, it looks like configuration, it makes a stubborn
sandbox work immediately, and it is the first result for every search about TLS errors in
development. *It will be suggested again.* The only durable defence is a build failure — and
because the scan discovers every project under `src/` at run time, it already covers modules that
do not exist yet.

There is a second, structural layer: `BoomboCard.Payments` references **no HTTP package at all**.
`IPaymentGateway` is declared there and implemented in the host, exactly as `IAccessTokenIssuer`
is, because the base address, the API key, the retry policy and the TLS configuration are all
deployment concerns. A module with no handler type in scope cannot write that line.

The adapter also reports a TLS handshake failure as a `TransportFailure` — which is the correct
behaviour and the precise inverse of S8. Onyx disabled validation so that this case would silently
*succeed* instead.

### The chain this phase completes

```
capture  ->  PaymentCapturedIntegrationEvent  ->  Sales marks the order paid
         ->  OrderPaidIntegrationEvent        ->  Loyalty credits the points
```

Three modules, none referencing another's internals, each step guaranteed by an outbox row
committed in the same transaction as the state change that produced it. Onyx did all three in one
controller action with four separate commits and no compensation (**D3**), and set the order to
`Paid` without ever inspecting the gateway's answer (**M1**).

`MarkOrderPaidCommand` documented itself in Phase 4 as a stand-in: *"When Payments exists, the
shape becomes Sales subscribing to its `PaymentCapturedIntegrationEvent` through the inbox and
calling this."* That is exactly the handler, and the command — with its idempotency on the
provider reference and its state machine — needed no change. It still has no HTTP route, which is
**M1** and **M4** enforced by absence: the only caller is that handler, and the only thing that
makes it run is a provider saying the money moved.

A decline raises the event that releases the order's holds, and that is the first thing in the
system that can mean `RestoreReason.PaymentFailed` — a value that has sat in `Catalog.Contracts`
since Phase 0 with nothing able to produce it.

### The most important guard in the consumer code

```csharp
if (!notification.AttemptsExhausted || notification.OutcomeIsUnknown) return;
```

`OnPaymentFailedCancelOrder` cancels the order only when the attempt budget is spent **and** the
outcome is known.

Cancelling on an unknown outcome would release stock for an order that may in fact have been
paid — an oversell caused by cleaning up, which is worse than one caused by a race because it
looks deliberate and the customer has a receipt. They would have been charged for goods then sold
to somebody else.

Onyx's handling of the same situation was to treat every unanswered request as a failure and
charge again. The symmetric mistake on this side is to treat it as a failure and give the goods
away. Both come from the same missing distinction.

### The bug this phase found in its own code

The smoke run submitted a payment, the whole chain fired correctly, and the HTTP response said
`status: "Pending"` for a payment that was `Captured`. The orchestrating handler re-read the
aggregate through its own `DbContext` after dispatching the recording command into a new scope —
and EF's identity map handed back the instance it had been tracking since step 1.

The data was right; only the answer was wrong. That is the mirror image of the failure this
project has cited since Phase 0 — dome-gym returning `200` with a created resource and then
rolling it back — and the fix was to delete the second read rather than defeat the cache: the
command that changes the state now returns it. Full write-up in
[BUGS-AND-LESSONS.md](BUGS-AND-LESSONS.md) section 14.

It is also the clearest illustration of what building this phase without integration tests
leaves uncovered. Nothing is wrong with any unit; the bug lives entirely in the interaction
between two DI scopes and EF's identity map, so it needs a real container to exist at all.

**Say out loud:** *"The root cause of two Critical findings in the legacy payment path is a
`bool Success` on the gateway result — a two-state answer cannot express 'we do not know', so the
code guessed, and guessed differently in two places ten lines apart. So the outcome type has four
cases, a stable idempotency key makes the unknown one resolvable by asking the provider, and a
settled status is unreachable without a provider reference that can only be built from a provider
response. The transaction never crosses the HTTP call, and the certificate callback is banned by a
test rather than a comment."*

---

## 26. Notifications: the finding with two sentences

### What is being replaced

```csharp
_ = Task.Run(() => SendConfirmationEmail(customer.Email, order.Id));    // OrdersController
_ = Task.Run(() => NotifyPaymentConfirmed(order.Id, confirmationCode)); // PaymentsController
```

Both target bodies were `throw new NotImplementedException(...)`. Discarded with `_ =`, so the
exception was never observed, never logged, never surfaced.

**Onyx A8 is two sentences, and almost everyone reading it stops after the first:**

> "No customer has ever received any communication from this system, **and nothing records that**.
> There is no retry, no dead-letter, no delivery record."

The missing implementation is the first sentence. It is the obvious defect and the cheap one — a
day's work. The second sentence is why it survived long enough to be written down in an audit: a
system with a notifications table showing ten thousand rows and zero deliveries is a system
somebody fixes in an afternoon, because somebody looks.

So this module is shaped around the second sentence. Everything in it — the status machine, the
attempt log, the dead-letter event, the health endpoint, the integrity check — exists so that
"we have sent nothing, ever" is a five-second discovery rather than an audit finding.

### The capability has to be reachable, not merely implemented

The roadmap asks for "a logging sender in development, **so the capability is reachable and
tested** rather than 'wired up separately per environment'". That phrasing is pointing at
**A32**, which is worth quoting because it is the subtler failure:

> `CsvImportExportService.ImportProductsAsync` is implemented and tested, but **no endpoint,
> command or job calls it**. The only caller is the test project. `PR_DESCRIPTION.md` lists "CSV
> export/import for bulk catalog management" as delivered; `README.md` advertises "CSV
> import/export for catalog data". The import half does not exist as a capability.

A notification path that only works once somebody has a provider account is the same shape: code
that exists, tests that pass, a README that is true in letter, and nothing that has ever run end
to end. `LoggingNotificationSender` means the whole chain — event, consumer, enqueue, claim, send,
delivery record — runs on a fresh clone with no setup, which is the only way a chain gets
routinely exercised rather than inspected.

It is refused outside Development by the options validator, and the process fails to start if
configured there. A sender that records every message as delivered and transmits nothing is
**A8** reached deliberately. And because the delivery log records *which sender handled each
attempt*, the mistake would be visible in the data even if the validator were removed — which is
why that column exists.

### Rendered at enqueue, not at send

The decision with the most consequences, and the one a reviewer should push on.

The facts arrive on an integration event. The send might happen three seconds later, or — after
four failures and backoff — eight hours later. Rendering late would mean re-reading the order, the
payment and the points balance at send time, so a retried "your order shipped" email would
describe the order **as it is now** rather than as it was when it shipped.

Freezing the text at enqueue makes a retry byte-identical to the first attempt, which is the only
version of a retry a recipient can make sense of. It is the same reasoning as `Order.PlaceFrom`
copying the quote verbatim instead of recomputing it: *when the record and the thing it describes
can drift, copy at the moment they agreed.*

The cost is a stored body, and for two notification kinds that body is a credential — see below.

### Always post-commit, stated precisely

**A9** is that `NotifyPaymentConfirmed` was dispatched *before* the payment row was added to the
context, before `SaveChanges`, and before `Commit` — so a rolled-back transaction left a customer
holding a confirmation for a transaction that never happened.

"Notifications are always post-commit" is the usual phrasing and it is slightly wrong, so it is
worth separating:

- **The decision to send is in-transaction.** The notification row is written inside the
  transaction that consumed the integration event, alongside the inbox claim. A notification
  therefore exists if and only if the event that caused it was consumed — exactly as durable as
  the fact it describes.
- **The send is post-commit.** A separate job, outside every transaction, polling for what is due.

And the integration event itself was published by the outbox only after the state change
committed, so there are two independent reasons the ordering cannot be got wrong by accident.

The templates carry the same fix in words. The order confirmation says payment is *still being
processed*; "payment received" is a separate message, sent when a provider says the money moved.
Getting the ordering right and the wording wrong would still tell a customer they had paid.

### The outbox shape, and where it stops being the outbox

The roadmap called this "the outbox shape, because the problem is the outbox problem", which is
right about the mechanics. Claim, send, record, back off, dead-letter — the same five steps
`OutboxDispatcher` performs.

What differs is what is being moved. The outbox carries this system's own events between its own
modules: rows are transient, the payload is opaque, and a redelivery hits an inbox guard. A
notification is a business fact with a recipient, a legible body, an audit trail, a retention rule
and an administrative read. So it borrows the shape and is its own aggregate, and three
differences follow:

| | outbox | notification |
|---|---|---|
| a definitive refusal | no such thing — an internal consumer cannot say "your address is wrong" | `Rejected`, terminal, **consumes no attempt** |
| duplicate protection | the inbox guard, downstream | the **recipient**, who is not amused |
| a dead letter | an operational row | a customer was not told something |

The second is why the claim is committed *before* the send rather than after. The third is why a
dead letter is published as an integration event: for a password reset it means a customer is
locked out and does not know why.

### Three outcomes, not four — and the asymmetry is the point

Payments has `AttemptOutcome.Indeterminate`, an idempotency key, an enquiry endpoint and a
reconciliation job, all to resolve a request that was sent and never answered. **None of that
machinery is here**, and the reason is worth stating rather than leaving to look like an oversight:

- A charge request that times out and is retried bills a customer twice. Money taken that they
  did not agree to.
- An email send that times out and is retried arrives twice. Mildly annoying.

So Payments spends real design effort making the unknown case resolvable, and Notifications
accepts at-least-once delivery. **Build the guarantee that the cost of being wrong justifies** —
and copying the previous phase's shape wholesale would have been the wrong instinct. Most email
providers do offer a request idempotency key; it is worth adding the day somebody complains about
duplicates, and not before.

The same asymmetry runs the other way too. When recording an outcome fails after a successful
send, the stale-claim sweep requeues the notification and it is sent again — a duplicate, chosen
deliberately, because the alternative is losing it.

### The idempotency key is (Kind, Reference), and choosing the reference is the work

One notification per `(Kind, Reference)`, unique at the database. The inbox claim already
deduplicates a redelivered integration event; this index covers what the inbox cannot — two
*different* events that both want to say the same thing, a replayed outbox after a restore, and a
consumer registered twice by a wiring mistake.

**Uniqueness on the reference alone would be wrong**, and that is the first thing to notice: one
order legitimately produces a confirmation, a shipment notice, a delivery notice and possibly a
cancellation, all keyed on the same order id. Keying on the reference would send the first and
silently swallow the rest — a worse failure than a duplicate, because nobody would ever notice.

Then each consumer has to choose its reference, and the choice is not uniform:

| notification | reference | why |
|---|---|---|
| order placed, delivered, cancelled | the order id | one per order, ever |
| payment received, declined | the payment id | one per payment attempt |
| **verification, reset** | **the token** | asking twice is normal — an expired link, a mistyped address — and each request is a different link. Keying on the customer would send the first and swallow every later one |
| **shipment** | the event id | partial shipment means one order ships more than once, and each parcel is its own message |
| **password changed** | the event id | a customer who changes their password three times should be told three times. Keying on the customer would tell them once and then go quiet, which is exactly wrong for a security notice |
| refund | the event id | partial refunds recur, and each moves real money |

Four of those six would be bugs if the obvious reference had been used. The pattern: **key on the
thing that is unique per message, not per subject.**

### The body is sometimes a credential

A verification or reset body contains the plaintext token, because the email is the only thing
that can deliver one — the database stores a hash, so after the message is rendered nobody, this
system included, can recover it from anywhere else.

That makes the notifications table a credential store for as long as the row holds the text, which
is a problem the design has to answer rather than note:

- `RenderedNotification.ContainsSecret` is set by the template and stored on the row, so machinery
  written before a new secret-bearing kind exists still handles it correctly.
- **The logging sender redacts it.** The thing that makes that sender useful — putting messages
  where a developer can read them — is exactly what would put live reset links into an aggregated,
  shipped, long-retained log file.
- **No query projects it.** Not the detail endpoint, not the summary contract, not the search. The
  endpoint returns `bodyRetained` instead, which tells an operator whether the retention sweep has
  run and is useless to anyone who wants the token. Exposing it would be **S11** with a different
  column — Onyx serialised its `Customer` entity straight to the wire from three endpoints, so
  every response carried `PasswordHash`.
- **It is discarded an hour after delivery**, by `notifications.maintenance`. The subject stays,
  so the audit trail still says what was sent; a row with neither records that something happened
  and nothing about what.

The token's own expiry is the primary control and this is defence in depth. Worth it because a
notifications table is exactly the thing that gets copied into a reporting warehouse and read by
people who have no business holding credentials — and Onyx **S14** stored its reset tokens in
plaintext with no expiry and no single-use marker at all.

### Which failures a customer hears about

A notification module can be too talkative, and the subscription list is where that is decided.

It subscribes to `PaymentDeclined` and **not** to `PaymentFailed`. A decline is the provider's
considered answer and the customer must act on it. A failure may be a transport problem on our
side, possibly followed by a successful fourth attempt — emailing about each one is telling the
customer about our infrastructure. And a failure whose outcome is *unknown* may mean the charge
succeeded, in which case "your payment failed" is simply false.

The decline message is also the only one with an instruction in it, which is why the provider's
code travels: `insufficient_funds` means try another card, `expired_card` means update the
details, `do_not_honor` means call your bank. Onyx had no declined path anywhere in the system, so
it had nothing to tell anybody.

### A notification failure never fails the business operation

Every consumer logs an enqueue failure and acknowledges the event anyway. An order that cannot be
emailed about is still an order, and refusing to acknowledge would park the message in the outbox
forever and block every message behind it — which is RiverBooks' poller, where one bad message
stops the queue.

The same rule inside the dispatcher: per-notification error handling, because the batch is ordered
oldest-first and one permanently broken message would otherwise block everything behind it.

### The eleven consumers are nine lines each

All the thinking — the idempotency check, recipient resolution, rendering, suppression when there
is nowhere to send — lives in `EnqueueNotificationCommand`. A consumer names the kind, names the
reference, and fills in the facts.

Adding the twelfth is a nine-line file, which is the test of whether the seam was drawn in the
right place. And `GetCustomerSummaryQuery` has said since Phase 1 that it is *"used by Sales to
confirm a checkout's customer is real and may transact, and by Notifications to address a
message"* — the second half arrived without that contract changing.

### What this phase added elsewhere

`OrderShippedEvent` and `OrderDeliveredEvent` have existed as **domain** events since Phase 4,
raised by a guarded state machine with per-item quantities — and nothing published them, because
nothing outside Sales had asked. Onyx **A31** is that its order lifecycle "stops at `Paid`" and
nothing ever set `Shipped` at all; BoomboCard could set it and could not tell anybody.

So Sales gained two integration events and two translators, and `OrderShippedEvent` gained a
`UnitsShipped` count — because "order BC-00000123 has shipped" is a weaker message than "3 items
have shipped", and a partial shipment is confusing without it.

### Nothing in this phase went wrong

`BUGS-AND-LESSONS.md` gets no entry, and that is worth recording rather than passing over. The
two defects it would most plausibly have had were both avoided by having met them already:

- **The stale-read bug (§14).** `DispatchNotificationCommand` has exactly the shape that produced
  it — read, call a provider in a new scope, report the result — and it returns the state from the
  command that changed it rather than re-reading through its own context. Written that way from
  the start because Phase 5 had just paid for the lesson.
- **A queued row the dispatcher can never see.** `NextAttemptAtUtc` is set at creation, because
  Phase 4's verification script hand-built an outbox row without one and spent a cycle wondering
  why nothing was delivered.

The only thing that did bite was in the test suite, twice, and both times it was the same mistake:
asserting on async work too early, and asserting a global ordering against a per-class shared
database. The second is the Phase 3 bug (§6) in a new costume.

**Say out loud:** *"The legacy finding here has two sentences and the second one is the real
defect: not that no email was ever sent, but that nothing recorded it — which is why nobody
noticed for the life of the system. So the module is a persisted aggregate with a status machine,
a row per delivery attempt, a published dead-letter and a health endpoint, and the development
sender logs instead of sending so the whole chain runs on a fresh clone. It deliberately does
**not** copy Payments' machinery for an unanswered send: a duplicate charge is theft and a
duplicate email is an apology, so the guarantee should cost what being wrong costs."*

---

## 27. Trade-offs I would defend, and what I would change

### Decisions I would defend

**The drain loop can hold a transaction open longer than a single pass.** Accepted, because
handlers are restricted to in-memory mutation and database writes — no I/O — so the extra time is
small and bounded, and the depth cap stops a pathological cascade.

**Hardcoded tax.** Two countries, one definition, and an explicit note on when it graduates to a
table. Building effective-dated tax rates with no requirement for them would be speculative.

**`uint Version` bumped by an interceptor instead of `rowversion`/`xmin`.** The provider features
are genuinely better — zero maintenance. I chose portability so the module carries no provider
dependency and the host picks the database.

**No cache.** Measurement first. The legacy cache is the single best argument in the codebase for
not adding one speculatively.

### Things I would change or add next

**A `NetArchTest` suite.** The layering rules are currently enforced by project references and
discipline. They should be assertions that fail in CI — the legacy system's architecture document
was contradicted by its own code in fifteen places.

**A real scheduler, and leader election.** `PeriodicTimer` gives a fixed cadence, not cron, and
every replica ticks its own timer (§16). The jobs are safe under that because they claim work
atomically or are read-only, but three instances still do the same read-only `GROUP BY` every
fifteen minutes. Quartz.NET or Hangfire with a persistent store, or a distributed lock with leader
election, is the fix — and the same mechanism would allow genuine "02:00 UTC daily" schedules.

**Rung 1 of the export notification ladder** (§17): `Retry-After` on the 202 and on every
`Generating` response, and `ETag`/`If-None-Match` on the status endpoint keyed on the aggregate's
existing `Version` token, so an unchanged status is a 304 with no body. An afternoon's work that
removes most of the polling cost. SSE and webhooks after that, and only if measurement says so.

**Alerting on the integrity check.** `StockIntegrityCheckJob` writes a non-zero `ItemsFailed` to
`JobRuns` on any pass that finds a discrepancy, which makes it queryable — but nothing yet turns
that query into a page. A discrepancy found there is a data-corruption defect and should wake
someone up.

**A concurrency test.** I have unit tests proving the invariants and integration tests proving
the indexes work, but not yet one that runs two concurrent deductions and asserts exactly one
fails. That is the test that proves the version token actually works end to end.

**`OwnsOne` vs `HasConversion` consistency.** Settled while building, the hard way. `Sku` started
as a converter and had to become an owned type, because a converter cannot be ordered or filtered
server-side — it broke the export and the paged search. The rule: **converters for single values
you only read back; owned types for anything you query, sort or index.** See
[BUGS-AND-LESSONS.md](BUGS-AND-LESSONS.md) section 3.
