using BoomboCard.Promotions.Contracts;
using BoomboCard.Promotions.Data;
using BoomboCard.Promotions.Data.Repositories;
using BoomboCard.Promotions.Domain.PromotionAggregate;
using BoomboCard.Promotions.Domain.PromotionAggregate.Events;
using BoomboCard.SharedKernel;
using ErrorOr;
using MediatR;

namespace BoomboCard.Promotions.Application.Promotions.Commands;

/// <summary>
/// The terms of a campaign, as a request contract.
///
/// <para>
/// <b>This record is the answer to M16.</b> Onyx's promotions were two entries in a
/// <c>private static readonly Dictionary&lt;string, DateTime&gt;</c>, so creating a campaign
/// meant editing C#, opening a pull request and deploying — and the one it shipped,
/// <c>SUMMER25</c>, had an expiry date in the past from the moment it was written. Promotions
/// being data rather than code is what makes a merchandiser able to create one, and what makes
/// "is this campaign live?" a question with an answer.
/// </para>
///
/// <para>
/// <c>EndsAtUtc</c> is <b>exclusive</b>. "Valid through 31 December" is
/// <c>2027-01-01T00:00:00Z</c>. Stating the instant rather than a date plus a convention is what
/// closes the <b>M17</b> half of Onyx's expiry bug — its comment said "through the end of the
/// listed day" and its code compared against midnight at the start of it, in server local time.
/// </para>
///
/// <para>
/// Supply exactly <b>one</b> of <c>DiscountRate</c> (a fraction, so <c>0.10</c> is 10%) or
/// <c>DiscountAmount</c> with a <c>Currency</c> — accepting both and silently preferring one is
/// how a campaign ends up doing something other than what was configured.
/// <c>MinimumSpend</c>, <c>MaxRedemptions</c> and <c>MaxRedemptionsPerCustomer</c> are null for
/// none and unlimited respectively. <c>ProductIds</c> is null or empty for the whole basket;
/// named products scope the discount to <i>their</i> lines.
/// </para>
/// </summary>
public sealed record PromotionTerms(
    string Description,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc,
    decimal? DiscountRate = null,
    decimal? DiscountAmount = null,
    string? Currency = null,
    decimal? MinimumSpend = null,
    int? MaxRedemptions = null,
    int? MaxRedemptionsPerCustomer = null,
    IReadOnlyList<Guid>? ProductIds = null,
    StackingPolicy Stacking = StackingPolicy.Exclusive);

/// <summary>
/// Creates a campaign. It starts as a <c>Draft</c> and cannot be used until activated.
/// </summary>
public sealed record CreatePromotionCommand(string Code, PromotionTerms Terms)
    : IRequest<ErrorOr<Guid>>, IPromotionsRequest;

internal sealed class CreatePromotionCommandHandler(
    IPromotionRepository promotions,
    IUnitOfWork<PromotionsDbContext> unitOfWork,
    TimeProvider timeProvider)
    : IRequestHandler<CreatePromotionCommand, ErrorOr<Guid>>
{
    public async Task<ErrorOr<Guid>> Handle(
        CreatePromotionCommand command,
        CancellationToken cancellationToken)
    {
        var code = PromotionCode.Create(command.Code);

        if (code.IsError)
        {
            return code.Errors;
        }

        var built = PromotionTermsBuilder.Build(command.Terms);

        if (built.IsError)
        {
            return built.Errors;
        }

        // A read, so two simultaneous creates for the same code can both pass it.
        // UX_Promotions_Code is what guarantees one row per code; this check turns the common
        // case into a 409 with a useful message rather than a duplicate-key exception.
        if (await promotions.CodeExistsAsync(code.Value, cancellationToken))
        {
            return PromotionErrors.CodeAlreadyExists(code.Value.Value);
        }

        var terms = built.Value;

        var promotion = Promotion.Create(
            code.Value,
            command.Terms.Description,
            terms.Discount,
            terms.Window,
            terms.Scope,
            command.Terms.Stacking,
            terms.MinimumSpend,
            command.Terms.MaxRedemptions,
            command.Terms.MaxRedemptionsPerCustomer,
            timeProvider.GetUtcNow());

        if (promotion.IsError)
        {
            return promotion.Errors;
        }

        promotions.Add(promotion.Value);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return promotion.Value.Id;
    }
}

/// <summary>
/// Replaces a draft campaign's terms. Draft only — see <c>Promotion.Reconfigure</c>.
/// </summary>
public sealed record ReconfigurePromotionCommand(Guid PromotionId, PromotionTerms Terms)
    : IRequest<ErrorOr<Success>>, IPromotionsRequest;

internal sealed class ReconfigurePromotionCommandHandler(
    IPromotionRepository promotions,
    IUnitOfWork<PromotionsDbContext> unitOfWork,
    TimeProvider timeProvider)
    : IRequestHandler<ReconfigurePromotionCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        ReconfigurePromotionCommand command,
        CancellationToken cancellationToken)
    {
        var built = PromotionTermsBuilder.Build(command.Terms);

        if (built.IsError)
        {
            return built.Errors;
        }

        var promotion = await promotions.GetByIdAsync(command.PromotionId, cancellationToken);

        if (promotion is null)
        {
            return PromotionErrors.NotFound(command.PromotionId);
        }

        var terms = built.Value;

        var reconfigured = promotion.Reconfigure(
            command.Terms.Description,
            terms.Discount,
            terms.Window,
            terms.Scope,
            command.Terms.Stacking,
            terms.MinimumSpend,
            command.Terms.MaxRedemptions,
            command.Terms.MaxRedemptionsPerCustomer,
            timeProvider.GetUtcNow());

        if (reconfigured.IsError)
        {
            return reconfigured.Errors;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success;
    }
}

