using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Messaging;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
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
/// trimmed length are rules a record cannot express) and never serialized: <see cref="BodyHash"/> carries its identity
/// into the idempotency record instead.
/// </param>
/// <param name="IdempotencyKey">The caller's <c>Idempotency-Key</c> (00 §9.1); the pipeline claims it for <c>run:cancel</c>.</param>
internal sealed record CancelRunCommand(
    Guid RunId,
    [property: JsonIgnore] ReadOnlyMemory<byte> Body,
    string IdempotencyKey) : ICommand<Result>, IIdempotentCommand
{
    /// <summary>The stable operation the key is claimed for, so a rename never re-opens an old key.</summary>
    string IIdempotentCommand.Operation => "run:cancel";

    /// <summary>
    /// The request identity the idempotency store hashes beside the serialized <see cref="RunId"/>: the raw SHA-256 of the
    /// body bytes, because the body itself is excluded from the serialized command. Raw bytes on purpose — a
    /// byte-identical retry from the same client is the case that must replay, and no canonical form is needed for that.
    /// </summary>
    public string BodyHash => "raw-sha256:" + Convert.ToHexStringLower(SHA256.HashData(Body.Span));
}

/// <summary>
/// Handler of <see cref="CancelRunCommand"/>, inside the C2 pipeline's one tenant transaction.
/// <para>
/// <b>Order of decisions.</b> The caller is required to be a member first — <c>run:cancel</c> is a member role's
/// permission and the request row records its requester — then the body. The run is moved to <c>canceled</c> through
/// <see cref="IRunStateTransition"/>, the one guarded entry point of the 00 §8.1 adjacency table: it takes the run row
/// <c>FOR NO KEY UPDATE</c>, so a cancel and a reconciler verdict serialise on that row and whichever commits first owns
/// the terminal state (a canceled run is never completed afterwards). A terminal run — including one already
/// <c>canceled</c> — is that call's no-op and the answer here is the same <c>202</c> with nothing written.
/// </para>
/// <para>
/// <b>The durable request.</b> A run that actually moved gets exactly one <c>run_cancel_request</c> row: the insert's
/// <c>on conflict (org_id, run_id) do nothing</c> makes the row's own unique key the arbiter of a repeat, so an accepted
/// request survives a restart and a second one records nothing. Its windows are computed from configuration on the
/// handler's clock, and they act on execution only — <c>run.state</c> is already terminal when they start.
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
    IRunStateTransition runStates,
    IAuditTrail audit,
    IOptions<RunCancelOptions> options,
    TimeProvider clock) : ICommandHandler<CancelRunCommand, Result>
{
    /// <summary>The <c>audit_log.action</c> of an accepted cancel request (00 §7).</summary>
    internal const string CancelAuditAction = "run.cancel";

    /// <summary>The longest accepted <c>reason</c>, matching the database check on the request row.</summary>
    internal const int MaxReasonLength = 1000;

    /// <summary>The closed member set of the cancel body (00 §9.3); any other member is 422 <c>additional_properties</c>.</summary>
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

        var orgId = scopes.Require().OrgId;

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
            // Already terminal: nothing was recorded and nothing changed, so the cancel is an accepted no-op.
            audit.Suppress();
            activity?.SetTag("evidgate.run.cancel.outcome", "no_op");

            return Result.Success(StatusCodes.Status202Accepted);
        }

        var now = clock.GetUtcNow();
        var settings = options.Value;
        var inserted = await db.ExecuteAsync(
            RunCancelSql.InsertRequest,
            new
            {
                id = Guid.CreateVersion7(),
                org_id = orgId,
                run_id = command.RunId,
                requested_by = requestedBy,
                reason = parsed.SuccessValue,
                requested_at = now,
                soft_stop_deadline_at = now + settings.SoftStopDelay,
                kill_at = now + settings.KillDelay,
            },
            cancellationToken).ConfigureAwait(false);

        if (inserted == 0)
        {
            // The run's unique key decides a repeat, so nothing new was recorded here: the transition's own entry is the
            // whole audit of this call.
            audit.Suppress();
            activity?.SetTag("evidgate.run.cancel.outcome", "already_requested");

            return Result.Success(StatusCodes.Status202Accepted);
        }

        audit.Describe(new AuditObject("run", command.RunId.ToString("D")));

        activity?.SetTag("evidgate.run_id", command.RunId.ToString("D"));
        activity?.SetTag("evidgate.run.cancel.outcome", "requested");

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

        // UTF-16 units, which are never fewer than the database's character count, so anything accepted here fits the
        // row's 1..1000 check.
        if (reason.Length > MaxReasonLength)
        {
            return Invalid($"reason must be at most {MaxReasonLength} characters", "/reason", "too_long");
        }

        return Result.Success<string?>(reason);
    }

    private static Result<string?> Invalid(string message, string path, string reason) =>
        Result.Failure<string?>(Error.Invalid(message, [new ErrorDetail(path, reason)]));

    /// <summary>401: no member principal could be resolved, so nobody can be recorded as the requester.</summary>
    private static Error Unauthenticated => new(
        ApiErrorCode.Unauthenticated,
        "no authenticated member could be resolved",
        StatusCodes.Status401Unauthorized);
}
