using System.Globalization;
using Npgsql;
using Shouldly;
using Xunit;

namespace Persistence.IntegrationTests;

/// <summary>
/// The row arrangement and catalogue reads the commit-registration schema tests share: the two orgs, the
/// three projects, one row per table, and the helpers that capture a refusal. It exists so the test class
/// stays under the size limit without either duplicating the seeding between its cases or hiding it in the
/// production code.
/// </summary>
internal static class GitSchemaRows
{
    internal static readonly Guid OrgA = Guid.Parse("11111111-1111-7111-8111-111111111111");

    internal static readonly Guid OrgB = Guid.Parse("22222222-2222-7222-8222-222222222222");

    internal static readonly string[] Tables = ["git_repo_binding", "webhook_delivery", "catalog_sync"];

    internal const string InsertSyncSql =
        "insert into public.catalog_sync (id, org_id, project_id, binding_id, commit_sha) "
        + "values (@id, @org_id, @project_id, @binding_id, @commit_sha)";

    private static long _number = 1000;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    internal static string Digest(char fill) => "sha256:" + new string(fill, 64);

    internal static string NewSha() =>
        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..24];

    internal static long NewNumber() => Interlocked.Increment(ref _number);

    internal static async Task<NpgsqlConnection> OpenOwnerAsync(string connectionString)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Token);
        return connection;
    }

    /// <summary>Two orgs and three projects: one per org plus an org-A sibling that repeats a project slug.</summary>
    internal static async Task<(Guid ProjectA, Guid ProjectSibling, Guid ProjectB)> SeedWorldAsync(
        NpgsqlConnection connection)
    {
        foreach (var (id, name, slug) in new[] { (OrgA, "Acme", "acme"), (OrgB, "Globex", "globex") })
        {
            await ExecuteAsync(
                connection,
                "insert into public.organization (id, name, slug) values (@id, @name, @slug)",
                ("id", id),
                ("name", name),
                ("slug", slug));
        }

        var projects = (ProjectA: Guid.CreateVersion7(), ProjectSibling: Guid.CreateVersion7(), ProjectB: Guid.CreateVersion7());
        foreach (var (id, org, slug) in new[]
                 {
                     (projects.ProjectA, OrgA, "checkout"),
                     (projects.ProjectSibling, OrgA, "cart"),
                     (projects.ProjectB, OrgB, "checkout"),
                 })
        {
            await ExecuteAsync(
                connection,
                "insert into public.project (id, org_id, name, slug) values (@id, @org_id, @name, @slug)",
                ("id", id),
                ("org_id", org),
                ("name", slug),
                ("slug", slug));
        }

        return projects;
    }

    internal static async Task<Guid> InsertBindingAsync(
        NpgsqlConnection connection,
        Guid org,
        Guid project,
        string? repositoryId = null)
    {
        var id = Guid.CreateVersion7();
        await ExecuteAsync(
            connection,
            "insert into public.git_repo_binding (id, org_id, project_id, installation_id, repository_id, webhook_secret_ref) "
            + "values (@id, @org_id, @project_id, @installation_id, @repository_id, 'secret:probe')",
            ("id", id),
            ("org_id", org),
            ("project_id", project),
            ("installation_id", NewNumber().ToString(CultureInfo.InvariantCulture)),
            ("repository_id", repositoryId ?? NewNumber().ToString(CultureInfo.InvariantCulture)));
        return id;
    }

    internal static async Task<Guid> InsertSyncAsync(
        NpgsqlConnection connection,
        Guid org,
        Guid project,
        Guid binding,
        string commit)
    {
        var id = Guid.CreateVersion7();
        await ExecuteAsync(
            connection,
            InsertSyncSql,
            ("id", id),
            ("org_id", org),
            ("project_id", project),
            ("binding_id", binding),
            ("commit_sha", commit));
        return id;
    }

    internal static async Task<Guid> InsertDeliveryAsync(NpgsqlConnection connection, Guid org, string deliveryId)
    {
        var id = Guid.CreateVersion7();
        await ExecuteAsync(
            connection,
            "insert into public.webhook_delivery (id, org_id, provider, delivery_id, event, payload_hash) "
            + "values (@id, @org_id, 'github', @delivery_id, 'push', @payload_hash)",
            ("id", id),
            ("org_id", org),
            ("delivery_id", deliveryId),
            ("payload_hash", Digest('a')));
        return id;
    }

    internal static async Task<List<(string Table, bool Enabled, bool Forced)>> ReadFlagsAsync(
        NpgsqlConnection connection)
    {
        await using var command = CreateCommand(
            connection,
            """
            select c.relname, c.relrowsecurity, c.relforcerowsecurity
              from pg_class c
              join pg_namespace n on n.oid = c.relnamespace
             where n.nspname = 'public' and c.relname = any(@tables)
             order by 1
            """,
            ("tables", Tables));

        var rows = new List<(string, bool, bool)>();
        await using var reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            rows.Add((reader.GetString(0), reader.GetBoolean(1), reader.GetBoolean(2)));
        }

        return rows;
    }

    internal static async Task<List<(string Table, string Policy, string Roles, string Qual)>> ReadPoliciesAsync(
        NpgsqlConnection connection)
    {
        await using var command = CreateCommand(
            connection,
            """
            select tablename, policyname, array_to_string(roles, ','), coalesce(qual, '')
              from pg_policies
             where schemaname = 'public' and tablename = any(@tables)
             order by 1, 2
            """,
            ("tables", Tables));

        var rows = new List<(string, string, string, string)>();
        await using var reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }

        return rows;
    }

    internal static async Task<int> ExecuteAsync(
        NpgsqlConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = CreateCommand(connection, sql, parameters);
        return await command.ExecuteNonQueryAsync(Token);
    }

    internal static async Task<long> CountAsync(NpgsqlConnection connection, string table, Guid id)
    {
        await using var command = CreateCommand(connection, $"select count(*) from {table} where id = @id", ("id", id));
        return (long)(await command.ExecuteScalarAsync(Token))!;
    }

    internal static async Task<PostgresException> ErrorAsync(
        NpgsqlConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = CreateCommand(connection, sql, parameters);
        return await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Token));
    }

    internal static async Task AssertRefusedAsync(
        NpgsqlConnection connection,
        string sql,
        string sqlState,
        string constraint,
        params (string Name, object Value)[] parameters)
    {
        var error = await ErrorAsync(connection, sql, parameters);
        error.SqlState.ShouldBe(sqlState, error.MessageText);
        error.ConstraintName.ShouldBe(constraint);
    }

    private static NpgsqlCommand CreateCommand(
        NpgsqlConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return command;
    }
}
