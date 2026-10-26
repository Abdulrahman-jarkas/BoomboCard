# Bugs I hit building this

Real failures from running the API, each found and then pinned by a test. Worth knowing because
"tell me about a bug you debugged" is a standard interview question, and because three of these are
EF Core traps rather than logic mistakes, one is a trap created by my own design, and three
reproduced a legacy failure mode in my own rebuild of it — including one I had written a whole
document about — one had been sitting in a shipped schema since the first migration, and one was
a design document describing code that was never written.

Companion to [CONCEPTS.md](CONCEPTS.md).

---

## 1. A concurrency exception on a completely uncontended write

**Symptom.** Every `PUT /products/{id}/prices/{country}` returned 500:

```
DbUpdateConcurrencyException: The database operation was expected to affect 1 row(s),
but actually affected 0 row(s)
```

No concurrency involved — one request, idle database.

**First wrong theory.** I assumed `VersioningInterceptor` was clobbering `OriginalValue`, so I
pinned it explicitly. Still failed. Guessing twice is the signal to stop guessing.

**Actual cause.** Logging the SQL with `EnableSensitiveDataLogging` showed it immediately:

```
ENTRY ProductPrice state=Modified                 <- a BRAND NEW price
UPDATE "ProductPrices" SET ... WHERE "Id" = @p6   <- UPDATE, not INSERT
```

`ProductPrice.Id` is assigned in the domain with `Guid.NewGuid()`. When EF discovers an
**untracked** entity inside a **tracked** parent's collection, it infers the state from the key
value: a non-default key means "this row already exists", so the new child was marked `Modified`.
EF emitted an UPDATE against a row that had never been inserted, it affected zero rows, and EF
surfaced that as a concurrency conflict.

**Fix.** Declare the keys as client-generated, across the whole model so it cannot be forgotten:

```csharp
// ModelBuilderExtensions.UseClientGeneratedKeys()
foreach (var entityType in modelBuilder.Model.GetEntityTypes())
{
    var primaryKey = entityType.FindPrimaryKey();

    foreach (var property in primaryKey?.Properties.Where(p => p.ClrType == typeof(Guid)) ?? [])
    {
        property.ValueGenerated = ValueGenerated.Never;
    }
}
```

**Why it hid.** It only bites when the parent is *already tracked*. Saving a new aggregate via
`Add()` marks the whole graph `Added` explicitly — so creating a product with prices worked
perfectly, and adding a price to an existing product failed every single time.

**Lessons.**

- If the domain assigns its own ids, the model must say so.
- `DbUpdateConcurrencyException` does not always mean a concurrency conflict. It means "an UPDATE
  or DELETE affected a different number of rows than EF expected" — which includes an UPDATE
  against a row that does not exist.

---

## 2. The version token silently never advanced

**Symptom.** Two tests failed together:

- `SequentialWrites_DoNotConflict_AndTheVersionAdvancesEachTime` expected `Version == 1` after a
  deduction and got `0`.
- `TwoWritersFromTheSameVersion_SecondOneLoses` expected a conflict and got **none** — both
  writers succeeded.

**Cause.** The interceptor only bumped entries whose state was `Modified`. But `Product.Stock` and
`Product.Status` are **owned types**: EF marks the *owned* entry `Modified` and leaves the
`Product` entry `Unchanged`. A stock deduction therefore never advanced the version.

**Why this is the worst possible failure mode.** A concurrency token that never advances detects
nothing — `WHERE Version = @original` always matches. That is Onyx **D1** rebuilt exactly: both
writers read 3, both passed the check, both wrote 1, four units sold from three, no error.

**Fix.** Treat a change anywhere inside the aggregate boundary as a change to the root:

```csharp
private static bool HasChangesInsideAggregate(EntityEntry rootEntry, DbContext context)
{
    if (rootEntry.State is EntityState.Modified) return true;

    // Owned value objects: Stock, Status, CostPrice.
    foreach (var reference in rootEntry.References)
    {
        if (reference.TargetEntry is { } target && target.Metadata.IsOwned()
            && target.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            return true;
    }

    // Child entities inside the aggregate: Prices.
    foreach (var collection in rootEntry.Collections)
    {
        foreach (var child in collection.CurrentValue ?? Enumerable.Empty<object>())
        {
            if (context.Entry(child).State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                return true;
        }
    }

    return false;
}
```

That is also the correct aggregate semantic: the aggregate is one consistency boundary, so two
concurrent price changes to the same product *should* conflict.

**Lesson.** A safety mechanism that silently does nothing is worse than no mechanism, because it
buys false confidence. The only reason I know it works is the test asserting the second writer
loses — and that test is the one I would write first next time.

