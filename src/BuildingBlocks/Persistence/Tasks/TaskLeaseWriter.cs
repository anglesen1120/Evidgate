using Dapper;
using Npgsql;

namespace Evidgate.BuildingBlocks.Persistence.Tasks;

/// <summary>
/// The completion side of the queue (01.5.1 §6): the three transitions a consumer
/// makes on a row it holds a lease on. Every one of them is conditional on the
/// <see cref="TaskClaim"/> it was given —
/// `where id = @id and state = 'active' and locked_until = @lease and version = @version`
/// — which is the whole safety story of the mechanism: a consumer whose lease
/// expired (and whose row was therefore reclaimed and handed to someone else)
/// cannot overwrite the newer attempt's outcome, and the newer attempt's completion
/// is the one that counts.
/// </summary>
public interface ITaskLeaseWriter
{
    /// <summary>
    /// `active → done` (handler returned). False when the claim is no longer current —
    /// the lease ran out and another consumer owns the row.
    /// </summary>
    Task<bool> MarkDoneAsync(TaskClaim claim, CancellationToken cancellationToken = default);

    /// <summary>
    /// `active → retry` (handler threw, attempts left): the lease is released and
    /// `available_at` moves out by <paramref name="backoff"/> — computed by the
    /// database (`now() + backoff`), so the delay does not depend on this host's clock
    /// agreeing with the database's. False when the claim is no longer current.
    /// </summary>
    Task<bool> MarkRetryAsync(
        TaskClaim claim,
        TimeSpan backoff,
        string lastError,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// `active → queued` without running the handler (01.5.1-T1.3's batch path): the
    /// claim is released and the attempt it had already counted is given back, so a
    /// row that only waited in a slow batch is redelivered without spending one of its
    /// retries. The same capability guard as the other transitions: a superseded claim
    /// cannot release the newer attempt's row.
    /// </summary>
    Task<bool> ReleaseAsync(TaskClaim claim, CancellationToken cancellationToken = default);

    /// <summary>
    /// `active → dead_letter` (retry cap reached). The row stays visible to operators
    /// (01.5.1-T4.2) until the retention sweep. False when the claim is no longer current.
    /// </summary>
    Task<bool> MarkDeadLetterAsync(
        TaskClaim claim,
        string lastError,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The production lease writer: one short `update` per transition on the app-role
/// data source, outside the handler's tenant transaction. The tenant transaction is
/// the handler's business boundary and must never span the mechanism row — a handler
/// that rolled back has produced no side effect, while the row still has to move on
/// (that is why 01.5.1 §8 P5 keeps the claim and the completion transactions short).
/// </summary>
/// <remarks>
/// A transition takes the whole <see cref="TaskClaim"/> because the claim <em>is</em>
/// the capability: `id` says which row, `locked_until` says which lease, and
/// `version` (0004's trigger bumps it on every update) says which attempt. Requiring
/// all three means a consumer that lost its lease cannot complete, retry or
/// dead-letter the newer attempt's row, whatever its lease timestamp happens to be —
/// the guard does not rest on the lease clock's resolution or on the reclaim timing
/// rules holding for ever.
/// </remarks>
public sealed class TaskLeaseWriter : ITaskLeaseWriter
{
    private const string Guard = """
        where id = @id
          and state = 'active'
          and locked_until = @lease
          and version = @version
        """;

    private const string DoneSql = $"""
        update public.task
           set state = 'done',
               locked_until = null
        {Guard}
        """;

    private const string RetrySql = $"""
        update public.task
           set state = 'retry',
               locked_until = null,
               available_at = now() + make_interval(secs => @backoff_seconds),
               last_error = @last_error
        {Guard}
        """;

    private const string ReleaseSql = $"""
        update public.task
           set state = 'queued',
               locked_until = null,
               available_at = now(),
               attempts = attempts - 1
        {Guard}
        """;

    private const string DeadLetterSql = $"""
        update public.task
           set state = 'dead_letter',
               locked_until = null,
               last_error = @last_error
        {Guard}
        """;

    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Creates the lease writer over the registered app-role data source.</summary>
    public TaskLeaseWriter(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    /// <inheritdoc />
    public Task<bool> MarkDoneAsync(TaskClaim claim, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claim);

        return ExecuteAsync(DoneSql, Parameters(claim), cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> MarkRetryAsync(
        TaskClaim claim,
        TimeSpan backoff,
        string lastError,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claim);

        var parameters = Parameters(claim);
        parameters.Add("backoff_seconds", backoff.TotalSeconds);
        parameters.Add("last_error", lastError);

        return ExecuteAsync(RetrySql, parameters, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> ReleaseAsync(TaskClaim claim, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claim);

        return ExecuteAsync(ReleaseSql, Parameters(claim), cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> MarkDeadLetterAsync(
        TaskClaim claim,
        string lastError,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claim);

        var parameters = Parameters(claim);
        parameters.Add("last_error", lastError);

        return ExecuteAsync(DeadLetterSql, parameters, cancellationToken);
    }

    private static DynamicParameters Parameters(TaskClaim claim)
    {
        var parameters = new DynamicParameters();
        parameters.Add("id", claim.Id);
        parameters.Add("lease", claim.LockedUntil);
        parameters.Add("version", claim.Version);

        return parameters;
    }

    private async Task<bool> ExecuteAsync(string sql, object parameters, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var updated = await connection.ExecuteAsync(new CommandDefinition(
            sql,
            parameters,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return updated == 1;
    }
}
