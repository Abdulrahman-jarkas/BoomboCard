using BoomboCard.Payments.Contracts;
using BoomboCard.Payments.Data;
using BoomboCard.SharedKernel;
using BoomboCard.SharedKernel.Persistence;

namespace BoomboCard.Payments.Application;

/// <summary>
/// Payments' transaction boundary: <see cref="ModuleUnitOfWorkBehavior{TRequest,TResponse}"/>
/// closed over this module's context and constrained to its requests.
///
/// <para>
/// <b>Payments is the module where what this behaviour does NOT wrap matters most.</b>
/// <c>SubmitPaymentCommand</c> and <c>RefundPaymentCommand</c> deliberately carry no
/// <see cref="IPaymentsRequest"/> marker, so they are never wrapped — they call a payment
/// provider over HTTP, and Onyx <b>A12</b> held a transaction across exactly that call. Each of
/// them dispatches a marked command afterwards to record the outcome, which is what gets the
/// short transaction.
/// </para>
/// </summary>
internal sealed class PaymentsUnitOfWorkBehavior<TRequest, TResponse>(
    IUnitOfWork<PaymentsDbContext> unitOfWork,
    IDomainEventDispatcher<PaymentsDbContext> dispatcher)
    : ModuleUnitOfWorkBehavior<TRequest, TResponse>(unitOfWork, dispatcher)
    where TRequest : notnull, IPaymentsRequest;