---

## 3. A value object mapped with a converter cannot be queried

**Symptom.** The export job recorded a failure on its aggregate (which is the design working — the
status went to `Failed` with the reason attached rather than hanging in `Generating`):

```
The LINQ expression 'DbSet<Product>().OrderBy(p => p.Sku.Value)' could not be translated.
```

The paged search had the same latent problem.

**Cause.** `Sku` was mapped with a value converter:

```csharp
builder.Property(product => product.Sku)
    .HasConversion(sku => sku.Value, value => Sku.Create(value).Value);
```

With a converter, `p.Sku` is the column and `p.Sku.Value` is a **member access on a CLR object** —
not translatable to SQL. It survives in a final `Select`, because EF evaluates that on the client,
but it fails outright in `OrderBy`, `Contains`, or a keyset predicate. Which is every query the
catalogue actually needs.

**Fix.** Map it as an **owned type**, so `Sku` becomes a real column:

```csharp
builder.OwnsOne(product => product.Sku, sku =>
{
    sku.Property(value => value.Value)
        .HasColumnName("Sku")
        .HasMaxLength(Sku.MaxLength)
        .IsRequired();

    sku.HasIndex(value => value.Value).IsUnique().HasDatabaseName("UX_Products_Sku");
});
builder.Navigation(product => product.Sku).IsRequired();
```

Also replaced `CompareTo` with `string.Compare(a, b) > 0` in the keyset predicate, which EF
translates reliably.

**Lesson, and it settles an open question.** My earlier notes called converter-vs-owned-type a
judgement call. It is not:

> **Value converters for single values you only ever read back.
> Owned types for anything you query, sort or index.**

---

## 4. SQLite cannot order or compare `DateTimeOffset`

**Symptom.** The outbox dispatcher and the export generator failed on every single pass:

```
SQLite does not support expressions of type 'DateTimeOffset' in ORDER BY clauses.
```

**Cause.** SQLite has no date type. It stores `DateTimeOffset` as TEXT and then refuses to sort or
range-compare it. That broke the three queries this system leans on hardest: the outbox's "pending
and due" predicate, the export generator's oldest-first ordering, and the effective-dated price
lookup.

**Fix.** A `DateTimeOffset` → UTC-ticks `long` converter, applied **only when the provider is
SQLite**:

```csharp
if (Database.ProviderName is SqliteProviderName)
{
    configurationBuilder.Properties<DateTimeOffset>()
        .HaveConversion<DateTimeOffsetToUtcTicksConverter>();

    configurationBuilder.Properties<DateTimeOffset?>()
        .HaveConversion<NullableDateTimeOffsetToUtcTicksConverter>();
}
```

Integers sort exactly and index well. The offset is discarded deliberately: everything here is UTC
by rule (`TimeProvider.GetUtcNow()` everywhere), so there is no offset worth preserving.

**Lesson, and the honest trade-off.** "Provider-agnostic" means the module does not *reference* a
provider. It does not mean providers are interchangeable — real capability gaps exist, and they
belong at the persistence edge, resolved by provider name, with the domain untouched.

The alternative would have been to change `DateTimeOffset` to `DateTime` to suit one database:
letting a storage limitation dictate the domain model. Worth refusing.

---

## 6. A background job killed the host — the exact bug I had documented

**Symptom.** After switching the provider to SQL Server, the application started and then
immediately stopped:

```
crit: The HostOptions.BackgroundServiceExceptionBehavior is configured to StopHost.
      A BackgroundService has thrown an unhandled exception, and the IHost instance is stopping.
System.ArgumentException: The value cannot be an empty string. (Parameter 'path')
   at System.IO.Directory.CreateDirectory(String path)
   at ProductExportGenerator.ExecuteAsync(...) line 49
```

**Cause.** Two mistakes meeting.

I had left an empty placeholder in `appsettings.json`:

```json
"Catalog": { "Export": { "StorageDirectory": "" } }
```

Configuration binding does not treat an empty string as absent, so it overrode the default. Then
`Directory.CreateDirectory("")` threw — and it was called **before the loop**, outside the
per-iteration `try/catch`. Under .NET's default `BackgroundServiceExceptionBehavior.StopHost`,
that took the entire host down.

**Why this one stings.** It is Onyx **A18/A21** reproduced in the rebuild. There, an unhandled
`DivideByZeroException` in the nightly report terminated the whole worker process and took the
unrelated stock job with it. I wrote that up, built `JobRunner` to absorb exceptions per
iteration — and then put setup code where no boundary could reach it.

**Three fixes, because one was not enough:**

