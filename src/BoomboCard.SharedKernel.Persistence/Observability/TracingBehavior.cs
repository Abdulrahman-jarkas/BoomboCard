using System.Diagnostics;
using BoomboCard.SharedKernel;
using BoomboCard.SharedKernel.Observability;
using MediatR;

namespace BoomboCard.SharedKernel.Persistence.Observability;

/// <summary>
/// A span per request through the MediatR pipeline — every command and every query, in every
/// module.
///
/// <para>
/// This is the backbone a flow hangs off. ASP.NET Core's instrumentation gives the inbound
/// request a root span, and the outbox dispatcher re-roots an asynchronous handoff, but between
/// those two the interesting structure is which handler ran and what it called: a Sales confirm
/// sends commands into three other modules, and the only way to see that nesting is to span the
/// request itself.
/// </para>
///
/// <para>
/// <b>Not abstract, and not per module</b> — unlike
/// <see cref="ModuleUnitOfWorkBehavior{TRequest,TResponse}"/>, which has to be closed once per
/// module because the unit of work it depends on is per module. This behaviour depends on
/// nothing scoped, so there is no per-module state to get wrong and no reason to make seven
/// subclasses of it. It is registered once by the host in
/// <c>AddBoomboCardSharedServices</c> and the module is read off the request's own namespace.
/// </para>
///
/// <para>
/// <b>Registered first, so it runs outermost.</b> MediatR resolves behaviours in DI registration
/// order, and the host calls <c>AddBoomboCardSharedServices</c> before any
/// <c>AddXModule</c> — so this span encloses the transaction rather than sitting inside it. That
/// ordering is what makes the span's duration the real wall-clock cost of the command including
/// its commit, and what lets a failure be attributed to the command even when it is the commit
/// that threw.
/// </para>
///
/// <para>
/// It constrains <typeparamref name="TRequest"/> to nothing but <c>notnull</c>, deliberately. The
/// transaction boundary's constraint on a module marker is load-bearing and asserted by
/// <c>ModuleWiringTests</c>; here the opposite is wanted — a request that somehow carries no
/// module marker gets no transaction, which is a bug those tests exist to catch, and it should
/// still be traced while being wrong.
/// </para>
/// </summary>
public sealed class TracingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private static readonly string RequestName = typeof(TRequest).Name;

    private static readonly string ModuleName = ModuleOf(typeof(TRequest));

    /// <summary>
    /// Commands and queries are told apart by the same marker the pipeline uses to decide
    /// whether to open a transaction, so the attribute cannot disagree with the behaviour.
    /// </summary>
    private static readonly string RequestKind =
        typeof(ITransactionalRequest).IsAssignableFrom(typeof(TRequest)) ? "command" : "query";

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        using var activity = BoomboCardTelemetry.Source.StartActivity(
            $"{RequestKind} {RequestName}",
            ActivityKind.Internal);

        // Nothing from the request itself goes on the span. Request objects carry card holder
        // names, addresses, webhook payloads and tokens, and a trace store is a copy of
        // production data in a system with different retention and a wider audience — which is
        // the shape of Onyx S10, where feature.Error.ToString() went into the response body in
        // every environment. The type name and the module are enough to read a waterfall; an id
        // worth correlating on is added by the handler that knows it is not a secret.
        activity?.SetTag(BoomboCardTelemetry.ModuleTag, ModuleName);
        activity?.SetTag(BoomboCardTelemetry.MessageTypeTag, RequestName);
        activity?.SetTag(BoomboCardTelemetry.RequestKindTag, RequestKind);

        try
        {
            return await next();
        }
        catch (Exception exception)
        {
            // Recorded and rethrown. The exception handler in the host turns it into a
            // ProblemDetails response; this makes sure the span says which request failed and
            // why before that happens.
            activity.RecordFailure(exception);
            throw;
        }
    }

    /// <summary>
    /// <c>BoomboCard.Catalog.Application.Products.ChangePriceCommand</c> becomes <c>Catalog</c>.
    ///
    /// <para>
    /// Read off the namespace, because the alternative is a registration that names the module
    /// and a module that does not get one has its spans silently attributed to nothing. The
    /// architecture tests already derive module namespaces from the module name the same way,
    /// which is what makes the convention safe to read: a module that put its types somewhere
    /// else would fail <c>Domain_DoesNotDependOnOuterLayers</c> and friends long before this
    /// mislabelled a span.
    /// </para>
    /// </summary>
    private static string ModuleOf(Type requestType)
    {
        var segments = (requestType.Namespace ?? string.Empty).Split('.');

        return segments.Length >= 2 && segments[0] == "BoomboCard" ? segments[1] : "Host";
    }
}
