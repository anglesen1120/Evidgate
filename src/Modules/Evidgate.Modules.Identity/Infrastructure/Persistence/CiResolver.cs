using Npgsql;

namespace Evidgate.Modules.Identity.Infrastructure.Persistence;

/// <summary>The tenant and project of the ingest token identified by its public prefix.</summary>
internal sealed record CiResolution(Guid OrgId, Guid ProjectId, Guid IngestTokenId);

/// <summary>
/// Resolves a public prefix through the pre-tenant definer, without reading a tenant's RLS table.
/// </summary>
internal interface ICiResolver
{
    /// <summary>The project and token behind a public prefix.</summary>
    /// <param name="prefix">The 12-character prefix.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<CiResolution?> ResolveIngestTokenAsync(string prefix, CancellationToken cancellationToken);
}

internal sealed class NpgsqlCiResolver(NpgsqlDataSource dataSource) : ICiResolver
{
    public async ValueTask<CiResolution?> ResolveIngestTokenAsync(string prefix, CancellationToken cancellationToken)
    {
        // The resolver contract calls the ingest token's primary key binding_id.
        await using var command = dataSource.CreateCommand(
            "select org_id, project_id, binding_id from evidgate_resolve.resolve_ingest_token(@prefix)");
        command.Parameters.AddWithValue("prefix", prefix);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new CiResolution(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2));
    }
}