1. **A blank configured value falls back to the default** instead of overriding it with nothing.
   An empty placeholder in a config file is a realistic thing to leave behind and must not be able
   to break startup.
2. **The setup moved inside the loop's try.** `CreateDirectory` is idempotent, so doing it per
   pass costs nothing and is self-healing if the directory is removed.
3. **`BackgroundServiceExceptionBehavior.Ignore`** in the host, as a backstop. A faulting job
   stops being retried; the host and every other job survive.

**Lesson.** A per-iteration error boundary only covers the iterations. Anything in `ExecuteAsync`
before the loop — option validation, directory creation, a connectivity check — is outside every
`try` you wrote, and the default host behaviour turns it into a total outage. Set
`BackgroundServiceExceptionBehavior.Ignore` deliberately rather than inheriting `StopHost`.

Also: this surfaced only because I changed the provider and restarted cold. The same latent bug
had been sitting there through every earlier run, because the default directory was being used.

---

## What they have in common

**Four of the five were invisible to a green build and to the first 36 unit tests.**

The unit tests exercised the aggregate directly, so they never touched EF's change tracker or a
SQL translator. Bugs 1, 3 and 4 only appeared when a request actually hit a database, and bug 2 —
the one that would have reintroduced Onyx's worst data defect — only appeared once I wrote a test
that drove two independent `DbContext` instances against the same row.

That is also precisely how Onyx shipped 101 issues with a passing build: it had tests for pricing,
CSV parsing and hashing, and none for orders, payments, concurrency or caching.

**What I would do differently:** run the API against the real provider on day one, and write the
concurrency test before the concurrency mechanism.

---

## 5. A dry run that wasn't dry

**Symptom.** `POST /imports/products?dryRun=true` returned `"committed": false` — and wrote all
three products to the database.

**Cause.** The handler's dry-run path simply skipped `unitOfWork.SaveChangesAsync()`. That is not
enough, because of how the domain event dispatcher works:

```csharp
foreach (var domainEvent in pending)
    await publisher.Publish(domainEvent, cancellationToken);

if (context.ChangeTracker.HasChanges())
    await context.SaveChangesAsync(cancellationToken);   // <- this
```

The dry run had built real aggregates and added them to the change tracker in order to validate
them against the domain rules. `UnitOfWorkBehavior` then called the dispatcher, the dispatcher saw
pending changes, and saved them. Then it committed the transaction.

**Fix.** An explicit `IUnitOfWork.DiscardChanges()`, implemented as `ChangeTracker.Clear()`, called
on the dry-run path. Clearing the tracker also drops the pending domain events, since the
dispatcher finds aggregates through the tracker.

**Lesson, and it is a general one.** The drain loop's `SaveChangesAsync` is what makes domain event
handlers able to mutate aggregates without saving themselves — a feature. But it means **"I did
not call save" is not the same as "nothing will be written."** Anything that deliberately does not
persist has to say so, not merely stay quiet.

Worth noting what caught it: not a test, but actually calling the endpoint and then checking the
side effect rather than trusting the response body. The response said `committed: false` and was
wrong about itself.

---

## 7. The module boundary that was one commit from losing data

Found by reading, not by running — while planning the modules after Catalog, which is the only
reason it was ever cheap to fix.

`AddModulePersistence<TContext>` registered the three per-module services unkeyed:

```csharp
services.AddScoped<IUnitOfWork, EfUnitOfWork<TContext>>();
services.AddScoped<IDomainEventDispatcher, DomainEventDispatcher<TContext>>();
services.AddScoped<IOutboxWriter, OutboxWriter<TContext>>();
```

With one module that is correct. The second call to it — `AddSalesModule`, two minutes of work —
would have replaced all three for **every handler in the process**, because the last registration
for a service type wins. A Catalog handler would then be handed `EfUnitOfWork<SalesDbContext>`,
call `SaveChangesAsync` on a context holding none of its changes, get `0` back, and
`UnitOfWorkBehavior` would commit a transaction over a context with nothing in it.

No exception. No log. No rows. A `201 Created` with a product id in it, and no product.

**Why it is worth dwelling on.** Every other defect in this codebase's history announced itself:
bug 1 threw, bug 6 killed the host, bug 5 lied in a response body I could check. This one would
have produced a system that looked completely healthy and silently discarded writes — the same
shape as Onyx D3 ("multi-write use cases are not transactional") arriving by a route that
`ARCHITECTURE.md` -style prose would never have predicted, in the plumbing written specifically
to prevent D3.

**Fix.** Type the registration on the module's own context, and register nothing against the bare
interface:

