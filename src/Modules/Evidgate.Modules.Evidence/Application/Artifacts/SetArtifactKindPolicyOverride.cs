using System.Text.Json;
using Evidgate.BuildingBlocks.ObjectStorage;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Evidence.Domain;
using Evidgate.Modules.Evidence.Endpoints;
using FluentValidation;
using Mediator;

namespace Evidgate.Modules.Evidence.Application.Artifacts;

/// <summary>
/// The per-project override of one artifact kind's capture policy: the maximum size, the accepted media types, the
/// default sensitivity, the capture switch and the secret-rejection switch, upserted on
/// <c>(org, project, kind)</c>. It is the only route that can lower a kind whose static default is sensitive, which
/// is why every write is audited with the previous sensitivity: a lowering can never happen by omission, and the
/// chain shows who made it.
/// </summary>
/// <param name="ProjectId">The project whose policy is overridden.</param>
/// <param name="Kind">One of the eight contract kinds; an unknown one is refused 422.</param>
/// <param name="MaxBytes">The largest artifact of the kind the project accepts, greater than zero.</param>
/// <param name="MediaTypes">The accepted media types, between one and 64.</param>
/// <param name="DefaultSensitivity"><c>normal</c> or <c>sensitive</c>.</param>
/// <param name="CaptureEnabled">False switches capture of the kind off for the project.</param>
/// <param name="RejectOnSecret">True makes the project reject an artifact whose scan flags a secret.</param>
internal sealed record SetArtifactKindPolicyOverride(
    Guid ProjectId,
    string Kind,
    long MaxBytes,
    string[] MediaTypes,
    string DefaultSensitivity,
    bool CaptureEnabled,
    bool RejectOnSecret)
    : ICommand<Result<ArtifactKindPolicyView>>;

/// <summary>
/// Shape validation of <see cref="SetArtifactKindPolicyOverride"/> before the tenant transaction: the sensitivity is
/// one of the column's two values, the media-type list is within the column's bounds and the size is positive and no
/// larger than one stored object can be read. Unknown kinds are the handler's named 422 <c>kind_unknown</c>, not a
/// validator reason, because the code has to survive a mature client.
/// </summary>
internal sealed class SetArtifactKindPolicyOverrideValidator : AbstractValidator<SetArtifactKindPolicyOverride>
{
    /// <summary>Builds the rules.</summary>
    public SetArtifactKindPolicyOverrideValidator()
    {
        RuleFor(command => command.MaxBytes)
            .GreaterThan(0)
            .WithErrorCode("out_of_range");

        // A cap above the storage ceiling is a row no scan or parse could ever read: the object read refuses it, so
        // the write is refused here instead of storing a policy that can only fail later.
        RuleFor(command => command.MaxBytes)
            .LessThanOrEqualTo(ObjectStorageLimits.MaxObjectBytes)
            .WithErrorCode("out_of_range");

        RuleFor(command => command.MediaTypes)
            .NotNull()
            .Must(types => types is { Length: >= 1 and <= 64 })
            .WithErrorCode("out_of_range");

        RuleFor(command => command.MediaTypes)
            .Must(types => types is null || types.All(type => !string.IsNullOrWhiteSpace(type) && type.Length <= 255))
            .WithErrorCode("invalid_format");

        RuleFor(command => command.DefaultSensitivity)
            .Must(sensitivity => sensitivity is ArtifactSensitivity.Normal or ArtifactSensitivity.Sensitive)
            .WithErrorCode("invalid_format");
    }
}

