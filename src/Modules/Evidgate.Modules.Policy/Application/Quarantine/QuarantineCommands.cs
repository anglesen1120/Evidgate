using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Messaging;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Policy.Domain;
using Mediator;
using Npgsql;

namespace Evidgate.Modules.Policy.Application.Quarantine;

/// <summary>Quarantine a case (<c>POST /v1/quarantine</c>, 07.3.1 §5).</summary>
/// <param name="ProjectId">The project the entry belongs to.</param>
/// <param name="CaseId">The catalog case the entry excludes from gating.</param>
/// <param name="Owner">The member accountable for the quarantine.</param>
/// <param name="Reason">Justification, 1–2000 Unicode code points, no NUL or lone surrogate.</param>
/// <param name="ExpiresAt">A date-time strictly in the future.</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c> (00 §9.1).</param>
internal sealed record CreateQuarantineEntryCommand(
    Guid ProjectId,
    Guid CaseId,
    Guid Owner,
    string? Reason,
    DateTimeOffset? ExpiresAt,
    string IdempotencyKey)
    : ICommand<Result<QuarantineEntry>>, IIdempotentCommand;

/// <summary>
/// Handler of <see cref="CreateQuarantineEntryCommand"/>: the caller must be a member, the project must be inside
/// the credential's scope and live, the case must exist in that project, the owner must be a membership of the org,
/// the body must be valid, and the case must not be on the active policy revision's critical list (T23). The
/// critical-list read and the insert share one tenant transaction, so the refusal returns before any write (AC4);
/// the row is inserted in state <c>active</c> (AC5).
/// </summary>
[AuditedAction("quarantine.create")]
[Permission(Permissions.Quarantine.Write)]
internal sealed class CreateQuarantineEntryCommandHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    TimeProvider clock,
    IAuditTrail audit)
    : ICommandHandler<CreateQuarantineEntryCommand, Result<QuarantineEntry>>
{
    /// <inheritdoc />
    public async ValueTask<Result<QuarantineEntry>> Handle(CreateQuarantineEntryCommand command, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Policy, nameof(CreateQuarantineEntryCommand));

        if (principals.Current?.UserId is null)
        {
            return Result.Failure<QuarantineEntry>(Error.Forbidden("only a member may quarantine a case"));
        }

        if (QuarantineScopeGuard.Project(command.ProjectId, principals, tenant) is { } mismatch)
        {
            return Result.Failure<QuarantineEntry>(mismatch);
        }

        var orgId = scopes.Require().OrgId;

        // The project row is locked FOR SHARE here (D4), before the critical-list read below: a concurrent
        // 07.1.1-T4 activation takes the same project row FOR NO KEY UPDATE (PolicyRevisionStore.AppendAsync), so it
        // must wait for this transaction to commit — no TOCTOU window between the check and the insert. The lock is
        // on public.project, not policy_revision: evidgate_app holds table-level UPDATE on project (0005) but only
        // SELECT, INSERT on policy_revision (0025), and a row lock needs UPDATE (a FOR SHARE there is denied 42501).
        // A missing or soft-deleted project reads no row, so the create still answers 404.
        var live = await db.ScalarAsync<int?>(QuarantineSql.LockProjectForShare, new { project_id = command.ProjectId }, cancellationToken).ConfigureAwait(false);
        if (live is null)
        {
            return Result.Failure<QuarantineEntry>(QuarantineErrors.ProjectNotFound);
        }

        if (Validate(command, clock, out var expiresAt) is { } invalid)
        {
            return Result.Failure<QuarantineEntry>(invalid);
        }

        var caseExists = await db.ScalarAsync<int?>(
            QuarantineSql.CaseExists,
            new { org_id = orgId, project_id = command.ProjectId, case_id = command.CaseId },
            cancellationToken).ConfigureAwait(false);
        if (caseExists is null)
        {
            return Result.Failure<QuarantineEntry>(QuarantineErrors.CaseNotFound);
        }

        var ownerExists = await db.ScalarAsync<int?>(
            QuarantineSql.OwnerExists,
            new { org_id = orgId, owner = command.Owner },
            cancellationToken).ConfigureAwait(false);
        if (ownerExists is null)
        {
            return Result.Failure<QuarantineEntry>(QuarantineErrors.UnknownOwner);
        }

        // T23 (D4): the critical-list check and the insert share this tenant transaction, and the project row is
        // already locked FOR SHARE above; the refusal returns before any INSERT, so no row is written (AC4). A
        // concurrent 07.1.1-T4 activation that retires the active revision must wait for this transaction to commit.
        var criticalCases = await db.ScalarAsync<string?>(
            QuarantineCriticalCases.ActiveRevisionCriticalCases,
            new { org_id = orgId, project_id = command.ProjectId },
            cancellationToken).ConfigureAwait(false);
        if (QuarantineCriticalCases.Contains(criticalCases, command.CaseId))
        {
            return Result.Failure<QuarantineEntry>(QuarantineErrors.CriticalCaseNotQuarantinable);
        }

        var id = Guid.CreateVersion7();
        QuarantineEntry row;
        try
        {
            row = await db.QuerySingleOrDefaultAsync<QuarantineEntry>(
                QuarantineSql.Insert,
                new
                {
                    id,
                    org_id = orgId,
                    project_id = command.ProjectId,
                    case_id = command.CaseId,
                    owner = command.Owner,
                    reason = command.Reason,
                    // The column is timestamptz and the Dapper path carries no DateTimeOffset handler (it is
                    // read-only by design), so the instant travels as a UTC DateTime — the shape the waiver insert uses.
                    expires_at = expiresAt.UtcDateTime,
                },
                cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("the quarantine insert returned no row");
        }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.UniqueViolation
            && error.ConstraintName == "quarantine_entry_active_case_key")
        {
            // The partial unique index (org_id, project_id, case_id) WHERE state = 'active' is the authority: an
            // unswept expired entry still counts as active, so a second create for the case is 409 (07.3.1 §5).
            // Only that index maps to quarantine_conflict; any other unique violation (e.g. the primary key) is a
            // real bug and must propagate rather than be masked as a conflict.
            return Result.Failure<QuarantineEntry>(QuarantineErrors.Conflict);
        }

        audit.Describe(new AuditObject("quarantine", id.ToString("D")));

        return Result.Success(row, 201);
    }

    /// <summary>
    /// Null when the body is acceptable, else the 422 with one <see cref="ErrorDetail"/> per offending field;
    /// <paramref name="expiresAt"/> is the validated expiry (07.3.1 §5).
    /// </summary>
    private static Error? Validate(CreateQuarantineEntryCommand command, TimeProvider clock, out DateTimeOffset expiresAt)
    {
        expiresAt = default;
        var details = new List<ErrorDetail>();

        if (command.CaseId == Guid.Empty)
        {
            details.Add(new ErrorDetail("case_id", "required"));
        }

        if (command.Owner == Guid.Empty)
        {
            details.Add(new ErrorDetail("owner", "required"));
        }

        if (string.IsNullOrEmpty(command.Reason))
        {
            details.Add(new ErrorDetail("reason", "empty"));
        }
        else if (QuarantineRules.CharacterCount(command.Reason) > QuarantineRules.ReasonMaxLength)
        {
            details.Add(new ErrorDetail("reason", "too_long"));
        }
        else if (QuarantineRules.HasUnstorableText(command.Reason))
        {
            details.Add(new ErrorDetail("reason", "invalid_value"));
        }

        if (command.ExpiresAt is not { } expiry)
        {
            details.Add(new ErrorDetail("expires_at", "required"));
        }
        else
        {
            expiresAt = expiry;
            if (expiry <= clock.GetUtcNow())
            {
                details.Add(new ErrorDetail("expires_at", "not_in_future"));
            }
        }

        return details.Count == 0 ? null : Error.Invalid("the quarantine request is not valid", details);
    }
}