```csharp
services.AddScoped<IUnitOfWork<TContext>, EfUnitOfWork<TContext>>();
```

A handler now asks for `IUnitOfWork<CatalogDbContext>`, and asking for a context its module
cannot see does not compile. Because the pipeline behaviour needs a concrete context too, and
MediatR's open-generic registration allows exactly two type parameters, the behaviour became
abstract with one sealed subclass per module, constrained to that module's request marker
(`ICatalogRequest`).

**The second-order problem, which is the interesting half.** That fix moved the failure rather
than removing it. A command that forgets its module marker still compiles, still runs, and now
gets *no* transaction at all — quieter than what it replaced. So the convention had to become an
assertion: `ModuleWiringTests` walks every `ITransactionalRequest` in every module and fails
unless it carries exactly one marker, fails unless each marker has a registered behaviour, and
fails if anything is ever registered against the bare interfaces again. I checked both tests fail
by reintroducing both mutations.

**Lesson.** A fix that relies on remembering something is not finished. And the thing I had
written down — "a missing registration here fails at resolution instead", in that method's own
doc comment — was true of a *missing* registration and silently false of a *duplicate* one. The
comment was accurate and useless, which is the most dangerous kind.

---

## 8. Two `Take`s with no `ORDER BY`, in the jobs that exist to find problems

The host logged this twice at every startup, and I had read past it more than once:

```
warn: Microsoft.EntityFrameworkCore.Query[10102]
      The query uses a row limiting operator ('Skip'/'Take') without an 'OrderBy' operator.
```

`StockIntegrityCheckJob` took the first 100 stock discrepancies and `ExportMaintenanceJob` took
the first 50 abandoned exports, neither with a sort key. SQL Server is free to return *any* 100
matching rows, and will happily return the same ones every pass — so a discrepancy outside that
arbitrary set is never reported, by the job whose only purpose is reporting discrepancies. The
integrity check would have looked like it was working. That is Onyx **A27** (unordered
pagination), in my own code, in the two files least able to afford it.

**Fix.** `OrderBy` on a unique, indexed key in both — SKU and `RequestedAtUtc` — so the order is
total and every row is eventually reached.

**The real fix.** A warning in a startup log is what Onyx did with NU1903 on every single compile
(**S18**), so the class of bug was promoted to an exception for every module's context:

```csharp
options.ConfigureWarnings(w => w.Throw(CoreEventId.RowLimitingOperationWithoutOrderByWarning));
```

It throws during query translation, so it is deterministic and surfaces on first execution rather
than selectively under load. Note that a source-scanning convention test could not have caught
this: the sort key may be applied anywhere in the chain, or inside a repository method. The query
compiler knows; a regex does not.

**Lesson.** I built `SourceConventionTests` because "a compiler accepts it happily" is how Onyx
shipped. The same reasoning applies one level up: *the framework already told me twice*, in
writing, at startup. An unread warning and an absent test are the same thing.

---

## 9. The outbox column that was two fields away from breaking every integration event

Found while reading a generated migration for a **second** module, which is the only reason it
was found at all.

`CustomersDbContext` produced this:

```csharp
Payload = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
```

The outbox payload is serialised JSON. 500 characters.

Checking Catalog's first migration, from weeks earlier: identical. It had been in the shipped
schema the whole time.

**Cause.** Each module's `DbContext` applies a sensible default to every string column:

```csharp
configurationBuilder.Properties<string>().HaveMaxLength(500);
```

Good for the domain's own columns, and quietly wrong for one that holds arbitrary serialised
data. The convention applied to `OutboxMessage.Payload` because nothing in
`OutboxMessageConfiguration` overrode it — the configuration set a length on `Type` and on
`LastError` and said nothing about `Payload`, so silence meant 500.

**Why it had not bitten yet.** `ProductPriceChangedIntegrationEvent` serialises to roughly 400
characters — four GUIDs, two decimals, two timestamps and their property names. It was inside
the limit by about a hundred bytes. Adding one more GUID field, or a product name, would have
crossed it.

**What crossing it would have looked like.** SQL Server does not truncate. It raises
*"String or binary data would be truncated"*, which fails the `INSERT`, which fails the
`SaveChangesAsync`, which rolls back **the entire command that raised the event**. So a price
change would have started returning 500 — with an error about string truncation, naming a table
the developer changing prices has no reason to think about. The outbox exists specifically so a
state change and its announcement cannot come apart; a payload limit means they come apart by
failing the state change.

**Fix.**

```csharp
builder.Property(message => message.Payload).Metadata.SetMaxLength(null);
```

