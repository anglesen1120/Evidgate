using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Evidgate.BuildingBlocks.Canonicalization;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Messaging;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Orchestrator.Application.Manifests;
using Evidgate.Modules.Orchestrator.Contracts;
using Evidgate.Modules.Orchestrator.Domain;
using Evidgate.Modules.Orchestrator.Infrastructure.Persistence;
using Mediator;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Orchestrator.Application.Runs;

/// <summary>
/// <c>POST /v1/runs/{r}/cancel</c>: one cancel request for one run.
/// </summary>
/// <param name="RunId">The run named in the path; it must be visible to the caller's tenant scope.</param>
/// <param name="Body">
/// The raw request body, capped by the route before anything binds. It is parsed by the handler (the member set and the
/// code-point length are rules a record cannot express) and never serialized: <see cref="BodyHash"/> carries its identity
/// into the idempotency fingerprint instead, so a retry that differs only in member order or whitespace replays while a
/// different <c>reason</c> is a conflict.
/// </param>
/// <param name="IdempotencyKey">The caller's <c>Idempotency-Key</c>; the pipeline claims it for <c>run:cancel</c>.</param>
internal sealed record CancelRunCommand(
    Guid RunId,
    [property: JsonIgnore] ReadOnlyMemory<byte> Body,
    string IdempotencyKey) : ICommand<Result>, IIdempotentCommand
{
    /// <summary>The stable operation the key is claimed for, so a rename never re-opens an old key.</summary>
    string IIdempotentCommand.Operation => "run:cancel";

    /// <summary>
    /// The body's identity in the idempotency fingerprint. The key is claimed for (<c>run:cancel</c>, run), so two cancels
    /// of one run with different reasons are different requests and answer <c>409 idempotency_conflict</c>; the canonical
    /// form is what makes a whitespace- or member-order-variant retry of the same request a replay instead. A body that
    /// cannot be canonicalized (not JSON, or an unpaired surrogate) falls back to the raw digest of its bytes: the store
    /// does see that request first, but the handler refuses it with <c>422</c> and a failure leaves the key claim
    /// uncommitted, so the fallback digest never decides a later conflict.
    /// </summary>
    public string BodyHash { get; } = ComputeBodyHash(Body);

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
/// Handler of <see cref="CancelRunCommand"/>, inside the C2 pipeline's one tenant transaction.
/// <para>
/// <b>Order of decisions.</b> The caller is required to be a member first — <c>run:cancel</c> is a member role's
/// permission and the request row records its requester — then the body. The run is moved to <c>canceled</c> through
/// <see cref="IRunStateTransition"/>, the one guarded entry point of the run-state adjacency table: it takes the run row
/// <c>FOR NO KEY UPDATE</c>, so a cancel and a reconciler verdict serialise on that row and whichever commits first owns
/// the terminal state (a canceled run is never completed afterwards). A terminal run — including one already
/// <c>canceled</c> — is that call's no-op and the answer here is the same <c>202</c> with nothing written.
/// </para>
/// <para>
/// <b>The durable request.</b> A run that actually moved gets exactly one <c>run_cancel_request</c> row: the insert's
/// <c>on conflict (org_id, run_id) do nothing</c> makes the row's own unique key the arbiter of a repeat, so an accepted
/// request survives a restart and a second one records nothing. Its windows are computed from configuration on the
/// handler's clock, and they act on execution only — <c>run.state</c> is already terminal when they start. The same
/// transaction queues the one kill task due at <c>kill_at</c>, so the deadline is durable with the request.
/// </para>
/// <para>
/// <b>Audit.</b> An applied transition writes its own canonical <c>run.state_changed</c> entry, credited to the caller.
/// This handler's own entry is described <b>after</b> that call returns, because they share the unit's single pending
/// description slot; a no-op cancel changed nothing, so it suppresses its entry instead.
/// </para>
/// </summary>
[AuditedAction(CancelRunCommandHandler.CancelAuditAction)]
[Permission(Permissions.Run.Cancel)]
internal sealed partial class CancelRunCommandHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    IRunStateTransition runStates,
    IAuditTrail audit,
    ITaskQueueWriter tasks,
    IOptions<TasksOptions> taskOptions,
    IOptions<RunCancelOptions> options,
    TimeProvider clock) : ICommandHandler<CancelRunCommand, Result>
{
    /// <summary>The <c>audit_log.action</c> of an accepted cancel request.</summary>
    internal const string CancelAuditAction = "run.cancel";

    /// <summary>The longest accepted <c>reason</c>, matching the database check on the request row.</summary>
    internal const int MaxReasonLength = 1000;

    /// <summary>The closed member set of the cancel body; any other member is 422 <c>additional_properties</c>.</summary>
    private const string ReasonMember = "reason";

    /// <inheritdoc />
    public async ValueTask<Result> Handle(CancelRunCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Orchestrator, nameof(CancelRunCommand));

        // Fail closed: the request row records who asked, and only a member principal carries a user id (run:cancel is
        // outside the service-role ceiling, so a service caller never reaches the permission check's grant either).
        if (principals.Current is not { UserId: { } requestedBy })
        {
            return Result.Failure(Unauthenticated);
        }

        var parsed = ParseReason(command.Body);
        if (!parsed.IsSuccess)
        {
            return Result.Failure(parsed.Error!);
        }

        if (!await IsRunInScopeAsync(command.RunId, cancellationToken).ConfigureAwait(false))
        {
            return Result.Failure(RunNotFound);
        }

        // The guarded edge, in this transaction: it locks the run row, classifies `* -> canceled` and writes the state
        // change, its outbox row and its own audit entry. Nothing about the request row is decided before it answers.
        var transition = await runStates
            .TransitionAsync(
                new TransitionRunStateCommand(command.RunId, RunStates.Canceled, TransitionActor.Caller),
                cancellationToken)
            .ConfigureAwait(false);

        if (!transition.IsSuccess)
        {
            return Result.Failure(transition.Error!);
        }

        if (transition.Value.Outcome != TransitionOutcome.Applied)
        {
            return Accepted(activity, "no_op");
        }

        var inserted = await RecordRequestAsync(command.RunId, requestedBy, (string?)parsed.SuccessValue, cancellationToken)
            .ConfigureAwait(false);
        if (inserted == 0)
        {
            return Accepted(activity, "already_requested");
        }

        audit.Describe(new AuditObject("run", command.RunId.ToString("D")));

        activity?.SetTag("evidgate.run_id", command.RunId.ToString("D"));
        activity?.SetTag("evidgate.run.cancel.outcome", "requested");

        return Result.Success(StatusCodes.Status202Accepted);
    }

    /// <summary>
    /// The project half of the scope rule, read unlocked before the guarded transition: a project-scoped credential must
    /// not cancel a sibling project's run, and a run outside the caller's tenant or project answers what an unknown id
    /// answers, so the refusal is not an existence oracle. Nothing is written and nothing is locked here.
    /// </summary>
    private async ValueTask<bool> IsRunInScopeAsync(Guid runId, CancellationToken cancellationToken)
    {
        var runProject = await db.QuerySingleOrDefaultAsync<RunProjectRow>(
                RunCancelSql.RunProject,
                new { run_id = runId },
                cancellationToken)
            .ConfigureAwait(false);

        return runProject is not null && OrchestratorScope.Project(runProject.ProjectId, principals, tenant) is null;
    }

    /// <summary>
    /// Writes the one durable request row of an applied cancel and answers its affected-row count: <c>1</c> when this
    /// call recorded it, <c>0</c> when the run's own unique key already held one. The stop windows are computed here from
    /// configuration on the handler's clock, so a repeat can never reopen or shift them.
    /// <para>
    /// The kill row follows the request row in this same transaction, so "the cancel is recorded" and "the kill is
    /// due at <c>kill_at</c>" commit together or not at all; its key names the request, so a second writer of the
    /// same request inserts nothing.
    /// </para>
    /// </summary>
    private async ValueTask<int> RecordRequestAsync(
        Guid runId,
        Guid requestedBy,
        string? reason,
        CancellationToken cancellationToken)
    {
        // The one scope of this unit: its connection and transaction are the ones the request row and the kill row are
        // written on, which is what makes them one durable event.
        var scope = scopes.Require();
        var now = clock.GetUtcNow();
        var settings = options.Value;
        var requestId = Guid.CreateVersion7();
        var killAt = now + settings.KillDelay;

        var inserted = await db.ExecuteAsync(
            RunCancelSql.InsertRequest,
            new
            {
                id = requestId,
                org_id = scope.OrgId,
                run_id = runId,
                requested_by = requestedBy,
                reason,
                requested_at = now,
                soft_stop_deadline_at = now + settings.SoftStopDelay,
                kill_at = killAt,
            },
            cancellationToken).ConfigureAwait(false);

        if (inserted == 0)
        {
            return 0;
        }

        // The queue's contract is that the row is durable with the business change, so it takes the caller's own
        // connection and transaction: a rollback leaves neither the request nor its schedule behind.
        // The queue is the deployment's routing decision for this task_type, never a constant: the Worker consumes the
        // queues it is configured for, so a row written into a queue nobody polls would lose the kill silently. Same
        // rule as the outbox dispatcher; resolved here because this row is written straight onto the caller's transaction.
        await tasks.EnqueueAsync(
            scope.Connection,
            scope.Transaction,
            new TaskEnqueue
            {
                Queue = TaskQueueRouting.QueueFor(taskOptions.Value, CancelKillContract.TaskType),
                TaskType = CancelKillContract.TaskType,
                PayloadJson = CancelKillContract.PayloadJson(runId, requestId),
                IdempotencyKey = CancelKillContract.IdempotencyKey(runId, requestId),
                OrgId = scope.OrgId,
                AvailableAt = killAt.UtcDateTime,
            },
            cancellationToken).ConfigureAwait(false);

        return inserted;
    }

    /// <summary>
    /// The <c>202</c> of a call that recorded nothing. A terminal run and a repeat request both leave the state and the
    /// request row untouched, so the unit's pending audit description is released and only the activity tag tells the
    /// two apart; the transition's own entry, where one ran, is the whole audit.
    /// </summary>
    private Result Accepted(Activity? activity, string outcome)
    {
        audit.Suppress();
        activity?.SetTag("evidgate.run.cancel.outcome", outcome);

        return Result.Success(StatusCodes.Status202Accepted);
    }

    /// <summary>
    /// The optional body: an absent body is "no reason", a present one is a closed object carrying <c>reason</c> as a
    /// string or <c>null</c>. A blank string is refused rather than stored, since an absent body already says "none".
    /// </summary>
    private static Result<string?> ParseReason(ReadOnlyMemory<byte> body)
    {
        if (body.IsEmpty)
        {
            return Result.Success<string?>(null);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return Invalid("the request body is not JSON", string.Empty, ApiErrorCode.RequestInvalid);
        }

        using var parsed = document;
        var root = parsed.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Invalid("the request body must be a JSON object", string.Empty, ApiErrorCode.RequestInvalid);
        }

        foreach (var member in root.EnumerateObject())
        {
            if (!string.Equals(member.Name, ReasonMember, StringComparison.Ordinal))
            {
                return Invalid("the request body carries an unknown field", $"/{member.Name}", "additional_properties");
            }
        }

        if (JsonBody.HasUnstorableText(root))
        {
            return Invalid("the request body carries text that cannot be stored", string.Empty, "invalid_value");
        }

        return ReadReason(root);
    }

    /// <summary>
    /// The <c>reason</c> member of an already-validated body: absent and <c>null</c> both mean "no reason", a blank
    /// string is refused because an absent body already says "none", and the bound is counted in Unicode code points —
    /// the unit the contract's <c>maxLength</c> and PostgreSQL's <c>char_length</c> both count, so a reason of astral
    /// characters is not refused long before the row's check would.
    /// </summary>
    private static Result<string?> ReadReason(JsonElement root)
    {
        if (!root.TryGetProperty(ReasonMember, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return Result.Success<string?>(null);
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            return Invalid("reason must be a string or null", "/reason", "invalid_value");
        }

        var reason = element.GetString()!;
        if (string.IsNullOrWhiteSpace(reason))
        {
            return Invalid("reason must not be blank", "/reason", "invalid_value");
        }

        if (ExceedsCodePoints(reason, MaxReasonLength))
        {
            return Invalid($"reason must be at most {MaxReasonLength} characters", "/reason", "too_long");
        }

        return Result.Success<string?>(reason);
    }

    /// <summary>
    /// True when <paramref name="text"/> holds more than <paramref name="limit"/> code points. A string of at most
    /// <paramref name="limit"/> UTF-16 units cannot exceed that many code points, so only longer strings are walked —
    /// and the walk counts no more than <paramref name="limit"/> + 1 runes, never the whole tail.
    /// </summary>
    private static bool ExceedsCodePoints(string text, int limit)
    {
        if (text.Length <= limit)
        {
            return false;
        }

        var count = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (++count > limit)
            {
                return true;
            }
        }

        return false;
    }

    private static Result<string?> Invalid(string message, string path, string reason) =>
        Result.Failure<string?>(Error.Invalid(message, [new ErrorDetail(path, reason)]));

    /// <summary>401: no member principal could be resolved, so nobody can be recorded as the requester.</summary>
    private static Error Unauthenticated => new(
        ApiErrorCode.Unauthenticated,
        "no authenticated member could be resolved",
        StatusCodes.Status401Unauthorized);

    /// <summary>The one <c>404</c> an out-of-scope run and a missing one share, so neither is an existence oracle.</summary>
    private static Error RunNotFound => Error.NotFound("no run with that id is visible to this tenant");

    /// <summary>The one column the project scope needs before the transition.</summary>
    private sealed class RunProjectRow
    {
        public Guid ProjectId { get; init; }
    }
}
