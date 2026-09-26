using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using FluentValidation;
using FluentValidation.Results;
using Mediator;

namespace Evidgate.BuildingBlocks.Pipeline.Behaviours;

/// <summary>
/// C2 stage 2 (00 §5.5 C2: validation): runs every <see cref="IValidator{TMessage}"/>
/// registered for the message — 422 <c>request_invalid</c> with one
/// <see cref="ErrorDetail"/> per failure — BEFORE the tenant transaction opens, so
/// an invalid command never borrows a connection. JSON Schema has already run at
/// the edge (C2 note); this is the command-shape validation. A non-Result message
/// with a failing validator cannot express the failure, so
/// <see cref="ResultFactory"/> throws — an application error shape the envelope
/// middleware surfaces as an internal error, not a silent pass.
/// </summary>
public sealed class ValidationBehaviour<TMessage, TResponse>(IEnumerable<IValidator<TMessage>> validators)
    : IPipelineBehavior<TMessage, TResponse>
    where TMessage : IMessage
{
    /// <inheritdoc />
    public async ValueTask<TResponse> Handle(
        TMessage message,
        MessageHandlerDelegate<TMessage, TResponse> next,
        CancellationToken cancellationToken)
    {
        List<ValidationFailure>? failures = null;

        foreach (var validator in validators)
        {
            var outcome = await validator.ValidateAsync(message, cancellationToken).ConfigureAwait(false);
            if (!outcome.IsValid)
            {
                (failures ??= []).AddRange(outcome.Errors);
            }
        }

        if (failures is null)
        {
            return await next(message, cancellationToken).ConfigureAwait(false);
        }

        return ResultFactory.Failure<TResponse>(Error.Invalid(
            "the request is invalid",
            failures.Select(Detail).ToList()));
    }

    private static ErrorDetail Detail(ValidationFailure failure) =>
        new(
            failure.PropertyName.Length == 0 ? "$" : failure.PropertyName,
            failure.ErrorCode);
}