`SetMaxLength(null)` rather than `HasColumnType("nvarchar(max)")`: explicit configuration beats
the convention either way, but leaving the length unset lets each provider choose its own
unbounded text type instead of putting SQL Server syntax into shared plumbing that is supposed
to be provider-agnostic. Catalog got a `WidenOutboxPayload` migration; Customers was never
created wrong.

**Lessons, and there are two.**

A convention is a decision applied to code that has not been written yet, including code where
it is wrong. `Properties<string>().HaveMaxLength(500)` is right for roughly every column in this
system and wrong for exactly the one that matters most, and the mechanism that makes it wrong —
silence meaning "the default applies" — is invisible at the point of the mistake. Shared
infrastructure under a module's conventions has to state its requirements explicitly rather than
inherit them.

And: **reading the generated migration is part of writing the model.** I had reviewed the
`ProductConfiguration` that produced Catalog's schema and not the schema it produced. The bug
was one line of unremarkable-looking generated code, in a file I had treated as an artefact
rather than as output worth checking. There is now a test that stores a 5,000-character payload
and reads it back.

---

## 10. Background jobs that raised domain events into the void

Found while writing the Promotions expiry job, by asking a question about my own code that I
had not asked before: *what actually dispatches the events this job raises?*

Nothing did.

```csharp
// ProductExportGenerator, shipped weeks earlier
var claim = export.BeginGenerating(timeProvider.GetUtcNow());   // raises a domain event
await unitOfWork.SaveChangesAsync(cancellationToken);           // writes rows. drains nothing.
```

Domain events are dispatched by `ModuleUnitOfWorkBehavior`, which runs only for requests going
through the MediatR pipeline. A `BackgroundService` is not one. So a job that mutated an
aggregate wrote its rows, raised its events, and dropped them on the floor when the DI scope
closed — in **four** places across two Catalog jobs.

**Why it had never caused a problem.** None of the export events had a subscriber. `ProductExport`
raises five, and the outbox translators covered `Product`'s events only. So the dispatch did
nothing, correctly, by coincidence.

**What it was waiting to do.** The obvious Phase 6 task is "tell the customer their export is
ready", and the obvious way to write it is an outbox translator for
`ProductExportCompletedEvent`. That translator would have been correct, registered, and
**never invoked** — no exception, no log, no row. Debugging it would have started from "the
notification isn't arriving" and the cause would have been three layers away in a method called
`SaveChangesAsync`.

This is the same failure mode the codebase already criticises RiverBooks for: taking
`IDomainEventDispatcher?` and returning early when it is null, so one missing DI line silently
disables every domain event in the application. I had written that criticism into
`ModulePersistenceExtensions`' own doc comment. Then wrote the same class of bug in a different
shape.

**Fix, in two parts.**

A `SaveAndDispatchAsync` that does for a job what the behaviour does for a command — open a
transaction, save, drain, commit:

```csharp
public async Task<int> SaveAndDispatchAsync(CancellationToken cancellationToken = default)
{
    if (HasActiveTransaction) return await context.SaveChangesAsync(cancellationToken);

    await using var transaction = await BeginTransactionAsync(cancellationToken);
    var written = await context.SaveChangesAsync(cancellationToken);
    await dispatcher.DispatchUntilDrainedAsync(cancellationToken);
    await transaction.CommitAsync(cancellationToken);
    return written;
}
```

And — the part that matters more — a guard that makes choosing wrong **loud**:

```csharp
public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
{
    if (!HasActiveTransaction)
    {
        var pending = context.ChangeTracker.Entries<AggregateRoot>()
            .Where(entry => entry.Entity.DomainEvents.Count > 0) ...

        if (pending.Count > 0) throw new InvalidOperationException(...);
    }

    return await context.SaveChangesAsync(cancellationToken);
}
```

Inside a command the behaviour has opened a transaction and will drain afterwards, so the check
is skipped. Outside one, saving an aggregate with undispatched events now throws with a message
naming the aggregates and pointing at the right method.

Verified live rather than by assertion alone: a campaign with a 25-second window, the sweep
closing it 40 seconds later, and then the `PromotionClosedIntegrationEvent` row sitting in
`promotions.OutboxMessages` with `Status = Published` — which is only possible if
`SaveAndDispatchAsync` drained the event the job raised.

**Two lessons, and the second is the real one.**

Adding a second method that callers must choose between is normally a smell — the usual outcome
is that somebody picks wrong. It is acceptable here *only* because the guard makes the wrong
choice throw. A fork in the road with a sign is a design; a fork with no sign is a trap.

