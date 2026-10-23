using System.Text.Json;
using System.Text.Json.Serialization;
using Evidgate.BuildingBlocks.Contracts.Validation;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Ingestion.Contracts;
using Evidgate.Modules.Ingestion.Domain;
using Mediator;

namespace Evidgate.Modules.Ingestion.Application.Commands;

/// <summary>Register one adapter capability document (04.7.1-T2.2, task file §5).</summary>
/// <param name="AdapterName">The adapter identity: the natural key's <c>name</c> (the <c>ext.schema</c> prefix on the first-send path).</param>
/// <param name="Document">The raw UTF-8 capability document (≤ 16 KiB), parsed and schema-checked by the handler after authorization.</param>
internal sealed record RegisterAdapterCapability(
    string AdapterName,
    [property: JsonIgnore] ReadOnlyMemory<byte> Document)
    : ICommand<Result<AdapterCapabilityRegistered>>;

/// <summary>
/// Handler of <see cref="RegisterAdapterCapability"/> (04.7.1-T2.2, task file §5 and §8 failure path): the
/// capability body is validated against <c>capability.v1</c> (422 <c>capability_invalid</c> with JSON-pointer
/// details), bound, negotiated (422 <c>unsupported_protocol_major</c>, <c>retryable: false</c>) and stored in
/// one upsert. A rejection writes nothing and increments <c>evidgate_adapter_capability_rejected_total</c>;
/// no document content reaches a log line or a metric label.
/// </summary>
[AuditedAction("adapter_capability.register")]
[Permission(Permissions.Event.Write)]
internal sealed class RegisterAdapterCapabilityHandler(AdapterCapabilityRegistrar registrar)
    : ICommandHandler<RegisterAdapterCapability, Result<AdapterCapabilityRegistered>>
{
    /// <inheritdoc />
    public ValueTask<Result<AdapterCapabilityRegistered>> Handle(
        RegisterAdapterCapability command,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Ingestion, nameof(RegisterAdapterCapability));

        return registrar.RegisterAsync(command.AdapterName, command.Document, cancellationToken);
    }
}

/// <summary>
/// The registration path shared by <see cref="RegisterAdapterCapability"/> and the first-send seam
/// (04.7.1-T2.3): validate → bind → negotiate → store, with the rejection telemetry and the audit subject in
/// one place, so the two entries cannot drift. It is a service, not a nested Mediator command, so the
/// first-send handler stays inside one pipeline unit of work.
/// </summary>
internal sealed class AdapterCapabilityRegistrar(
    Lazy<ContractSchemaSet> schemas,
    IProtocolNegotiator negotiator,
    AdapterCapabilityStore store,
    IAuditTrail audit)
{
    /// <summary>Registers a raw capability document; the byte cap and the parse happen here.</summary>
    /// <param name="adapterName">The adapter identity: the natural key's <c>name</c>.</param>
    /// <param name="document">The raw UTF-8 capability document.</param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    internal async ValueTask<Result<AdapterCapabilityRegistered>> RegisterAsync(
        string adapterName,
        ReadOnlyMemory<byte> document,
        CancellationToken cancellationToken)
    {
        var error = CapabilityBody.Validate(document, schemas.Value, out var parsed);
        if (error is not null)
        {
            EvidgateTelemetry.RecordAdapterCapabilityRejected(error.Code, null);

            return Result.Failure<AdapterCapabilityRegistered>(error);
        }

        using (parsed)
        {
            // The bytes were validated above; do not run the schema a second time on the parsed root.
            return await RegisterValidatedAsync(adapterName, parsed!.RootElement, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Registers an already-parsed capability document (the first-send path).</summary>
    /// <param name="adapterName">The adapter identity: the natural key's <c>name</c>.</param>
    /// <param name="document">The <c>capability.v1</c> document root.</param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    /// <param name="pathPrefix">The JSON pointer of <paramref name="document"/> inside the request body; empty when it is the body.</param>
    internal async ValueTask<Result<AdapterCapabilityRegistered>> RegisterAsync(
        string adapterName,
        JsonElement document,
        CancellationToken cancellationToken,
        string pathPrefix = "")
    {
        if (CapabilityBody.Validate(document, schemas.Value, pathPrefix) is { } invalid)
        {
            EvidgateTelemetry.RecordAdapterCapabilityRejected(invalid.Code, null);

            return Result.Failure<AdapterCapabilityRegistered>(invalid);
        }

        return await RegisterValidatedAsync(adapterName, document, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Checks the adapter name, then binds, negotiates and stores a document that already passed
    /// <c>capability.v1</c>.
    /// </summary>
    /// <param name="adapterName">The adapter identity: the natural key's <c>name</c>.</param>
    /// <param name="document">The validated <c>capability.v1</c> document root.</param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    private async ValueTask<Result<AdapterCapabilityRegistered>> RegisterValidatedAsync(
        string adapterName,
        JsonElement document,
        CancellationToken cancellationToken)
    {
        // The name is the natural key's `name` and the column CHECK's subject (migration 0026); refuse it
        // here, before any statement, so a bad name is a 422 capability_invalid and never a 23514 that
        // aborts the tenant transaction. The first-send path already enforced the same grammar in
        // FirstSendCapabilityExtractor, so this is a no-op there and the one guard for the direct command.
        if (!AdapterName.IsValid(adapterName))
        {
            var invalid = CapabilityBody.Invalid(
                "the adapter name does not match the adapter-name grammar",
                [new ErrorDetail("/name", "invalid_format")]);

            EvidgateTelemetry.RecordAdapterCapabilityRejected(invalid.Code, null);

            return Result.Failure<AdapterCapabilityRegistered>(invalid);
        }

        var binding = CapabilityBinder.Bind(document);
        var negotiation = negotiator.Negotiate(binding.Document, ServerProtocol.Current);
        if (!negotiation.Accepted)
        {
            EvidgateTelemetry.RecordAdapterCapabilityRejected(
                negotiation.RejectionCode!,
                binding.Document.Protocol.Major);

            return Result.Failure<AdapterCapabilityRegistered>(Error.Unprocessable(
                negotiation.RejectionCode!,
                "the adapter's protocol major is not one this server supports",
                [.. negotiation.Details.Select(detail => new ErrorDetail(detail.Path, detail.Reason))]));
        }

        var registered = await store.RegisterAsync(adapterName, binding, cancellationToken).ConfigureAwait(false);
        if (!registered.IsSuccess)
        {
            EvidgateTelemetry.RecordAdapterCapabilityRejected(
                registered.Error!.Code,
                binding.Document.Protocol.Major);

            return registered;
        }

        // The audit row names the object, never its content: the allowlist carries no adapter name, major, minor
        // or mode key, so the entry stays a bare object reference (the EnvironmentRevision precedent).
        audit.Describe(new AuditObject("adapter_capability", registered.Value.Id.ToString("D")));

        return registered;
    }
}
