using Npgsql;
using NpgsqlTypes;

namespace Evidgate.Modules.Identity.Infrastructure.Persistence;

/// <summary>The T1 tuple the pre-tenant CI resolvers return. Ids are null for <c>pending_unproven</c>.</summary>
/// <param name="OrgId">Tenant, or null.</param>
/// <param name="ProjectId">Project, or null.</param>
/// <param name="SubjectId">The binding id or ingest-token id.</param>
/// <param name="Status">Binding: active / pending_verification / disabled / pending_unproven. Token: active / revoked / expired.</param>
internal sealed record CiResolution(Guid? OrgId, Guid? ProjectId, Guid? SubjectId, string Status);

/// <summary>
/// The pre-tenant lookups of <c>POST /v1/ci/token</c> (00 §5.4.8, TL T1): they run on the app role before any
/// tenant scope exists, through the <c>evidgate_resolve</c> definer functions of migrations 0016 and 0018 — never by
/// reading <c>ci_binding</c> or <c>ingest_token</c> directly, which RLS would answer with nothing.
/// </summary>
internal interface ICiResolver
{
    /// <summary>The binding for a repository, preferring the active one; see migration 0016.</summary>
    /// <param name="provider">The CI provider.</param>
    /// <param name="repositoryId">The provider's immutable repository id.</param>
    /// <param name="challengeHashes">HMACs of the presented challenge under each live pepper, or null.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<CiResolution?> ResolveBindingAsync(
        string provider,
        long repositoryId,
        IReadOnlyList<byte[]>? challengeHashes,
        CancellationToken cancellationToken);

    /// <summary>The ingest token behind a public prefix, with its derived status.</summary>
    /// <param name="prefix">The 12-character prefix.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<CiResolution?> ResolveIngestTokenAsync(string prefix, CancellationToken cancellationToken);
}

internal sealed class NpgsqlCiResolver(NpgsqlDataSource dataSource) : ICiResolver
{
    public async ValueTask<CiResolution?> ResolveBindingAsync(
        string provider,
        long repositoryId,
        IReadOnlyList<byte[]>? challengeHashes,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "select org_id, project_id, binding_id, status from evidgate_resolve.resolve_ci_binding(@provider, @repository_id, @hashes)");
        command.Parameters.AddWithValue("provider", provider);
        command.Parameters.AddWithValue("repository_id", repositoryId);
        command.Parameters.Add(new NpgsqlParameter("hashes", NpgsqlDbType.Array | NpgsqlDbType.Bytea)
        {
            Value = challengeHashes is null ? DBNull.Value : challengeHashes.ToArray(),
        });

        return await ReadAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<CiResolution?> ResolveIngestTokenAsync(string prefix, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "select org_id, project_id, binding_id, status from evidgate_resolve.resolve_ingest_token(@prefix)");
        command.Parameters.AddWithValue("prefix", prefix);

        return await ReadAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<CiResolution?> ReadAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new CiResolution(
            reader[0] as Guid?,
            reader[1] as Guid?,
            reader[2] as Guid?,
            reader.GetString(3));
    }
}