And: **a guarantee that only applies on one code path is not a guarantee, it is a coincidence.**
I had tested the transaction-and-dispatch sequence thoroughly — through commands. The jobs used
the same `IUnitOfWork` interface and got a quietly different contract, and nothing in the type
system or the tests distinguished them. The question that found it was not "is this code
correct?" but "which of the mechanisms I rely on is this call site actually inside?"

---

## 11. A design document describing a column that does not exist

Not a runtime bug. A documentation bug of exactly the kind this project exists to refuse, in the
project's own flagship document.

`CONCEPTS.md` §11, in the EF configuration walkthrough:

```markdown
#### Category index

```csharp
builder.HasIndex(product => product.CategoryId).HasDatabaseName("IX_Products_CategoryId");
```
```

`Product` has no `CategoryId`. `ProductConfiguration` has no such index. The snippet would not
compile. And `Product`'s own class comment opened with "a sellable catalogue item: identity,
**merchandising category**, lifecycle…".

**How it was found.** Not by review, and not by a test — by needing the answer. Promotion scope
had to decide between product ids and categories, so for the first time something *depended* on
whether products have categories. They do not.

**Why no test caught it.** `ArchitectureTests` and `SourceConventionTests` assert things about
code. Nothing reads the prose, and nothing can: a paragraph describing an index that should
exist is indistinguishable, to a test, from a paragraph describing one that does.

**The fix was to correct the document, not to add the column.** Tempting to make the paragraph
true — it is three lines of configuration. But categories are on the capability-gap list in
`ISSUES.md` §7, nothing needs them, and adding a property so a stale paragraph stops being wrong
is precisely the speculative generality **A45** is about. The section now explains what is
actually there and why, and records that it used to claim otherwise.

**Lesson.** This is BoomboCard's third X-number — after `Program.cs` claiming a container test
that did not exist and the README claiming SQLite when the fixture had moved to SQL Server. All
three were documentation that drifted from code. The honest conclusion is not "be more careful";
it is that **`CONCEPTS.md` is 3,000 lines of prose with no mechanism keeping it true**, and every
claim in it that is not backed by a named test is a claim that will eventually be wrong. That is
why [ISSUE-COVERAGE.md](ISSUE-COVERAGE.md) maps findings to *tests* rather than to prose, and why
the third column is the one that matters.

---

## 12. Two owned navigations holding the same `Money` instance

**What happened.** Every Sales persistence test failed at `context.Orders.Add(order)` — before
any SQL was generated — with:

```
The property 'OrderId' belongs to the type 'Order.QualifyingSpend#Money',
but is being used with an instance of type 'Order.DiscountedMerchandise#Money'.
```

**The cause.** `Order.PlaceFrom` copies the quote onto the order, and two of the figures are the
same number:

```csharp
DiscountedMerchandise = session.DiscountedMerchandise,
QualifyingSpend       = session.DiscountedMerchandise,   // the same OBJECT
```

`Money` is a reference type. EF maps those two properties to two separate owned entity types, and
an owned instance belongs to exactly one navigation — EF identifies an owned entity by its
*owner's* key plus the navigation, so the same instance in two slots makes the change tracker
read the second navigation's shadow key off the first one's metadata and throw.

**Why the error message is so unhelpful.** It names two shadow properties the code never mentions
and gives no hint that the problem is aliasing. Nothing in the message says "you assigned one
object twice", so the time it cost was spent looking for a configuration mistake.

**The fix.** A copy at the assignment, plus the invariant stated one layer up so no consumer can
trip over it again:

```csharp
QualifyingSpend = Copy(session.DiscountedMerchandise),
```

`QuoteTotals` now documents that **every field is a distinct instance even where two hold the
same value**, and `QuoteCalculator` copies at the two places where aliasing was possible — the
`QualifyingSpend` projection, and `TaxableBase` on the not-taxable branch. That second one is not
reachable today, because both configured countries tax delivery; it is a latent trap that would
have fired the first time somebody added a country that does not, with the same baffling message
and no obvious connection to the change they had just made.

**Why value-object equality does not save you.** `Money` is a `ValueObject`, so two copies compare
equal and nothing downstream can tell the difference. That is exactly what makes the bug easy to
write: the aliasing is semantically invisible, and only the change tracker's identity model cares.

**Lesson.** Reference-typed value objects and owned types interact badly in one specific way, and
the shape is worth remembering rather than the message: **if two owned navigations can ever hold
the same value, they must not hold the same instance.** The place to enforce it is wherever the
figures are produced, not at each of the N places they are consumed — which is why the guard went
into `QuoteCalculator` and not only into `Order`.

---

## 13. Stock restored that was never deducted

**The most serious bug in the project so far, and the only one found by the end-to-end run rather
than by a test.**

