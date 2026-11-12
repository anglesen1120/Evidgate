using Dapper;
using Evidgate.BuildingBlocks.Persistence.Tenancy;

namespace Evidgate.BuildingBlocks.Persistence.Tasks;

/// <summary>
/// The outcome of <c>POST /internal/operator/tasks/dead-letter/{id}/replay</c>:
/// <see cref="Replayed"/> carries the row's new
/// <c>available_at</c>, <see cref="NotFound"/> and <see cref="NotDeadLetter"/>
/// discriminate the two ways the guarded update matched no row.
/// </summary>
public enum ReplayDeadLetterOutcome
{
    /// <summary>The row was `dead_letter` and is now `queued` with its attempts reset.</summary>
    Replayed,

    /// <summary>No task row exists with that id at all.</summary>
    NotFound,

    /// <summary>The row exists but is not `dead_letter` (e.g. it was already replayed or reclaimed).</summary>
    NotDeadLetter,
}

/// <summary>The row the replay returned, or the failure outcome.</summary>
public sealed record ReplayDeadLetterResult(ReplayDeadLetterOutcome Outcome, DateTime? AvailableAt)
{
    /// <summary>A successful replay; <see cref="AvailableAt"/> is the row's new value.</summary>
    public static ReplayDeadLetterResult Replayed(DateTime availableAt) =>
        new(ReplayDeadLetterOutcome.Replayed, availableAt);

    /// <summary>No row with that id exists.</summary>
    public static ReplayDeadLetterResult NotFound() =>
        new(ReplayDeadLetterOutcome.NotFound, null);

    /// <summary>The row exists in a state that cannot be replayed.</summary>
    public static ReplayDeadLetterResult NotDeadLetter() =>
        new(ReplayDeadLetterOutcome.NotDeadLetter, null);
}

/// <summary>
/// The replay (<c>dead_letter → queued</c>): one call to
/// <c>evidgate.replay_dead_letter_task</c>, the owner <c>SECURITY DEFINER</c> function whose
/// guard is the capability — a row that is not <c>dead_letter</c> simply does not match,
/// which is why the endpoint is idempotent and needs no <c>ETag</c> or
/// <c>Idempotency-Key</c>.
/// </summary>
/// <remarks>
/// Deliberately NOT an <c>ICommand</c> through the Mediator C2 pipeline: a
/// Result-contract command would trip <c>TenantTransactionBehaviour</c>'s fail-closed
/// Require() on a request the tenant pipeline never entered. The org and the acting
/// operator arrive as parameters, the service opens the tenant transaction the function
/// reads <c>evidgate.current_org_id()</c> from, and the function itself writes the replay's
/// audit row — one action and its audit in one transaction, as the definer.
/// </remarks>
public sealed class ReplayDeadLetterTask
{
    /// <summary>The function's answer: the state it reached and the new due time when replayed.</summary>
    private const string ReplaySql = """
        select outcome as "Outcome",
               available_at as "AvailableAt"
          from evidgate.replay_dead_letter_task(@id, @actor)
        """;

    private readonly ITenantScopeFactory _scopes;

    /// <summary>Creates the replay service over the tenant scope factory.</summary>
    public ReplayDeadLetterTask(ITenantScopeFactory scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);

        _scopes = scopes;
    }

    /// <summary>
    /// Requeues the dead-lettered row <paramref name="taskId"/>: clears the lease,
    /// resets the attempts the row spent on its last handler run and makes it
    /// immediately claimable. The row keeps its <c>last_error</c> — a replay is
    /// a re-queue, not an acquittal, and the operator's audit of why it
    /// dead-lettered stays on the row.
    /// </summary>
    /// <param name="orgId">The org the operator route names; the scope the function reads the org from.</param>
    /// <param name="taskId">The dead-lettered row's id; a row of another org answers not-found.</param>
    /// <param name="actor">The authenticated operator, the audit row's actor.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public async Task<ReplayDeadLetterResult> ExecuteAsync(
        Guid orgId,
        Guid taskId,
        Guid actor,
        CancellationToken cancellationToken = default)
    {
        await using var scope = await _scopes.BeginAsync(orgId, null, false, cancellationToken).ConfigureAwait(false);

        var row = await scope.Connection.QuerySingleAsync<ReplayFunctionRow>(new CommandDefinition(
            ReplaySql,
            new { id = taskId, actor },
            scope.Transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);

        return row.Outcome switch
        {
            "replayed" when row.AvailableAt is { } availableAt => ReplayDeadLetterResult.Replayed(availableAt),
            "not_found" => ReplayDeadLetterResult.NotFound(),
            "not_dead_letter" => ReplayDeadLetterResult.NotDeadLetter(),
            // Fail closed: an answer this service does not know is not a successful replay.
            _ => throw new InvalidOperationException(
                $"evidgate.replay_dead_letter_task answered '{row.Outcome}' for a replay of task {taskId:D}"),
        };
    }

    /// <summary>The function's two output columns.</summary>
    private sealed record ReplayFunctionRow(string Outcome, DateTime? AvailableAt);
}
