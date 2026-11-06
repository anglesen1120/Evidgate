using System.Data.Common;
using Npgsql;
using NpgsqlTypes;

namespace Evidgate.Modules.Usage.Infrastructure.Persistence;

/// <summary>
/// The project-visibility read shared by every write path that names a project: a foreign key
/// violation on <c>(org_id, project_id)</c> would abort the caller's transaction, so an unknown or
/// other-org project is refused before the statement runs instead. Under row security another tenant's
/// project is simply absent, which is why unknown and foreign ids share one answer.
/// </summary>
internal static class UsageProjectVisibility
{
    /// <summary>Takes no row lock: the insert's own key-share lock is enough, and a project is soft-deleted.</summary>
    private const string Sql = """
        select id from public.project
        where org_id = @org_id and id = any(@project_ids)
        """;

    /// <summary>The ids of <paramref name="projectIds"/> visible to <paramref name="orgId"/>; empty for no ids.</summary>
    /// <param name="connection">The caller's tenant-bound connection.</param>
    /// <param name="transaction">The caller's tenant-bound transaction.</param>
    /// <param name="orgId">The tenant of the open scope (never a value from the request).</param>
    /// <param name="projectIds">The projects to resolve.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal static async Task<IReadOnlySet<Guid>> VisibleAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid orgId,
        IReadOnlyCollection<Guid> projectIds,
        CancellationToken cancellationToken)
    {
        if (projectIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = Sql;
        command.Parameters.Add(new NpgsqlParameter("org_id", NpgsqlDbType.Uuid) { Value = orgId });
        command.Parameters.Add(
            new NpgsqlParameter("project_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = projectIds.ToArray() });

        var visible = new HashSet<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            visible.Add(reader.GetGuid(0));
        }

        return visible;
    }
}
