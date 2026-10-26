using BoomboCard.SharedKernel;
using BoomboCard.SharedKernel.Persistence.Inbox;
using BoomboCard.SharedKernel.Persistence.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BoomboCard.SharedKernel.Persistence;

public static class ModulePersistenceExtensions
{
    /// <summary>
    /// Wires one module's unit of work, domain event dispatcher, outbox writer and inbox guard.
    ///
    /// <para>
    /// All four are registered unconditionally. RiverBooks takes
    /// <c>IDomainEventDispatcher?</c> and returns early when it is null — so forgetting one DI
    /// line silently stops every domain event in the application from firing, with no exception
    /// and no log. A missing registration here fails at resolution instead.
    /// </para>
    ///
    /// <para>
    /// <b>Every registration is against the generic, context-typed interface</b>
    /// (<c>IUnitOfWork&lt;TContext&gt;</c>), and none against the bare one. This method used to
    /// register the bare interfaces, which worked exactly as long as there was one module: the
    /// second call replaced the first for every handler in the process, so a Catalog handler
    /// would save a Sales context, write nothing, and have an empty transaction committed over
    /// the top of it. Nothing threw and nothing logged. Typing the registration on the context
    /// turns that into a compile error.
    /// </para>
    /// </summary>
    public static IServiceCollection AddModulePersistence<TContext>(this IServiceCollection services)
        where TContext : DbContext
    {
        services.AddScoped<IUnitOfWork<TContext>, EfUnitOfWork<TContext>>();
        services.AddScoped<IDomainEventDispatcher<TContext>, DomainEventDispatcher<TContext>>();
        services.AddScoped<IOutboxWriter<TContext>, OutboxWriter<TContext>>();
        services.AddScoped<IInboxGuard<TContext>, InboxGuard<TContext>>();

        return services;
    }
}
