using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Evidgate.BuildingBlocks.Canonicalization;
using Evidgate.BuildingBlocks.Contracts.Validation;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Messaging;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Healing.Contracts;
using Mediator;

namespace Evidgate.Modules.Healing.Application;

/// <summary>
/// Set the healing kill switch of one scope (<c>PUT /v1/healing/switch</c>, 08.6.3 §3/§5, rulings T33/T72). The
/// platform-wide <c>system</c> scope is never reachable from here (403 <c>forbidden_scope</c>): the operator ingress
/// owns it, and a tenant route that could write it would let one org switch healing off for every other org.
/// </summary>
/// <param name="Body">The raw request body, read under the route's 4 KiB cap and parsed in the handler.</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c> (00 §9.1).</param>
internal sealed record SetHealingSwitchCommand(
    [property: JsonIgnore] ReadOnlyMemory<byte> Body,
    string IdempotencyKey)
    : ICommand<Result<HealingSwitchView>>, IIdempotentCommand
{
    /// <summary>The 00 §7 operation the key is claimed for: stable across a rename.</summary>
    string IIdempotentCommand.Operation => "healing:switch:set";

    /// <summary>
    /// The request identity the idempotency store hashes, computed from <see cref="Body"/> on first serialization:
    /// the RFC 8785 digest of the body, so member order and whitespace differences replay rather than conflict. A body
    /// that cannot be canonicalized falls back to <c>raw-sha256:&lt;hex&gt;</c>, so it still reaches the handler, which
    /// answers its own 422 (a failure Result is never stored).
    /// </summary>
    public string BodyHash => ComputeBodyHash(Body);

    private static string ComputeBodyHash(ReadOnlyMemory<byte> body)
    {
        try
        {
            return CanonicalHash.Compute(body.Span);
        }
        catch (JsonCanonicalizationException)
        {
            return RawSha256(body);
        }
        catch (JsonException)
        {
            return RawSha256(body);
        }
    }

    private static string RawSha256(ReadOnlyMemory<byte> body) =>
        "raw-sha256:" + Convert.ToHexStringLower(SHA256.HashData(body.Span));
}

/// <summary>
/// Handler of <see cref="SetHealingSwitchCommand"/> (08.6.3 AC1/AC2/AC7): the caller must be a member holding
/// <c>healing:configure</c>, the body must match <c>healing-switch-set.v1</c>, <c>system</c> is refused before any
/// lookup, a <c>tenant</c> body may not name a project, a <c>project</c> body names an in-scope live project (404
/// otherwise, like the waiver create), the mode is one of the three storable values and <c>enabled</c> needs
/// <c>Healing:Apply</c>. The row is upserted in the caller's tenant transaction with its outbox event and audit row,
/// so a refusal writes nothing.
/// <para>
/// An idempotent retry replays the stored response verbatim (C2 stage 5) and never a second outbox row: the event's
/// dedupe key carries the row version, the shape <c>identity.invite.notify</c> uses for repeated mints.
/// </para>
/// </summary>
[AuditedAction("healing.switch.set")]
[Permission(Permissions.Healing.Configure)]
internal sealed class SetHealingSwitchCommandHandler(
    HealingSwitchStore store,
    IHealingApplyPolicy apply,
    IOutboxWriter outbox,
    Lazy<ContractSchemaSet> schemas,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    IAuditTrail audit)
    : ICommandHandler<SetHealingSwitchCommand, Result<HealingSwitchView>>
{
    /// <summary>Runs the write.</summary>
    /// <param name="command">The command.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async ValueTask<Result<HealingSwitchView>> Handle(SetHealingSwitchCommand command, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Healing, nameof(SetHealingSwitchCommand));

        if (principals.Current?.UserId is not { } setBy)
        {
            return Result.Failure<HealingSwitchView>(Error.Forbidden("healing:configure is held by a member"));
        }

        if (Parse(command.Body, schemas.Value) is var parsed && parsed.Error is not null)
        {
            return Result.Failure<HealingSwitchView>(parsed.Error);
        }

        var request = parsed.Request!;

        // AC1: the platform row belongs to the operator ingress, and the refusal comes before any lookup so a tenant
        // cannot probe whether a project id exists by sending a system write.
        if (request.Scope == HealingSwitchScopes.System)
        {
            return Result.Failure<HealingSwitchView>(Error.Denied(
                ApiErrorCode.ForbiddenScope,
                "the platform-wide switch is set through the operator ingress, not a tenant route"));
        }

        if (request.Scope == HealingSwitchScopes.Tenant && request.ProjectId is not null)
        {
            return Result.Failure<HealingSwitchView>(Error.Invalid(
                "a tenant switch carries no project_id",
                [new ErrorDetail("project_id", "not_allowed")]));
        }

        Guid? projectId = null;
        if (request.Scope == HealingSwitchScopes.Project)
        {
            if (request.ProjectId is not { } named)
            {
                return Result.Failure<HealingSwitchView>(Error.Invalid(
                    "a project switch needs its project_id",
                    [new ErrorDetail("project_id", "required")]));
            }

            if (HealingScopeGuard.Project(named, principals, tenant) is { } mismatch)
            {
                return Result.Failure<HealingSwitchView>(mismatch);
            }

            if (!await store.ProjectExistsAsync(named, forShare: true, cancellationToken).ConfigureAwait(false))
            {
                return Result.Failure<HealingSwitchView>(Error.NotFound("no live project with that id is visible to this tenant"));
            }

            projectId = named;
        }

        if (HealingModeGate.Check(request.Mode, request.Reason, apply.IsApplyEnabled) is { } invalid)
        {
            return Result.Failure<HealingSwitchView>(invalid);
        }

        var row = await store.UpsertAsync(request.Scope!, projectId, request.Mode!, request.Reason, setBy, cancellationToken)
            .ConfigureAwait(false);

        await HealingSwitchEvents.EnqueueAsync(outbox, row, cancellationToken).ConfigureAwait(false);

        // 02.7.1-T2.2's allowlist is closed and `scope`/`project_id`/`version` are not on it: the row id names the
        // switch row, `mode` is the change itself, and the scope and project travel in the `healing.switch_changed`
        // payload of the same transaction (HealingSwitchEvents) rather than as an unvetted metadata key.
        audit.Describe(new AuditObject(
            "healing_switch",
            row.Id.ToString("D"),
            Metadata: new Dictionary<string, string>
            {
                ["mode"] = row.Mode,
            }));

        return Result.Success(HealingSwitchView.Of(row));
    }

    /// <summary>
    /// Validates the body against <c>healing-switch-set.v1</c> (the closed schema of the route) and reads its four
    /// members. Schema failures answer 422 <c>request_invalid</c> with the mapped pointer rows; the mode vocabulary is
    /// <see cref="HealingModeGate"/>'s job, not the schema's, so <c>unavailable</c> answers its own code.
    /// </summary>
    private static (SetHealingSwitchRequest? Request, Error? Error) Parse(ReadOnlyMemory<byte> body, ContractSchemaSet schemas)
    {
        var validation = schemas.Validate(ContractSchemaKeys.HealingSwitchSet, body);
        if (!validation.IsValid)
        {
            return (null, Error.Invalid(
                "the request body does not match healing-switch-set.v1",
                [.. validation.Errors.Select(error => new ErrorDetail(error.Path, error.Reason, error.Index))]));
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        string? scope = root.TryGetProperty("scope", out var scopeProperty) ? scopeProperty.GetString() : null;
        string mode = root.GetProperty("mode").GetString()!;
        string? reason = root.TryGetProperty("reason", out var reasonProperty) && reasonProperty.ValueKind == JsonValueKind.String
            ? reasonProperty.GetString()
            : null;
        Guid? projectId = root.TryGetProperty("project_id", out var projectProperty)
            && projectProperty.ValueKind == JsonValueKind.String
            && Guid.TryParse(projectProperty.GetString(), out var parsedProject)
                ? parsedProject
                : null;

        return (new SetHealingSwitchRequest(scope, projectId, mode, reason), null);
    }
}