/// <summary>
/// Upserts the project's row for one kind. The writes of one kind serialize on an advisory lock taken before the
/// previous value is read, so the audit records the value the write it queued behind actually left, an absent row
/// included. The upsert follows on the row's unique key, and the answer carries the stored policy. <c>version</c>
/// and <c>updated_at</c> are the trigger's, never the caller's.
/// </summary>
[AuditedAction("artifact_kind_policy.set")]
[Permission(Permissions.Project.Write)]
internal sealed class SetArtifactKindPolicyOverrideHandler(
    ITenantDb db,
    IRequestTenantContextAccessor tenant,
    IRequestPrincipalAccessor principals,
    IAuditTrail audit)
    : ICommandHandler<SetArtifactKindPolicyOverride, Result<ArtifactKindPolicyView>>
{
    /// <summary>The stored row's previous default sensitivity, or null when the project had no row for the kind.</summary>
    private const string PreviousSql = """
        select default_sensitivity
          from public.artifact_kind_policy
         where org_id = @org_id
           and project_id = @project_id
           and kind = @kind
        """;

    /// <summary>
    /// Serialises the writes of one (org, project, kind) on the logical key, so an absent row is covered too: the
    /// lock is the transaction's, and the previous read and the upsert both run under it.
    /// </summary>
    private const string LockSql =
        "select pg_advisory_xact_lock(hashtext('evidgate.artifact_kind_policy'), hashtext(@lock_key))";

    /// <summary>The project's status, so a missing or deleting project is a named refusal and not a foreign-key error.</summary>
    private const string ProjectStatusSql = """
        select status
          from public.project
         where org_id = @org_id
           and id = @project_id
        """;

    /// <summary>The upsert on the row's unique key; the triggers own <c>version</c>/<c>updated_at</c>.</summary>
    /// <remarks>
    /// The tenant Dapper path refuses array parameters (<c>StringArrayTypeHandler</c>), so <c>media_types</c> travels
    /// as JSON text and is rebuilt in SQL, keeping the submitted order (<c>AdapterCapabilityStore</c> precedent).
    /// </remarks>
    private const string UpsertSql = """
        insert into public.artifact_kind_policy
            (id, org_id, project_id, kind, max_bytes, media_types, default_sensitivity, reject_on_secret, capture_enabled)
        values
            (@id, @org_id, @project_id, @kind, @max_bytes,
             array(select m from jsonb_array_elements_text(@media_types::jsonb) with ordinality as t(m, n) order by n),
             @default_sensitivity, @reject_on_secret, @capture_enabled)
        on conflict (org_id, project_id, kind) do update
           set max_bytes = excluded.max_bytes,
               media_types = excluded.media_types,
               default_sensitivity = excluded.default_sensitivity,
               reject_on_secret = excluded.reject_on_secret,
               capture_enabled = excluded.capture_enabled
        """;

    /// <summary>The <c>project.status</c> that refuses a policy write.</summary>
    private const string ProjectDeleted = "deleted";

    /// <inheritdoc />
    public async ValueTask<Result<ArtifactKindPolicyView>> Handle(
        SetArtifactKindPolicyOverride command,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Evidence, nameof(SetArtifactKindPolicyOverride));

        // The route names its project, so a request narrowed to another project is refused before the kind or the
        // project row is looked at — the same rule the retention pair applies. `project:write` is outside the service
        // ceiling, so no project-bound credential reaches this handler today; both members stay checked.
        var context = tenant.Require();
        if (EvidenceProjectScope.Project(command.ProjectId, principals.Current?.ProjectScope, context.ProjectId) is { } mismatch)
        {
            return Result.Failure<ArtifactKindPolicyView>(mismatch);
        }

        if (!ArtifactKind.IsKnown(command.Kind))
        {
            return Result.Failure<ArtifactKindPolicyView>(EvidenceErrors.KindUnknown);
        }

        var parameters = new
        {
            id = Guid.CreateVersion7(),
            org_id = context.OrgId,
            project_id = command.ProjectId,
            kind = command.Kind,
            // The row's natural key: the writers of one kind queue behind each other on it, absent row included.
            lock_key = $"{context.OrgId:D}:{command.ProjectId:D}:{command.Kind}",
            max_bytes = command.MaxBytes,
            // A list, so it travels as JSON text: the tenant Dapper path refuses array parameters.
            media_types = JsonSerializer.Serialize(command.MediaTypes),
            default_sensitivity = command.DefaultSensitivity,
            reject_on_secret = command.RejectOnSecret,
            capture_enabled = command.CaptureEnabled,
        };

        var status = await db.QuerySingleOrDefaultAsync<string>(ProjectStatusSql, parameters, cancellationToken)
            .ConfigureAwait(false);
        if (status is null)
        {
            return Result.Failure<ArtifactKindPolicyView>(EvidenceErrors.ProjectNotFound);
        }

        if (string.Equals(status, ProjectDeleted, StringComparison.Ordinal))
        {
            return Result.Failure<ArtifactKindPolicyView>(EvidenceErrors.ProjectDeleting);
        }

        // Taken before the previous read: two writers of one kind must not both read the value the other is about to
        // replace, which is how the audit chain would record a "previous" that never was the row's value.
        await db.ExecuteAsync(LockSql, parameters, cancellationToken).ConfigureAwait(false);

        var previous = await db.QuerySingleOrDefaultAsync<string>(PreviousSql, parameters, cancellationToken)
            .ConfigureAwait(false);

        await db.ExecuteAsync(UpsertSql, parameters, cancellationToken).ConfigureAwait(false);

        // The chain records which sensitivity the row carried before and which it carries now: a lowering of a
        // kind whose static default is sensitive is exactly the change an operator has to be able to find. A project
        // without a row was running on the kind's default, so that value is what the write replaces.
        var before = previous
            ?? ArtifactKindDefaults.Resolve(command.Kind, row: null)?.DefaultSensitivity
            ?? ArtifactSensitivity.ForKind(command.Kind);

        audit.Describe(new AuditObject(
            "artifact_kind_policy",
            $"{command.ProjectId:D}:{command.Kind}",
            Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["previous_default_sensitivity"] = before,
                ["status"] = command.DefaultSensitivity,
            }));

        return Result.Success(new ArtifactKindPolicyView(
            command.Kind,
            command.MaxBytes,
            command.MediaTypes,
            command.DefaultSensitivity,
            command.CaptureEnabled,
            command.RejectOnSecret));
    }
}
