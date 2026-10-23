using Dapper;
using Npgsql;

namespace Evidgate.BuildingBlocks.Persistence.Resolve;

/// <summary>One org a user belongs to, as the pre-tenant resolver returns it (T1 tuple, member form).</summary>
/// <param name="OrgId">The org.</param>
/// <param name="Status">The ORG's status (<c>active</c> or <c>suspended</c>; deleted orgs are never returned).</param>
public sealed record MemberOrg(Guid OrgId, string Status);

/// <summary>An org of the caller with the fields <c>GET /v1/orgs</c> lists.</summary>
/// <param name="OrgId">The org.</param>
/// <param name="Slug">Its slug.</param>
/// <param name="Name">Its display name.</param>
/// <param name="Status">Its status.</param>
public sealed record MemberOrganization(Guid OrgId, string Slug, string Name, string Status);

/// <summary>
/// Pre-tenant membership lookups (00 §5.4.8, TL T1; 02.6.1-T3). They run before any
/// tenant scope exists, on the app role, through the owner-owned
/// <c>evidgate_resolve</c> functions of migration 0014 — never by reading
/// <c>membership</c> directly, which RLS would answer with zero rows.
/// </summary>
public interface IMemberOrgResolver
{
    /// <summary>
    /// The orgs where <paramref name="userId"/> holds an active membership. Read from
    /// the database on every call: removing a member takes effect on their next request.
    /// </summary>
    /// <param name="userId">The authenticated user.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>At most 200 orgs, ordered by id.</returns>
    ValueTask<IReadOnlyList<MemberOrg>> MemberOrgsAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// The hot-path check: the user's active membership in one org, or null. One row at
    /// most, so a member of any number of orgs is never truncated.
    /// </summary>
    /// <param name="userId">The authenticated user.</param>
    /// <param name="orgId">The org the request names.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<MemberOrg?> MemberOrgAsync(Guid userId, Guid orgId, CancellationToken cancellationToken);

    /// <summary>The caller's orgs with slug and name, for <c>GET /v1/orgs</c>.</summary>
    /// <param name="userId">The authenticated user.</param>
    /// <param name="limit">Requested page size; the function clamps it to 1–200.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<IReadOnlyList<MemberOrganization>> MemberOrganizationsAsync(
        Guid userId,
        int limit,
        CancellationToken cancellationToken);
}

/// <summary>Npgsql/Dapper implementation of <see cref="IMemberOrgResolver"/> on the app-role data source.</summary>
/// <param name="dataSource">The app-role data source (never the owner's).</param>
internal sealed class NpgsqlMemberOrgResolver(NpgsqlDataSource dataSource) : IMemberOrgResolver
{
    private const string MemberOrgsSql =
        "select org_id as OrgId, status as Status from evidgate_resolve.member_orgs(@user_id)";

    private const string MemberOrgSql =
        "select org_id as OrgId, status as Status from evidgate_resolve.member_org(@user_id, @org_id)";

    private const string MemberOrganizationsSql =
        "select org_id as OrgId, slug as Slug, name as Name, status as Status "
        + "from evidgate_resolve.member_organizations(@user_id, @limit)";

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<MemberOrg>> MemberOrgsAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<MemberOrg>(
            new CommandDefinition(MemberOrgsSql, new { user_id = userId }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return rows.AsList();
    }

    /// <inheritdoc />
    public async ValueTask<MemberOrg?> MemberOrgAsync(Guid userId, Guid orgId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        return await connection.QuerySingleOrDefaultAsync<MemberOrg>(
            new CommandDefinition(MemberOrgSql, new { user_id = userId, org_id = orgId }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<MemberOrganization>> MemberOrganizationsAsync(
        Guid userId,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<MemberOrganization>(
            new CommandDefinition(MemberOrganizationsSql, new { user_id = userId, limit }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return rows.AsList();
    }
}
