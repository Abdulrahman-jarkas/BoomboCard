using BoomboCard.SharedKernel;
using BoomboCard.SharedKernel.Persistence.Observability;
using Microsoft.Extensions.DependencyInjection;

namespace BoomboCard.SharedKernel.Persistence;

public static class SharedServiceExtensions
{
    /// <summary>
    /// The services every module needs exactly one of, registered once by the host.
    ///
    /// <para>
    /// This used to live in <c>BoomboCard.Catalog.DependencyInjection</c>, where it was already
    /// documented as "registered once by the host, not per module" — a host-wide concern
    /// implemented inside a module, which worked only because there was one module. The second
    /// module made it visible: <c>BoomboCard.Customers</c> would have had to reference
    /// <c>BoomboCard.Catalog</c> to compose the host's shared services, which is precisely the
    /// cross-module reference <c>Modules_SeeEachOtherOnlyThroughContracts</c> forbids.
    /// </para>
    ///
    /// <para>
    /// Note what is NOT here: the unit-of-work pipeline behaviour. It is per module, because the
    /// unit of work is — see <see cref="ModuleUnitOfWorkBehavior{TRequest,TResponse}"/>. The
    /// tracing behaviour, by contrast, IS here: it holds nothing per module, so one open-generic
    /// registration covers every request in every module.
    /// </para>
    /// </summary>
    public static IServiceCollection AddBoomboCardSharedServices(this IServiceCollection services)
    {
        // FIRST, and that is not incidental. MediatR resolves pipeline behaviours in DI
        // registration order, and the host calls this method before any AddXModule — so the
        // tracing span encloses each module's transaction instead of nesting inside it. A
        // command's span therefore covers its commit, which is where a surprising amount of a
        // command's time and a fair share of its failures actually live.
        services.AddTransient(
            typeof(MediatR.IPipelineBehavior<,>),
            typeof(TracingBehavior<,>));

        // TimeProvider is registered here because nothing in this codebase calls
        // DateTime.UtcNow directly. Onyx shipped a ClockHelper that wrapped it and then used it
        // in zero places (A30), so no time-dependent rule — promo expiry, token expiry, report
        // windows — could be unit tested; and it wrote DateTime.Now into a field named
        // OrderDateUtc (A29). A SourceConventionTests rule fails the build on either.
        //
        // TryAdd semantics, so a test can substitute a FakeTimeProvider before calling this.
        if (services.All(descriptor => descriptor.ServiceType != typeof(TimeProvider)))
        {
            services.AddSingleton(TimeProvider.System);
        }

        services.AddSingleton<VersioningInterceptor>();

        // Singleton: it creates its own DI scope per attempt, so it must not capture a scoped
        // DbContext. That captive-dependency mistake is exactly Onyx A1 — a singleton
        // ProductCache holding a scoped DbContext, which the DI scope validator correctly
        // detected and which A2 then switched the validator off to hide.
        services.AddSingleton<ICommandDispatcher, ResilientCommandDispatcher>();

        return services;
    }
}
