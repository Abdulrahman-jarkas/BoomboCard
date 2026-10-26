using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace BoomboCard.SharedKernel.Persistence;

public static class ModuleDbContextOptions
{
    /// <summary>
    /// The EF-level conventions every module's context opts into, applied by the module inside
    /// its own <c>AddDbContext</c> call.
    ///
    /// <para>
    /// <b>Row limiting without an ORDER BY throws.</b> EF reports this as a warning, and a
    /// warning is what Onyx did with NU1903 on every single compile (S18). It was found here
    /// the honest way: the host logged it twice at startup, from
    /// <c>StockIntegrityCheckJob</c> and <c>ExportMaintenanceJob</c>, both of which took a
    /// capped batch with no sort key. Without one the database may return any N of the matching
    /// rows, so a given row can be starved indefinitely while the same ones come back every
    /// pass — in the integrity check's case, a stock discrepancy reported for a while and then
    /// silently not. Onyx A27 is the same defect on a customer-facing endpoint.
    /// </para>
    ///
    /// <para>
    /// Promoting it to an exception is safe to do in production as well as development: it
    /// throws during query translation, so it is deterministic per query shape and surfaces on
    /// the first execution of a bad query rather than selectively under load. A text-scanning
    /// convention test could not see this — the sort key may be applied anywhere in the chain,
    /// or come from a repository method — but the query compiler can.
    /// </para>
    /// </summary>
    public static DbContextOptionsBuilder UseBoomboCardConventions(this DbContextOptionsBuilder options) =>
        options.ConfigureWarnings(Configure);

    /// <summary>
    /// The typed overload, so the conventions can be applied mid-chain on a
    /// <c>DbContextOptionsBuilder&lt;TContext&gt;</c> without losing the generic type — which is
    /// what the test fixture builds.
    /// </summary>
    public static DbContextOptionsBuilder<TContext> UseBoomboCardConventions<TContext>(
        this DbContextOptionsBuilder<TContext> options)
        where TContext : DbContext =>
        options.ConfigureWarnings(Configure);

    private static void Configure(WarningsConfigurationBuilder warnings) =>
        warnings.Throw(CoreEventId.RowLimitingOperationWithoutOrderByWarning);
}