public sealed record ActivatePromotionCommand(Guid PromotionId)
    : IRequest<ErrorOr<Success>>, IPromotionsRequest;

internal sealed class ActivatePromotionCommandHandler(
    IPromotionRepository promotions,
    IUnitOfWork<PromotionsDbContext> unitOfWork,
    TimeProvider timeProvider)
    : IRequestHandler<ActivatePromotionCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        ActivatePromotionCommand command,
        CancellationToken cancellationToken)
    {
        var promotion = await promotions.GetByIdAsync(command.PromotionId, cancellationToken);

        if (promotion is null)
        {
            return PromotionErrors.NotFound(command.PromotionId);
        }

        var activated = promotion.Activate(timeProvider.GetUtcNow());

        if (activated.IsError)
        {
            return activated.Errors;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success;
    }
}

public sealed record PausePromotionCommand(Guid PromotionId)
    : IRequest<ErrorOr<Success>>, IPromotionsRequest;

internal sealed class PausePromotionCommandHandler(
    IPromotionRepository promotions,
    IUnitOfWork<PromotionsDbContext> unitOfWork,
    TimeProvider timeProvider)
    : IRequestHandler<PausePromotionCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        PausePromotionCommand command,
        CancellationToken cancellationToken)
    {
        var promotion = await promotions.GetByIdAsync(command.PromotionId, cancellationToken);

        if (promotion is null)
        {
            return PromotionErrors.NotFound(command.PromotionId);
        }

        var paused = promotion.Pause(timeProvider.GetUtcNow());

        if (paused.IsError)
        {
            return paused.Errors;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success;
    }
}

/// <summary>
/// Ends a campaign for good. Not reversible — see <c>PromotionStatus</c>.
/// </summary>
public sealed record ClosePromotionCommand(Guid PromotionId)
    : IRequest<ErrorOr<Success>>, IPromotionsRequest;

internal sealed class ClosePromotionCommandHandler(
    IPromotionRepository promotions,
    IUnitOfWork<PromotionsDbContext> unitOfWork,
    TimeProvider timeProvider)
    : IRequestHandler<ClosePromotionCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        ClosePromotionCommand command,
        CancellationToken cancellationToken)
    {
        var promotion = await promotions.GetByIdAsync(command.PromotionId, cancellationToken);

        if (promotion is null)
        {
            return PromotionErrors.NotFound(command.PromotionId);
        }

        var closed = promotion.Close(PromotionCloseReason.Withdrawn, timeProvider.GetUtcNow());

        if (closed.IsError)
        {
            return closed.Errors;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success;
    }
}

/// <summary>
/// Turns the flat request contract into the module's value objects.
///
/// <para>
/// Shared by create and reconfigure so there is one definition of how the wire shape maps to the
/// domain — the alternative is two mappings that have to agree, which is the <b>M14</b> shape
/// again at a smaller scale.
/// </para>
/// </summary>
internal static class PromotionTermsBuilder
{
    internal sealed record Built(
        Discount Discount,
        ValidityWindow Window,
        PromotionScope Scope,
        Money? MinimumSpend);

    public static ErrorOr<Built> Build(PromotionTerms terms)
    {
        // Exactly one of the two discount forms. Accepting both and silently preferring one is
        // how a campaign ends up doing something other than what the merchandiser configured.
        var hasRate = terms.DiscountRate is not null;
        var hasAmount = terms.DiscountAmount is not null;

        if (hasRate == hasAmount)
        {
            return Error.Validation(
                "Promotion.DiscountFormAmbiguous",
                "Supply exactly one of discountRate (a fraction, so 0.10 is 10%) or "
                + "discountAmount with a currency.");
        }

        ErrorOr<Discount> discount;

        if (hasRate)
        {
            discount = Discount.Percentage(terms.DiscountRate!.Value);
        }
        else
        {
            var amount = Money.Create(terms.DiscountAmount!.Value, terms.Currency);

            if (amount.IsError)
            {
                return amount.Errors;
            }

            discount = Discount.Fixed(amount.Value);
        }

        if (discount.IsError)
        {
            return discount.Errors;
        }

        var window = ValidityWindow.Create(terms.StartsAtUtc, terms.EndsAtUtc);

        if (window.IsError)
        {
            return window.Errors;
        }

        Money? minimumSpend = null;

        if (terms.MinimumSpend is { } minimum)
        {
            // A minimum spend needs a currency even for a percentage discount: "10% off orders
            // over 50" means a different thing in USD and AED, so the campaign has to say which.
            var currency = terms.Currency
                ?? (discount.Value.Kind is DiscountKind.FixedAmount
                    ? discount.Value.Amount!.CurrencyCode
                    : null);

            var money = Money.Create(minimum, currency);

            if (money.IsError)
            {
                return money.Errors;
            }

            minimumSpend = money.Value;
        }

        var scope = terms.ProductIds is { Count: > 0 }
            ? PromotionScope.ForProducts(terms.ProductIds)
            : PromotionScope.EntireBasket;

        return new Built(discount.Value, window.Value, scope, minimumSpend);
    }
}
