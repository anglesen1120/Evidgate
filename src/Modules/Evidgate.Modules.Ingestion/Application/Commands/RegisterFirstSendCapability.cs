using System.Text.Json;
using System.Text.Json.Serialization;
using Evidgate.BuildingBlocks.Contracts.Validation;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Ingestion.Domain;
using Mediator;

namespace Evidgate.Modules.Ingestion.Application.Commands;

/// <summary>
/// The 04.7.1-T2.3 first-send seam: register the capability a producer sends as the first envelope of a run.
/// The future events handler (04.1.1-T1) calls this before it accepts the first event; the envelope is the
/// 00 §9.4 form the reporter sends (<c>kind</c> <c>run.log</c>, <c>sequence</c> 1, <c>ext.schema</c> and
/// <c>ext.capability</c>).
/// </summary>
/// <param name="FirstEnvelope">The raw UTF-8 first envelope (≤ the <c>event-envelope.v1</c> 1 MiB transport cap).</param>
internal sealed record RegisterFirstSendCapability([property: JsonIgnore] ReadOnlyMemory<byte> FirstEnvelope)
    : ICommand<Result<FirstSendCapabilityOutcome>>;

/// <summary>What the first-send seam did: whether a capability was registered, and its row id.</summary>
/// <param name="Registered">True when the envelope carried a capability and it is now on file.</param>
/// <param name="CapabilityId">The capability row's id; null when the envelope carried none.</param>
internal sealed record FirstSendCapabilityOutcome(bool Registered, Guid? CapabilityId);

/// <summary>
/// Handler of <see cref="RegisterFirstSendCapability"/> (04.7.1-T2.3): an envelope that is not a capability
/// carrier registers nothing and appends no audit row; a malformed carrier is 422 <c>capability_invalid</c>
/// with the path under <c>/ext/...</c>; a carrier takes the same validate → negotiate → store path as
/// <see cref="RegisterAdapterCapability"/>.
/// </summary>
[AuditedAction("adapter_capability.register_first_send")]
[Permission(Permissions.Event.Write)]
internal sealed class RegisterFirstSendCapabilityHandler(
    Lazy<ContractSchemaSet> schemas,
    AdapterCapabilityRegistrar registrar,
    IAuditTrail audit)
    : ICommandHandler<RegisterFirstSendCapability, Result<FirstSendCapabilityOutcome>>
{
    /// <inheritdoc />
    public async ValueTask<Result<FirstSendCapabilityOutcome>> Handle(
        RegisterFirstSendCapability command,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Ingestion, nameof(RegisterFirstSendCapability));

        // The envelope is capped by event-envelope.v1's x-max-bytes (1 MiB,
        // contracts/schemas/event-envelope.v1.json), so the seam never parses an unbounded body.
        if (schemas.Value.MaxBytes(ContractSchemaKeys.EventEnvelope) is { } maxBytes
            && command.FirstEnvelope.Length > maxBytes)
        {
            return Reject(CapabilityBody.Invalid(
                "the first envelope exceeds the event-envelope transport cap",
                [new ErrorDetail(string.Empty, ContractValidationError.BodyTooLargeReason)]));
        }

        JsonDocument envelope;
        try
        {
            envelope = JsonDocument.Parse(command.FirstEnvelope);
        }
        catch (JsonException)
        {
            return Reject(CapabilityBody.Invalid(
                "the first envelope is not JSON",
                [new ErrorDetail(string.Empty, ContractValidationError.InvalidJsonReason)]));
        }

        using (envelope)
        {
            var extraction = FirstSendCapabilityExtractor.Extract(envelope.RootElement);

            if (!extraction.IsCarrier)
            {
                if (extraction.Errors.Count == 0)
                {
                    // Not a capability carrier: nothing was registered, so no audit row is appended.
                    audit.Suppress();

                    return Result.Success(new FirstSendCapabilityOutcome(false, null));
                }

                return Reject(CapabilityBody.Invalid(
                    "the first envelope's capability carrier is malformed",
                    extraction.Errors));
            }

            var registered = await registrar
                .RegisterAsync(extraction.AdapterName!, extraction.Document, cancellationToken, "/ext/capability")
                .ConfigureAwait(false);

            return registered.IsSuccess
                ? Result.Success(new FirstSendCapabilityOutcome(true, registered.Value.Id))
                : Result.Failure<FirstSendCapabilityOutcome>(registered.Error!);
        }
    }

    private static Result<FirstSendCapabilityOutcome> Reject(Error error)
    {
        EvidgateTelemetry.RecordAdapterCapabilityRejected(error.Code, null);

        return Result.Failure<FirstSendCapabilityOutcome>(error);
    }
}
