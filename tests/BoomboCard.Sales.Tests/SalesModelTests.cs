using BoomboCard.Sales.Domain.CheckoutSessionAggregate;
using BoomboCard.Sales.Domain.Numbering;
using BoomboCard.Sales.Domain.OrderAggregate;
using BoomboCard.Sales.Domain.Pricing;
using BoomboCard.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace BoomboCard.Sales.Tests;

/// <summary>
/// What the database guarantees, asserted against the database.
///
/// <para>
/// Every test here would pass vacuously on the InMemory provider, which is Onyx <b>T6</b>: its
/// one concurrency test used InMemory, so it proved the opposite of what it claimed — the
/// provider has no unique indexes and no concurrency tokens, so two conflicting writes both
/// succeed and the test goes green.
/// </para>
/// </summary>
public sealed class SalesModelTests(SqlServerFixture fixture) : IClassFixture<SqlServerFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private static CheckoutSession HeldSession()
    {
        var session = CheckoutSession.Start(
            Guid.NewGuid(),
            Country.UnitedStates,
            ShippingPolicy.UnitedStates,
            [new QuotedLine(Guid.NewGuid(), "SKU-1", "A thing", 1, 50.00m, Guid.NewGuid())],
            promotion: null,
            loyalty: null,
            Now).Value;

        session.BeginHolding(Now);

        return session;
    }

    private static Order OrderFor(CheckoutSession session, long sequence)
    {
        var order = Order.PlaceFrom(session, OrderNumber.FromSequence(sequence), Now).Value;
        session.Confirm(order.Id, Now);

        return order;
    }

    // ----------------------------------------------------------------- uniqueness

    /// <summary>
    /// <b>Confirm idempotency, and the reason it is a guarantee rather than a hope.</b>
    ///
    /// <para>
    /// <c>ConfirmCheckoutCommand</c> checks for an existing order before placing one, but that
    /// check is a read — two concurrent confirms of the same session can both pass it. This index
    /// is what makes the second insert fail, roll back, and be retried by
    /// <c>ICommandDispatcher</c>, at which point the read sees the committed order and returns
    /// it. The constraint is the guarantee; the read is the fast path.
    /// </para>
    ///
    /// <para>
    /// Onyx <b>M3</b> is "no duplicate-order protection" and <b>D3</b> is order creation
    /// committing N+2 separate times. A customer double-clicking Confirm got two orders and two
    /// stock deductions.
    /// </para>
    /// </summary>
    [Fact]
    public async Task OneSessionCannotProduceTwoOrders()
    {
        var session = HeldSession();

        await using (var setup = fixture.CreateContext())
        {
            setup.CheckoutSessions.Add(session);
            setup.Orders.Add(OrderFor(session, 1));
            await setup.SaveChangesAsync();
        }

        // A second, independent writer — the same shape as a second request in a second scope.
        await using var second = fixture.CreateContext();
        second.Orders.Add(OrderFor(HeldSessionWith(session.Id), 2));

        // UX_Orders_CheckoutSession.
        await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());
    }

    /// <summary>
    /// <b>D8.</b> Onyx's order number came from a <c>static int</c> that reset to 1000 on every
    /// restart and was per-process, so numbers repeated across restarts and across instances.
    /// </summary>
    [Fact]
    public async Task TwoOrdersCannotShareANumber()
    {
        var first = HeldSession();
        var secondSession = HeldSession();

        await using (var setup = fixture.CreateContext())
        {
            setup.CheckoutSessions.AddRange(first, secondSession);
            setup.Orders.Add(OrderFor(first, 7));
            await setup.SaveChangesAsync();
        }

        await using var second = fixture.CreateContext();
        second.Orders.Add(OrderFor(secondSession, 7));

        // UX_Orders_OrderNumber.
        await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());
    }

    /// <summary>
    /// <b>M3 and M5 — the duplicate-payment guarantee, and the reason <c>OrderPayment</c> is a
    /// row rather than a nullable column on the order.</b>
    ///
    /// <para>
    /// A nullable unique column does not work here: SQL Server treats two <c>NULL</c>s as equal
    /// in a unique index, so exactly one unpaid order could exist. The usual workaround is a
    /// filtered index, which is provider-specific SQL written into a module that is supposed to
    /// be provider-agnostic. Making the capture a row that either exists or does not means a
    /// plain unique index gives the guarantee on any provider.
    /// </para>
    ///
    /// <para>
    /// Onyx's payment path had no idempotency key at all, and its PR asserted the retry was safe
    /// because "the gateway call is idempotent on their end" — with nothing in the request
    /// identifying the payment.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheSameProviderReferenceCannotBeCapturedTwice()
    {
        var first = HeldSession();
        var secondSession = HeldSession();

        await using (var setup = fixture.CreateContext())
        {
            var order = OrderFor(first, 11);
            order.MarkPaid("pi_shared_reference", Now);

            setup.CheckoutSessions.AddRange(first, secondSession);
            setup.Orders.Add(order);
            await setup.SaveChangesAsync();
        }

        await using var second = fixture.CreateContext();
        var other = OrderFor(secondSession, 12);
        other.MarkPaid("pi_shared_reference", Now);
        second.Orders.Add(other);

        // UX_OrderPayments_Reference, and global rather than per order on purpose: a provider
        // reference identifies a transaction at the provider, so the same one against two orders
        // is a bug worth failing on rather than a coincidence worth allowing.
        await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());
    }

    /// <summary>
    /// <b>Duplicate products are collapsed before anything is priced</b>, and the unique index
    /// is the backstop behind that.
    ///
    /// <para>
    /// The collapse is the behaviour that matters: the stock idempotency key is per product and
    /// reference, so two lines for one product in a single checkout would collide with
    /// themselves at the deduction. Catalog's <c>DeductStockCommandHandler</c> groups for the
    /// same reason, and doing it in the quote too means the basket the customer sees matches
    /// what will be held.
    /// </para>
    ///
    /// <para>
    /// A rejection would also be defensible, but it would push the merge into every caller and
    /// surface as an error on a basket the customer sees nothing wrong with.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TwoLinesForOneProductAreCollapsedIntoOne()
    {
        var productId = Guid.NewGuid();

        var session = CheckoutSession.Start(
            Guid.NewGuid(),
            Country.UnitedStates,
            ShippingPolicy.UnitedStates,
            [
                new QuotedLine(productId, "SKU-1", "A thing", 1, 10.00m, Guid.NewGuid()),
                new QuotedLine(productId, "SKU-1", "A thing", 2, 10.00m, Guid.NewGuid())
            ],
            promotion: null,
            loyalty: null,
            Now);

        Assert.False(session.IsError);

        var line = Assert.Single(session.Value.Lines);
        Assert.Equal(3, line.Quantity);
        Assert.Equal(30.00m, line.LineTotal.Amount);
        Assert.Equal(30.00m, session.Value.MerchandiseSubtotal.Amount);

        // And it persists, because there is only ever one row per product.
        await using var context = fixture.CreateContext();
        context.CheckoutSessions.Add(session.Value);
        await context.SaveChangesAsync();
    }

    /// <summary>
    /// <c>UX_CheckoutLines_SessionProduct</c>, asserted directly rather than through the
    /// aggregate that collapses duplicates before they reach it — so the index is proven to
    /// exist rather than assumed from the fact that nothing has needed it.
    /// </summary>
    [Fact]
    public async Task TheDatabaseRefusesTwoRowsForOneProductInOneSession()
    {
        var productId = Guid.NewGuid();
        var session = HeldSession();

        await using (var setup = fixture.CreateContext())
        {
            setup.CheckoutSessions.Add(session);
            await setup.SaveChangesAsync();
        }

        await using var context = fixture.CreateContext();

        context.CheckoutLines.Add(
            CheckoutLine.Create(session.Id, productId, "SKU-X", "A thing", 1, Usd(5.00m), Guid.NewGuid()).Value);
        context.CheckoutLines.Add(
            CheckoutLine.Create(session.Id, productId, "SKU-X", "A thing", 2, Usd(5.00m), Guid.NewGuid()).Value);

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    // ----------------------------------------------------------------- concurrency

    /// <summary>
    /// <b>The order-number sequence under contention.</b>
    ///
    /// <para>
    /// Two processes allocating a number at the same instant both read the same
    /// <c>NextValue</c>. The concurrency token on the singleton row is what stops them both
    /// writing it: the loser's update matches zero rows, EF throws
    /// <c>DbUpdateConcurrencyException</c>, and <c>ICommandDispatcher</c> retries the whole
    /// command in a fresh scope with a fresh context — which reloads the sequence and allocates
    /// the next number.
    /// </para>
    ///
    /// <para>
    /// This is the test Onyx <b>T6</b> claimed to have written. Its version used the InMemory
    /// provider, which has no concurrency tokens, so both writes succeeded and the test asserted
    /// that the race was safe.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TwoWritersCannotBothAllocateTheSameOrderNumber()
    {
        // The fixture is per test CLASS and the sequence is a singleton row, so it may already
        // exist — created by whichever sequence test ran first. Asserting a clean slate here
        // would make this test's result depend on xUnit's ordering, which is the shape of the
        // Phase 3 bug recorded in BUGS-AND-LESSONS section 6.
        await EnsureSequenceExistsAsync();

        await using var first = fixture.CreateContext();
        await using var second = fixture.CreateContext();

        var firstSequence = await first.OrderNumberSequences
            .FirstAsync(row => row.Id == OrderNumberSequence.SingletonId);
        var secondSequence = await second.OrderNumberSequences
            .FirstAsync(row => row.Id == OrderNumberSequence.SingletonId);

        var firstNumber = firstSequence.Allocate();
        var secondNumber = secondSequence.Allocate();

        // Both read the same value, so in memory they agree — which is exactly the bug the
        // database has to catch.
        Assert.Equal(firstNumber.Value, secondNumber.Value);

        await first.SaveChangesAsync();

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }

    /// <summary>
    /// The sequence creates itself on first use, so there is no seeding step to forget and no
    /// migration that has to insert data. Two simultaneous first orders both try, and the primary
    /// key settles it.
    /// </summary>
    [Fact]
    public async Task TheSequenceIsASingletonRow()
    {
        await EnsureSequenceExistsAsync();

        await using var second = fixture.CreateContext();
        second.OrderNumberSequences.Add(OrderNumberSequence.Create());

        await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());
    }

    // ----------------------------------------------------------------- round trips

    /// <summary>
    /// Money survives the round trip exactly.
    ///
    /// <para>
    /// Onyx <b>X10</b> claimed "all monetary values use <c>decimal(18,2)</c>" while configuring
    /// precision nowhere, so SQLite stored every amount as a float and 19.99 came back as
    /// 19.989999999999998. Every <c>Money</c> column here has explicit precision, and a figure
    /// that did not round-trip would make every total in the system approximate.
    /// </para>
    /// </summary>
    [Fact]
    public async Task EveryMonetaryFigureOnAnOrderRoundTripsExactly()
    {
        var session = CheckoutSession.Start(
            Guid.NewGuid(),
            Country.UnitedStates,
            ShippingPolicy.UnitedStates,
            [new QuotedLine(Guid.NewGuid(), "SKU-1", "A thing", 3, 19.99m, Guid.NewGuid())],
            promotion: new PromotionInput(Guid.NewGuid(), "SAVE7", 7.77m),
            loyalty: null,
            Now).Value;

        session.BeginHolding(Now);
        var order = OrderFor(session, 21);

        await using (var setup = fixture.CreateContext())
        {
            setup.CheckoutSessions.Add(session);
            setup.Orders.Add(order);
            await setup.SaveChangesAsync();
        }

        await using var read = fixture.CreateContext();
        var reloaded = await read.Orders
            .Include(candidate => candidate.Items)
            .Include(candidate => candidate.Adjustments)
            .FirstAsync(candidate => candidate.Id == order.Id);

        Assert.Equal(order.MerchandiseSubtotal.Amount, reloaded.MerchandiseSubtotal.Amount);
        Assert.Equal(order.Shipping.Amount, reloaded.Shipping.Amount);
        Assert.Equal(order.Tax.Amount, reloaded.Tax.Amount);
        Assert.Equal(order.Total.Amount, reloaded.Total.Amount);
        Assert.Equal(order.QualifyingSpend.Amount, reloaded.QualifyingSpend.Amount);
        Assert.Equal(order.Number.Value, reloaded.Number.Value);

        // And the invariant SalesIntegrityCheckJob asserts in production, asserted here against
        // a figure that has actually been through the database.
        Assert.True(reloaded.TotalMatchesItsRows());
    }

    /// <summary>
    /// The order number is a real column, not a value stored inside something else — which is
    /// what lets a customer look an order up by the number on their email.
    ///
    /// <para>
    /// An owned type rather than a value converter: the rule Catalog settled the hard way
    /// (<c>BUGS-AND-LESSONS</c> §3), because a converted value cannot be matched server-side and
    /// the query silently becomes a client-side scan of the table.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AnOrderCanBeFoundByItsNumberInTheDatabase()
    {
        var session = HeldSession();
        var order = OrderFor(session, 31);

        await using (var setup = fixture.CreateContext())
        {
            setup.CheckoutSessions.Add(session);
            setup.Orders.Add(order);
            await setup.SaveChangesAsync();
        }

        await using var read = fixture.CreateContext();

        var found = await read.Orders
            .FirstOrDefaultAsync(candidate => candidate.Number.Value == "BC-00000031");

        Assert.NotNull(found);
        Assert.Equal(order.Id, found.Id);
    }

    /// <summary>
    /// A session's status is a queryable string column, which is what the expiry sweeper's
    /// <c>WHERE</c> clause depends on.
    /// </summary>
    [Fact]
    public async Task ExpiredSessionsAreFoundByTheSweepersQuery()
    {
        var live = HeldSession();

        await using (var setup = fixture.CreateContext())
        {
            setup.CheckoutSessions.Add(live);
            await setup.SaveChangesAsync();
        }

        await using var read = fixture.CreateContext();

        var expired = await read.CheckoutSessions
            .Where(session =>
                (session.Status.Value == CheckoutStatusValue.Open
                    || session.Status.Value == CheckoutStatusValue.Holding)
                && session.ExpiresAtUtc <= Now + CheckoutSession.Lifetime)
            .OrderBy(session => session.ExpiresAtUtc)
            .Take(10)
            .Select(session => session.Id)
            .ToListAsync();

        Assert.Contains(live.Id, expired);
    }

    /// <summary>
    /// <b>BUGS-AND-LESSONS §8, promoted to a thrown exception.</b> A capped query with no
    /// <c>ORDER BY</c> returns an arbitrary page, so a sweeper could skip the same rows pass
    /// after pass — and these are sessions holding stock.
    ///
    /// <para>
    /// <c>UseBoomboCardConventions</c> turns EF's warning for it into an exception, so the bug
    /// cannot ship. This asserts the guard is actually wired on Sales' context rather than only
    /// on the ones where it was first needed.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ACappedQueryWithNoOrderByThrows()
    {
        await using var context = fixture.CreateContext();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            context.CheckoutSessions.Take(5).ToListAsync());
    }

    private static Money Usd(decimal amount) => Money.Create(amount, "USD").Value;

    /// <summary>
    /// Creates the singleton sequence row if no test has created it yet. Idempotent, because the
    /// per-class fixture means these tests share a database and their order is not defined.
    /// </summary>
    private async Task EnsureSequenceExistsAsync()
    {
        await using var context = fixture.CreateContext();

        if (await context.OrderNumberSequences
                .AnyAsync(row => row.Id == OrderNumberSequence.SingletonId))
        {
            return;
        }

        context.OrderNumberSequences.Add(OrderNumberSequence.Create());
        await context.SaveChangesAsync();
    }

    private static CheckoutSession HeldSessionWith(Guid sessionId)
    {
        // The second confirm references the SAME session id — which is what UX_Orders_CheckoutSession
        // is there to reject. Reflection is the only way to force the id, because a session's id
        // is assigned at creation and nothing is allowed to change it.
        var session = HeldSession();

        typeof(Entity)
            .GetProperty(nameof(Entity.Id))!
            .SetValue(session, sessionId);

        return session;
    }
}