/// <summary>The scope vocabulary of a switch row (migration 0052's <c>healing_switch_scope_check</c>).</summary>
internal static class HealingSwitchScopes
{
    /// <summary>The platform-wide row: operator ingress only.</summary>
    internal const string System = "system";

    /// <summary>The org-level override.</summary>
    internal const string Tenant = "tenant";

    /// <summary>The project override.</summary>
    internal const string Project = "project";
}

/// <summary>One validated switch request.</summary>
/// <param name="Scope">The scope of the row.</param>
/// <param name="ProjectId">The project of a <c>project</c> row.</param>
/// <param name="Mode">The requested mode token, not yet gated.</param>
/// <param name="Reason">The reason, or null.</param>
internal sealed record SetHealingSwitchRequest(string? Scope, Guid? ProjectId, string? Mode, string? Reason);

/// <summary>
/// The outbox event of a switch change (08.6.3 §3): <c>healing.switch_changed</c>, one row per stored version, so a
/// retry of the same <c>Idempotency-Key</c> writes none and a later change writes its own. The payload carries ids and
/// the mode only — never a reason, which may name a customer (00 §5.7 S3).
/// </summary>
internal static class HealingSwitchEvents
{
    /// <summary>The dispatcher's handler key; no handler registers it yet (08.6.3 §7).</summary>
    internal const string TaskType = "healing.switch_changed";

    /// <summary>The <c>aggregateType</c> of the row: the switch row itself.</summary>
    internal const string AggregateType = "healing_switch";

    /// <summary>Enqueues the change event of one stored version.</summary>
    /// <param name="outbox">The outbox writer of the caller's tenant transaction.</param>
    /// <param name="row">The row as stored.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal static Task<Guid> EnqueueAsync(IOutboxWriter outbox, HealingSwitchRow row, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outbox);
        ArgumentNullException.ThrowIfNull(row);

        return outbox.EnqueueAsync(
            TaskType,
            Payload(row),
            AggregateType,
            row.Id,
            IdempotencyKey(row),
            cancellationToken);
    }

    /// <summary>The dedupe key of one stored version: <c>healing:switch:&lt;scope&gt;:&lt;key&gt;:v&lt;version&gt;</c>.</summary>
    /// <param name="row">The row as stored.</param>
    internal static string IdempotencyKey(HealingSwitchRow row)
    {
        var key = row.ProjectId?.ToString("D") ?? row.Scope;
        return $"healing:switch:{row.Scope}:{key}:v{row.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
    }

    private static string Payload(HealingSwitchRow row) => JsonSerializer.Serialize(new
    {
        scope = row.Scope,
        project_id = row.ProjectId,
        mode = row.Mode,
        version = row.Version,
    });
}