**What happened.** The Phase 4 verification script started a checkout with a promotion code, read
the quote, then abandoned it with `DELETE /api/checkouts/{id}` — a session that had never been
confirmed, so nothing anywhere was holding anything for it. Seconds later the stock assertion
failed: a product that should have had 8 units had 10. Catalog's movement log said why:

```
reference                             reason           delta  after
1e2dcac6...  (the confirmed checkout) Sale               -3      5
d7fcaa65...  (the abandoned one)      SessionExpired     +2      7   <-- from nowhere
1e2dcac6...                           SessionExpired     +3     10
```

The abandoned session's reference has a `SessionExpired +2` and **no `Sale` at all**. Two units of
inventory were created out of a cancelled basket.

**The cause.** `OnCheckoutAbandonedReleaseHolds` — code written the same afternoon — did this:

```csharp
session.Lines.Select(line => new StockLine(line.ProductId, line.Quantity))
```

The session's lines describe **what the basket contained**. What needed releasing is **what TX 1
took off the shelf**. Those coincide for a session that reached `Holding` and differ completely
for one that did not: an `Open` session holds nothing, because in this design stock is deducted at
confirm rather than at quote. So the handler asked Catalog to put back a basket that had never
been taken.

**Why idempotency did not catch it, and could not have.** The unique index on
`(ProductId, Reference, Reason)` is the guarantee the whole saga rests on, and it was working
perfectly. It stops an operation being **repeated**; this was an operation being performed
**wrongly**, once. It was the first `SessionExpired` row for that reference, so the insert was
entirely legal. *Idempotency protects against duplicate work, never against wrong work* — and
after three phases of leaning on it, that distinction had quietly stopped being visible.

**Why no test caught it.** Every Sales test exercises a session that reaches `Holding`, because
that is the path an order takes. The abandoned-before-holding path had no test because it looked
like the trivial case — the one where nothing happens. It was the only case where something
happened that should not have.

**Whose bug it is.** Both modules'. Two fixes, and the second is the one that matters:

1. **Sales stops asking.** `CheckoutSession.HoldsPlacedAtUtc` records when TX 1 ran, and
   `OnCheckoutAbandonedReleaseHolds` returns early when it is null. It is a durable fact about the
   session's history rather than its current state, which is precisely what the handler needs — by
   the time a release runs the status is already terminal and `IsHolding` is long gone. It is set
   *before* the holds are placed, deliberately: a crash midway through TX 1 must leave the session
   saying "holds may exist" rather than "none do", because claiming too much is harmless once
   Catalog clamps, and claiming too little strands stock forever.

2. **Catalog stops complying.** `RestoreStockCommandHandler` now reads `SUM(Delta)` over the
   checkout reasons for that reference and **restores at most what is actually held**, logging a
   warning when asked for more. `RestoreReason.Return` is exempt — a return references an order
   and real goods coming back through the door, so there is no prior deduction to match it
   against.

Fix 1 alone would have closed this instance. Fix 2 is what closes the class, and it belongs in
Catalog for a reason worth stating plainly: **"never invent stock" is Catalog's own invariant, and
an invariant enforced by trusting the caller is not enforced.** Sales was a well-behaved caller
that happened to be wrong. The next one will be too.

**The uncomfortable part.** This is Onyx **D5** — a job that topped stock up to a level it
computed itself, healing every oversell before anyone could see one — arriving in our own code by
a different road, inside a handler whose own documentation cites D5 as the thing it is carefully
not doing. The register entry it reopens is one of the two Critical findings this project was
built around.

Worse, `catalog.stock-integrity-check` could not have found it either, because the counter and the
log **agreed**: both said 10. The movement was written through `Product.Restore`, so state and log
moved together. Agreement between a counter and its log proves they are consistent; it says
nothing about whether either is *right*. The regression tests therefore assert the **net** — a
release with no matching deduction nets positive, which is the arithmetic signature of inventory
from nowhere — rather than the counter-equals-sum form that was already true.

**Lesson, and it is three.**

- A release must be bounded by what was actually taken, not by what the caller believes it took.
  The log already knew. Nothing had asked it.
- "Fire blind and rely on idempotency" is a real and good pattern — it is what makes the expiry
  sweeper simple — but it only works when the blind call is **capped by the receiver**. Blind plus
  trusting is how you invent inventory.
- The end-to-end script is not ceremony. Four phases and 393 tests, all green, and this needed a
  real HTTP request against a real database with a real background job running to show itself. It
  surfaced because the script asserted a figure that *nothing in the request path had any business
  changing* — which is the kind of assertion only an end-to-end test is positioned to make.

