using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Npgsql;

namespace Evidgate.Modules.Policy.Infrastructure.Persistence;

/// <summary>One waiver or quarantine entry the sweep moved to <c>expired</c>.</summary>
/// <param name="Kind"><c>waiver</c> or <c>quarantine</c>.</param>
/// <param name="Id">The row's id.</param>
/// <param name="ProjectId">The project the row belongs to.</param>
internal sealed record ExpiredException(string Kind, Guid Id, Guid ProjectId);

/// <summary>
/// The two statements of the expiry sweep. Both go through definer functions, so neither the app role nor the sweep
/// role gets a table grant on waivers or quarantine entries: the sweep role lists which orgs have due rows and nothing
/// else, and the tenant transaction moves one org's own rows.
/// </summary>
internal sealed class ExceptionExpiryStore(IWorkerSchedDataSource sweep, ITenantDb db)
{
    /// <summary>The kind the sweep reports for a waiver.</summary>
    internal const string WaiverKind = "waiver";

    /// <summary>The kind the sweep reports for a quarantine entry.</summary>
    internal const string QuarantineKind = "quarantine";

    private const string MoveDue =
        """
        select kind as "Kind", id as "Id", project_id as "ProjectId"
          from evidgate.sweep_expired_exceptions(@org_id, @limit)
        """;

    /// <summary>One keyset page of the org ids that hold a due waiver or quarantine entry, read as the sweep role.</summary>
    public async Task<IReadOnlyList<Guid>> ListDueOrgsAsync(Guid? after, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await sweep.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("select evidgate.exception_expiry_org_ids(@after, @limit)", connection);
        command.Parameters.AddWithValue("after", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)after ?? DBNull.Value);
        command.Parameters.AddWithValue("limit", limit);

        var orgs = new List<Guid>(limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            orgs.Add(reader.GetGuid(0));
        }

        return orgs;
    }

    /// <summary>
    /// Moves up to <paramref name="limit"/> due rows of the scope's org to <c>expired</c> and returns exactly those rows.
    /// Needs an open tenant scope: the function refuses any org other than the scope's.
    /// </summary>
    public Task<IReadOnlyList<ExpiredException>> MoveDueAsync(Guid orgId, int limit, CancellationToken cancellationToken) =>
        db.QueryAsync<ExpiredException>(MoveDue, new { org_id = orgId, limit }, cancellationToken);
}
