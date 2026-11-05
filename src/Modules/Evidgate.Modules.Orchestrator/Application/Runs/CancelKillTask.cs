using System.Globalization;
using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Orchestrator.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Evidgate.Modules.Orchestrator.Application.Runs;

/// <summary>The members of one forced-kill row, as written (snake_case on the wire).</summary>
/// <param name="RunId">The canceled run, for the attempt's log line; the request row owns the stop state.</param>
/// <param name="CancelRequestId">The <c>run_cancel_request</c> row this fire is allowed to move.</param>
internal sealed record CancelKillPayload(Guid RunId, Guid CancelRequestId);

/// <summary>
/// The stable names of the forced kill: the task type, the key that makes a repeated cancel enqueue nothing, and the
/// payload the fire carries.
/// <para>
/// The task row is the schedule, not a timer: it is inserted in the same tenant transaction as the request row with
/// <c>available_at = kill_at</c>, so a restart loses neither the deadline nor the fire. A plain task row, never a cron
/// occurrence — the kill happens once per request.
/// </para>
/// </summary>
internal static class CancelKillContract
{
    /// <summary>The <c>task_type</c> of one forced kill.</summary>
    public const string TaskType = "orchestrator.run.cancel.kill";

    /// <summary>snake_case, the wire convention every payload of this platform writes.</summary>
    private static readonly JsonSerializerOptions PayloadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>
    /// The key of one request's kill: <c>orchestrator:run:{run}:cancel:kill:{request}</c>. The request id makes it unique
    /// per accepted cancel, so a retried producer — or a second operator whose cancel lost the request insert — cannot
    /// queue a second kill.
    /// </summary>
    /// <param name="runId">The canceled run.</param>
    /// <param name="cancelRequestId">The accepted <c>run_cancel_request</c> row.</param>
    public static string IdempotencyKey(Guid runId, Guid cancelRequestId) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"orchestrator:run:{runId:D}:cancel:kill:{cancelRequestId:D}");

    /// <summary>The payload of one kill: the run and the request row it may move.</summary>
    /// <param name="runId">The canceled run.</param>
    /// <param name="cancelRequestId">The accepted <c>run_cancel_request</c> row.</param>
    public static string PayloadJson(Guid runId, Guid cancelRequestId) =>
        JsonSerializer.Serialize(new CancelKillPayload(runId, cancelRequestId), PayloadOptions);
}