---

## 14. A correct write with an answer that described the state before it

**What happened.** The Phase 5 smoke run submitted a payment against the stub gateway. The stub
approved it, the payment was captured, the outbox fired, Sales marked the order paid and Loyalty
credited 80 points — the whole three-module chain worked. And the HTTP response said:

```json
{ "reference": "PAY-YWJ88YUQ6MHPM5WR", "status": "Pending", "message": "Payment accepted." }
```

`Pending`. For a payment that was, at that moment, `Captured`.

The same bug made every refund response wrong in the same direction: a partial refund returned
`status: "Captured", refundedAmount: 0.00` while the database already held the refund row and the
updated total. Six of the seven smoke-run failures were this one defect.

**The cause.** `SubmitPaymentCommandHandler` is a three-step orchestrator — read, call the
provider outside any transaction, then dispatch a marked command to record the outcome. Step 3
goes through `ICommandDispatcher`, which **opens a fresh DI scope with a fresh `DbContext`** so a
concurrency conflict can be retried against a reloaded aggregate. That part is correct and
deliberate.

Then the handler did this:

```csharp
var recorded = await dispatcher.SendAsync(new RecordPaymentOutcomeCommand(...));

var settled = await payments.GetAsync(payment.Id, cancellationToken);   // <-- here
return Describe(settled ?? payment, MessageFor(result));
```

`payments` is the repository from the **original** scope, and its `DbContext` has had the payment
tracked since step 1. EF's identity map returns the tracked instance for a query that resolves to
an already-tracked key — it does not overwrite a tracked entity with fresh database values. The
write happened in a different context, in a different scope, and this one never heard about it.

So `GetAsync` returned the object as it was before the provider was ever called.

**Why it is worse than it looks.** The data was right; only the answer was wrong. That is the
failure mode this project is explicitly built against — the README has said since Phase 0 that
**"responses describe what is committed"**, citing dome-gym returning `200` with a created
resource and then silently rolling it back. This was the mirror image: commit the right thing and
then describe something else. A customer sees "Payment accepted" next to `Pending`; a client
polling on status waits forever for a transition that already happened; a support screen shows a
payment stuck in a state it left seconds ago.

**Why no unit test caught it, and could not have.** There is nothing wrong with any unit here.
The aggregate transitions correctly, the command records correctly, the response record is shaped
correctly. The bug lives entirely in the interaction between **two DI scopes and EF's identity
map**, which means it needs a real container and a real `DbContext` to exist at all. Phase 5 was
built without integration tests by request, and this is precisely the class of defect that
decision leaves uncovered — which is worth stating plainly rather than filing under bad luck.

It was found by a smoke run asserting on the HTTP response rather than on the database. Both the
Phase 4 bug (§13) and this one were found that way, by two different kinds of assertion: that one
checked a figure nothing should have changed, this one checked that the answer matched the state.

**The fix is to delete the second read, not to defeat the cache.**

```csharp
var recorded = await dispatcher.SendAsync(new RecordPaymentOutcomeCommand(...));

return new SubmittedPaymentResponse(..., recorded.Value.Status, recorded.Value.DeclineCode, ...);
```

`RecordPaymentOutcomeCommand` now returns `PaymentOutcomeRecorded`, and `RecordRefundCommand`
returns the `RefundResponse`. The command that changed the state reports the state, read off the
aggregate it just mutated, inside the transaction that committed it.

Three other fixes were available and all are worse:

- **`AsNoTracking()` on the re-read.** Works, and leaves a second query doing work the first
  handler had already done — plus a window in which a concurrent write could make the two
  disagree again.
- **`ChangeTracker.Clear()` before re-reading.** Works, and quietly discards any other pending
  state on that context. A fix that depends on knowing what else is tracked is not a fix.
- **`entry.ReloadAsync()`.** Works, and is a fix that only a reader who already knows about this
  trap will understand. It also leaves the shape — "mutate over there, read back over here" —
  which is the actual problem.

Returning the state removes the second read entirely, so there is nothing left to be stale. The
early-return paths still describe the aggregate this context loaded itself, which is correct and
commented as such: nothing has been called, so that view *is* the current state.

**Lesson.** Two rules, and the second is the general one.

- **A handler that dispatches a command into a new scope must not read the result back through
  its own context.** It is holding a snapshot from before the call, and EF will hand that snapshot
  back without telling anybody. Have the command return what changed.
- **"Where did this value come from?" is a question worth asking of every field in a response.**
  Not "is it correct" — it was correct, as of a moment that had passed. A response is a claim
  about the present, and a value read through a stale cache is a claim about the past with no
  marker saying so.
