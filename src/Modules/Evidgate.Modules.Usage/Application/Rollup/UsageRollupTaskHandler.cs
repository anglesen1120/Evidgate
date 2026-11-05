using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Usage.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Evidgate.Modules.Usage.Application.Rollup;

/// <summary>
/// Platform task: pages the org ids that hold ledger rows through the sweep role's definer, then recomputes each org's
/// roll-up inside that org's own tenant scope, and only there — the aggregate rows take their tenant from the scope,
/// never from the scan.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RequiresTenant"/> is <c>false</c> because the scan spans tenants: it cannot run inside any one of them.
/// The definer returns org ids and nothing else, so a page names which tenants to recompute without exposing a row of
/// theirs; the recompute then runs as <c>evidgate_app</c> under RLS.
/// </para>
/// <para>
/// A period whose ledger rows disappear after a roll-up was computed is the one gap this module can detect on its own:
/// the source stream is Ingestion's, whose accepted batches are reached only through that module's contract, so the
/// recompute flags a scope that had usage and now has no ledger rows rather than reading another module's table. The
/// reconciliation read reports those rows and clears the flag when rows return.
/// </para>
/// </remarks>
internal sealed partial class UsageRollupTaskHandler(
    IWorkerSchedDataSource sweep,
    ITenantScopeFactory scopes,
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
        var drained = false;
        Guid? after = null;

        for (var page = 0; page < UsageRollupSweeps.MaxPagesPerFire; page++)
        {
            var orgs = await ListOrgsAsync(after, limit, cancellationToken).ConfigureAwait(false);
            if (orgs.Count == 0)
            {
                drained = true;
                break;
            }

            foreach (var orgId in orgs)
            {
                // One tenant scope per org, committed per org: a failure on one tenant leaves the others' roll-up rows
                // durable instead of rolling a whole fire back, and the task row still reports the failure.
                await using var orgScope = await scopes
                    .BeginAsync(orgId, projectId: null, readOnlyTransaction: false, cancellationToken)
                    .ConfigureAwait(false);
                await UsageRollupStore.RecomputeAsync(orgScope, cancellationToken).ConfigureAwait(false);
                await orgScope.CommitAsync(cancellationToken).ConfigureAwait(false);
                recomputed++;
            }

            after = orgs[^1];

            // A page below the limit is the scan's own stop signal: the keyset cursor had nothing left after it.
            if (orgs.Count < limit)
            {
                drained = true;
                break;
            }
        }

        // A full last page at the bound means the scan has no evidence the backlog ended: the rest waits for the next
        // fire, so the cut is logged rather than passed off as a finished recompute.
        if (!drained)
        {
            LogPageBoundReached(UsageRollupSweeps.MaxPagesPerFire, limit);
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

    [LoggerMessage(EventId = 2470, Level = LogLevel.Information, Message = "Recomputed usage roll-up rows for {Orgs} org(s)")]
    private partial void LogRecomputed(int orgs);

    [LoggerMessage(
        EventId = 2471,
        Level = LogLevel.Warning,
        Message = "Usage roll-up fire reached its page bound: {Pages} pages of {Limit} orgs; the remainder waits for the next fire")]
    private partial void LogPageBoundReached(int pages, int limit);
}