/// <summary>Remove a quarantine entry (<c>DELETE /v1/quarantine/{q}</c>, 07.3.1 §5, AC6).</summary>
/// <param name="QuarantineId">The entry from the route (<c>{q}</c>).</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c> (00 §9.1).</param>
internal sealed record RemoveQuarantineEntryCommand(Guid QuarantineId, string IdempotencyKey)
    : ICommand<Result<QuarantineEntry>>, IIdempotentCommand;

/// <summary>
/// Handler of <see cref="RemoveQuarantineEntryCommand"/>: the caller must be a member, then one conditional
/// statement moves <c>active</c> to <c>removed</c> and returns the row, so two concurrent removes race on the row
/// and exactly one wins (AC6). A row that is unknown, another tenant's, outside the credential's project scope, or
/// not <c>active</c> matches nothing and is 404 <c>not_found</c> — never a silent no-op 200.
/// </summary>
[AuditedAction("quarantine.remove")]
[Permission(Permissions.Quarantine.Write)]
internal sealed class RemoveQuarantineEntryCommandHandler(
    ITenantDb db,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    IAuditTrail audit)
    : ICommandHandler<RemoveQuarantineEntryCommand, Result<QuarantineEntry>>
{
    /// <inheritdoc />
    public async ValueTask<Result<QuarantineEntry>> Handle(RemoveQuarantineEntryCommand command, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Policy, nameof(RemoveQuarantineEntryCommand));

        // The same member gate as the create (defence in depth): a service principal carries no user id, so it can
        // never remove an entry even if a future permission change let it past the evaluator.
        if (principals.Current?.UserId is null)
        {
            return Result.Failure<QuarantineEntry>(Error.Forbidden("only a member may remove a quarantine entry"));
        }

        var removed = await db.QuerySingleOrDefaultAsync<QuarantineEntry>(
            QuarantineSql.Remove,
            new
            {
                id = command.QuarantineId,
                token_scope = principals.Current?.ProjectScope,
                header_project = tenant.Current?.ProjectId,
            },
            cancellationToken).ConfigureAwait(false);
        if (removed is null)
        {
            return Result.Failure<QuarantineEntry>(QuarantineErrors.NotFound);
        }

        audit.Describe(new AuditObject("quarantine", command.QuarantineId.ToString("D")));

        return Result.Success(removed);
    }
}
