using System.Text.Json;
using Evidgate.BuildingBlocks.Contracts.Validation;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Ingestion.Application.Commands;
using Evidgate.Modules.Ingestion.Domain;

namespace Evidgate.Modules.Ingestion.Application;

/// <summary>
/// The first-send capability seam as one injectable step (04.1.1-T1, D11): the core of
/// <see cref="RegisterFirstSendCapabilityHandler"/> lifted into a scoped service so the events handler
/// runs the very same validate → negotiate → store path on an envelope it has <b>already parsed</b>.
/// The batch handler owns a ≤1 MiB body and up to 500 events, so it must not parse the body a second
/// time to look for the carrier; the raw-bytes command keeps its own cap and JSON parse and then calls
/// this service with the root element.
/// <para>
/// An envelope that carries no capability registers nothing (a success with
/// <see cref="FirstSendCapabilityOutcome.Registered"/> false); a malformed carrier is 422
/// <c>capability_invalid</c> with the path of the offending member; a carrier takes the shared
/// validate → negotiate → store path and any refusal (422 <c>capability_invalid</c>/<c>unsupported_protocol_major</c>,
/// 409 <c>capability_conflict</c>) is returned unchanged so the caller can roll its transaction back.
/// </para>
/// </summary>
internal sealed class FirstSendCapabilityService(AdapterCapabilityRegistrar registrar)
{
    /// <summary>Registers the capability of one already-parsed first envelope, or reports why it was refused.</summary>
    /// <param name="envelope">The parsed first envelope of the run (00 §9.4).</param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    internal async ValueTask<Result<FirstSendCapabilityOutcome>> RegisterAsync(
        JsonElement envelope,
        CancellationToken cancellationToken)
    {
        var extraction = FirstSendCapabilityExtractor.Extract(envelope);

        if (!extraction.IsCarrier)
        {
            return extraction.Errors.Count == 0
                ? Result.Success(new FirstSendCapabilityOutcome(false, null))
                : Result.Failure<FirstSendCapabilityOutcome>(CapabilityBody.Invalid(
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
