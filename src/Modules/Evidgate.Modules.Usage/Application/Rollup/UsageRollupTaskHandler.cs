using System.Globalization;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Evidgate.Modules.Usage.Application.Rollup;

/// <summary>
/// Platform task: pages the org ids that hold ledger or roll-up rows through the sweep role's definer, then recomputes
/// each org's roll-up inside that org's own tenant scope, and only there — the aggregate rows take their tenant from
/// the scope, never from the scan.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RequiresTenant"/> is <c>false</c> because the scan spans tenants: it cannot run inside any one of them.
/// The definer returns org ids and nothing else, so a page names which tenants to recompute without exposing a row of
/// theirs; the recompute then runs as <c>evidgate_app</c> under RLS.
/// </para>
/// <para>
/// One fire drains the scan: the keyset cursor advances page by page until the definer returns nothing, so no org is
/// starved because a large tenant list happened to fill earlier pages. Only the cancellation token stops it early.
/// </para>
/// <para>
/// A period whose ledger rows disappear after a roll-up was computed is the one gap this module can detect on its own:
/// the source stream is Ingestion's, whose accepted batches are reached only through that module's contract, so the
/// recompute flags a scope that had usage and now has no ledger rows rather than reading another module's table. The
/// reconciliation read reports those rows and clears the flag when rows return. Because the scan walks orgs that hold a
/// roll-up row as well as ledger rows, the org that lost its last ledger row is still visited and flagged.
/// </para>
/// <para>
/// A tenant that fails is collected and the scan continues: one broken org must not starve the tenants behind it. The
/// fire then throws one aggregate naming the failed count and their ids, so the task queue retries the whole fire.
/// </para>
/// </remarks>
internal sealed partial class UsageRollupTaskHandler(
    IWorkerSchedDataSource sweep,
    ITenantScopeFactory scopes,
    IUsageRollupRecompute recompute,
    ILogger<UsageRollupTaskHandler> logger) : ITaskHandler<UsageRollupPayload>
{
    /// <inheritdoc />
    public string TaskType => UsageRollupSweeps.TaskType;

    /// <inheritdoc />
    public bool RequiresTenant => false;

    /// <inheritdoc />
    public async ValueTask HandleAsync(
        UsageRollupPayload payload,
        ITenantScope? scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var limit = Math.Clamp(payload.OrgBatch, 1, UsageRollupSweeps.OrgBatch);
        var recomputed = 0;
        List<Guid>? failedOrgIds = null;
        List<Exception>? failures = null;
        Guid? after = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var orgs = await ListOrgsAsync(after, limit, cancellationToken).ConfigureAwait(false);
            if (orgs.Count == 0)
            {
                break;
            }

            foreach (var orgId in orgs)
            {
                try
                {
                    // One tenant scope per org, committed per org: a failure on one tenant leaves the others' roll-up
                    // rows durable instead of rolling a whole fire back.
                    await using var orgScope = await scopes
                        .BeginAsync(orgId, projectId: null, readOnlyTransaction: false, cancellationToken)
                        .ConfigureAwait(false);
                    await recompute.RecomputeAsync(orgScope, cancellationToken).ConfigureAwait(false);
                    await orgScope.CommitAsync(cancellationToken).ConfigureAwait(false);
                    recomputed++;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    (failedOrgIds ??= []).Add(orgId);
                    (failures ??= []).Add(exception);
                    LogOrgFailed(orgId, exception);
                }
            }

            after = orgs[^1];
        }

        if (failures is not null)
        {
            throw new AggregateException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"usage roll-up failed for {failures.Count} org(s): {string.Join(", ", failedOrgIds!)}"),
                failures);
        }

        LogRecomputed(recomputed);
    }

    private async Task<List<Guid>> ListOrgsAsync(Guid? after, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await sweep.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("select evidgate.usage_ledger_org_ids(@after, @limit)", connection);
        command.Parameters.AddWithValue("after", (object?)after ?? DBNull.Value);
        command.Parameters.AddWithValue("limit", limit);

        var orgs = new List<Guid>(limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            orgs.Add(reader.GetGuid(0));
        }

        return orgs;
    }

    [LoggerMessage(EventId = 2477, Level = LogLevel.Information, Message = "Recomputed usage roll-up rows for {Orgs} org(s)")]
    private partial void LogRecomputed(int orgs);

    [LoggerMessage(EventId = 2478, Level = LogLevel.Warning, Message = "Usage roll-up failed for org {Org}")]
    private partial void LogOrgFailed(Guid org, Exception exception);
}