/// <summary>
/// Consumes one forced-kill row at <c>kill_at</c>: it moves the request row to <c>killed</c> and never touches
/// <c>run.state</c>, which has been <c>canceled</c> since the request was accepted. The request row's stage is the
/// durable execution-side marker of the cancel — <c>requested</c>/<c>soft_stopping</c> while the graceful window is
/// open, <c>killed</c> once it is over — and it is what a job-killing channel would read; no such channel exists yet,
/// so this fire records the deadline and the stage and nothing else.
/// <para>
/// Every outcome is a normal completion, because the row is a schedule and not a command: a request already
/// <c>killed</c> or <c>soft_stopped</c> is a no-op (a replay, or a graceful stop recorded before the deadline), and a
/// request this tenant cannot see is a no-op too — the row policy makes another org's request not a row at all. The one
/// failure is a claim before the deadline, thrown rather than swallowed so the row retries instead of dropping the kill.
/// </para>
/// <para>
/// <see cref="IServiceScopeFactory"/> reaches the scoped <see cref="ITenantDb"/>: handlers are singletons, and the DI
/// scope is not the tenant scope — that one is ambient and stays the consumer's, including its commit.
/// </para>
/// </summary>
/// <param name="scopeFactory">Creates the per-attempt scope the tenant accessor is resolved from.</param>
/// <param name="logger">The logger of the one structured line per attempt.</param>
internal sealed partial class CancelKillTaskHandler(
    IServiceScopeFactory scopeFactory,
    ILogger<CancelKillTaskHandler> logger) : ITaskHandler<CancelKillPayload>
{
    /// <summary>The stage a request reaches when a producer stopped inside the graceful window.</summary>
    private const string SoftStopped = "soft_stopped";

    /// <summary>The stage the deadline moves a still-stopping request to.</summary>
    private const string Killed = "killed";

    /// <inheritdoc />
    public string TaskType => CancelKillContract.TaskType;

    /// <inheritdoc />
    public bool RequiresTenant => true;

    /// <inheritdoc />
    public async ValueTask HandleAsync(
        CancelKillPayload payload,
        ITenantScope? scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (scope is null)
        {
            throw new InvalidOperationException(
                $"{CancelKillContract.TaskType} is tenant-bound: a claimed row without an org_id is dead-lettered as missing_tenant, never run unscoped");
        }

        await using var services = scopeFactory.CreateAsyncScope();
        var db = services.ServiceProvider.GetRequiredService<ITenantDb>();

        var outcome = await db.QuerySingleOrDefaultAsync<KillOutcomeRow>(
                RunCancelSql.MoveToKilled,
                new { cancel_request_id = payload.CancelRequestId },
                cancellationToken)
            .ConfigureAwait(false);

        // No row: the request is another tenant's (the policy hides it) or gone. A retry would find the same nothing, and
        // a task naming an invisible request is worth an operator's attention, unlike the ordinary replay below.
        if (outcome is null)
        {
            LogRequestNotVisible(payload.RunId, payload.CancelRequestId);
            return;
        }

        if (outcome.Moved > 0)
        {
            LogKilled(payload.RunId, payload.CancelRequestId);
            return;
        }

        // A final stage: a replay after the commit (killed) and a producer that stopped itself in time (soft_stopped)
        // both leave this fire nothing to do, which is why a soft stop is never undone by the deadline.
        if (outcome.State is Killed or SoftStopped)
        {
            LogAlreadyStopped(payload.RunId, payload.CancelRequestId, outcome.State);
            return;
        }

        // Still stopping at a claim the queue should not have allowed yet: throwing keeps the row (retry, then dead
        // letter) instead of recording a kill that did not happen as done.
        throw new InvalidOperationException(
            $"{CancelKillContract.TaskType} claimed before kill_at for cancel request '{payload.CancelRequestId:D}': the fire is due at '{outcome.KillAt:O}'");
    }

    /// <summary>
    /// The kill deadline reached with the request still stopping: the <c>killed</c> stage is recorded, and execution
    /// itself is left alone because no job-killing channel exists.
    /// </summary>
    [LoggerMessage(
        EventId = 2452,
        Level = LogLevel.Information,
        Message = "run {RunId} reached its cancel kill deadline (cancel request {CancelRequestId}): stage recorded")]
    private partial void LogKilled(Guid runId, Guid cancelRequestId);

    /// <summary>The fire found the request already final — a replay, or a soft stop that beat the deadline.</summary>
    [LoggerMessage(
        EventId = 2453,
        Level = LogLevel.Information,
        Message = "cancel request {CancelRequestId} of run {RunId} is already {State}: nothing to kill")]
    private partial void LogAlreadyStopped(Guid runId, Guid cancelRequestId, string state);

    /// <summary>The request row is not visible in this tenant, so the fire changed nothing.</summary>
    [LoggerMessage(
        EventId = 2454,
        Level = LogLevel.Warning,
        Message = "cancel request {CancelRequestId} of run {RunId} is not visible in this tenant: nothing killed")]
    private partial void LogRequestNotVisible(Guid runId, Guid cancelRequestId);

    /// <summary>The one row the statement answers: the stage it found and whether this attempt moved it.</summary>
    private sealed class KillOutcomeRow
    {
        public string State { get; init; } = string.Empty;

        public DateTimeOffset KillAt { get; init; }

        public long Moved { get; init; }
    }
}
