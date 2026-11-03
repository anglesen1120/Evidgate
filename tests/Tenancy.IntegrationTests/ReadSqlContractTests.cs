using System.Globalization;
using Evidgate.Modules.Tenancy.Contracts;
using Evidgate.Modules.Tenancy.Infrastructure.Persistence;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// QA additions for 02.1.1-T1.3: the module's Dapper read SQL
/// (<c>TenancyReadSql</c>) is a deliverable of this task, so it is executed here
/// against the real schema — as the application role inside a tenant scope
/// (00 §5.5 C3/C5) — and pinned to the DTO it feeds. The author's suite pins the
/// DTO against the table, but nothing executed the three statements; a column
/// renamed in the SQL or the migration would otherwise reach production as a DTO
/// field silently left at its default.
/// </summary>
[Trait("Category", "Docker")]
public sealed class ReadSqlContractTests
{
    private readonly PostgresTenancyFixture _fixture;

    public ReadSqlContractTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    /// <summary>
    /// Each statement returns exactly the DTO's fields, in snake_case, in the DTO
    /// constructor's order and no others — Dapper matches a positional record's
    /// constructor parameter to the column at the same position, so a reordered
    /// column is a silently defaulted field (M1W3-02).
    /// </summary>
    [Fact]
    public async Task ReadStatementsProjectExactlyTheDtoColumns()
    {
        var (database, orgId, projectId) = await SeedAsync(projectCount: 1, withTimestamps: false);

        await using var connection = new NpgsqlConnection(_fixture.AppConnectionString(database));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await SetTenantAsync(connection, transaction, orgId);

        (await FieldNamesAsync(
                connection,
                transaction,
                TenancyReadSql.OrganizationById,
                ("org_id", orgId)))
            .ShouldBe(DtoColumnOrder<OrganizationDto>());

        (await FieldNamesAsync(
                connection,
                transaction,
                TenancyReadSql.ProjectById,
                ("project_id", projectId)))
            .ShouldBe(DtoColumnOrder<ProjectDto>());

        // The list query is two statements (M1W3-F4): the first page carries no
        // cursor predicate, the next page the row comparison. Both project the
        // same columns, and the order is what Dapper matches a positional-record
        // constructor on.
        (await FieldNamesAsync(
                connection,
                transaction,
                TenancyReadSql.ProjectsFirstPage,
                ("org_id", orgId),
                ("status", "active"),
                ("limit", 20)))
            .ShouldBe(DtoColumnOrder<ProjectDto>());

        var cursor = await FirstProjectCursorAsync(connection, transaction, orgId);

        (await FieldNamesAsync(
                connection,
                transaction,
                TenancyReadSql.ProjectsNextPage,
                ("org_id", orgId),
                ("status", "active"),
                ("cursor_created_at", cursor.CreatedAt),
                ("cursor_id", cursor.Id),
                ("limit", 20)))
            .ShouldBe(DtoColumnOrder<ProjectDto>());
    }

    /// <summary>
    /// The keyset walk of <c>GET /v1/orgs/{o}/projects</c> (00 §9.1, C5): the first
    /// page comes from the cursor-less statement (M1W3-F4) and every following page
    /// from the row-comparison one, in <c>(created_at, id)</c> order, with no row
    /// repeated and no row skipped.
    /// </summary>
    [Fact]
    public async Task ProjectsPageWalksTheKeysetInOrder()
    {
        const int Count = 5;
        var (database, orgId, _) = await SeedAsync(Count, withTimestamps: true);

        await using var connection = new NpgsqlConnection(_fixture.AppConnectionString(database));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await SetTenantAsync(connection, transaction, orgId);

        var seen = new List<(Guid Id, DateTime CreatedAt)>();
        (Guid Id, DateTime CreatedAt)? cursor = null;

        for (var page = 0; page <= Count; page++)
        {
            var rows = await PageAsync(connection, transaction, orgId, cursor, limit: 2);
            seen.AddRange(rows);

            if (rows.Count == 0)
            {
                break;
            }

            cursor = rows[^1];
        }

        seen.Count.ShouldBe(Count);
        seen.Select(row => row.Id).Distinct().Count().ShouldBe(Count);
        seen.Select(row => (row.CreatedAt, row.Id))
            .ShouldBe(seen.Select(row => (row.CreatedAt, row.Id)).OrderBy(pair => pair.CreatedAt).ThenBy(pair => pair.Id));

        (await PageAsync(connection, transaction, orgId, cursor, limit: 2)).ShouldBeEmpty();
    }

    /// <summary>
    /// One page: <see cref="TenancyReadSql.ProjectsFirstPage"/> while there is no
    /// cursor — the statement the handler calls for page one — and
    /// <see cref="TenancyReadSql.ProjectsNextPage"/> after it.
    /// </summary>
    private static async Task<List<(Guid Id, DateTime CreatedAt)>> PageAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid orgId,
        (Guid Id, DateTime CreatedAt)? cursor,
        int limit)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = cursor is null
            ? TenancyReadSql.ProjectsFirstPage
            : TenancyReadSql.ProjectsNextPage;

        command.Parameters.AddWithValue("org_id", orgId);
        command.Parameters.AddWithValue("status", "active");
        command.Parameters.AddWithValue("limit", limit);

        if (cursor is { } value)
        {
            // The next page takes the cursor as required parameters: there is no
            // NULL sentinel to bind.
            command.Parameters.AddWithValue("cursor_created_at", value.CreatedAt);
            command.Parameters.AddWithValue("cursor_id", value.Id);
        }

        var rows = new List<(Guid Id, DateTime CreatedAt)>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var id = reader.GetOrdinal("id");
        var createdAt = reader.GetOrdinal("created_at");

        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add((
                await reader.GetFieldValueAsync<Guid>(id, TestContext.Current.CancellationToken),
                await reader.GetFieldValueAsync<DateTime>(createdAt, TestContext.Current.CancellationToken)));
        }

        return rows;
    }

    /// <summary>The first project of the org, as the cursor the next page starts after.</summary>
    private static async Task<(Guid Id, DateTime CreatedAt)> FirstProjectCursorAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid orgId)
    {
        var rows = await PageAsync(connection, transaction, orgId, cursor: null, limit: 1);
        rows.ShouldNotBeEmpty();
        return rows[0];
    }

    /// <summary>
    /// The reader's column names in projection order — the order Dapper matches the
    /// DTO constructor's parameters against.
    /// </summary>
    private static async Task<IReadOnlyList<string>> FieldNamesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var names = Enumerable.Range(0, reader.FieldCount)
            .Select(reader.GetName)
            .ToList();

        // Consume the result: a statement that fails after the first row must fail here.
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
        }

        return names;
    }

    /// <summary>
    /// The DTO's column names, snake_case (00 §7), in constructor order: what the
    /// read SQL has to project, in that order.
    /// </summary>
    private static List<string> DtoColumnOrder<T>() =>
        typeof(T)
            .GetConstructors()
            .ShouldHaveSingleItem()
            .GetParameters()
            .Select(parameter => DtoContractTests.SnakeCase.PropertyNamingPolicy!.ConvertName(parameter.Name!))
            .ToList();

    private static async Task SetTenantAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid orgId)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "select set_config('evidgate.org_id', @org_id, true)";
        command.Parameters.AddWithValue("org_id", orgId.ToString("D", CultureInfo.InvariantCulture));
        await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }

    private async Task<(string Database, Guid OrgId, Guid ProjectId)> SeedAsync(
        int projectCount,
        bool withTimestamps)
    {
        // One shared database: the org and its projects are the test's own tenant, so the reads stay isolated by RLS.
        var database = await _fixture.SharedDatabaseAsync();
        var orgId = Guid.CreateVersion7();
        var projectId = Guid.CreateVersion7();

        await using var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database));
        await scope.ExecuteAsync(
            "insert into public.organization (id, name, slug) values (@id, @name, @slug)",
            ("id", orgId),
            ("name", "Acme"),
            ("slug", Unique.Slug("acme")));

        for (var index = 0; index < projectCount; index++)
        {
            var id = index == 0 ? projectId : Guid.CreateVersion7();
            var slug = Unique.Slug("project-" + index.ToString(CultureInfo.InvariantCulture));

            if (withTimestamps)
            {
                await scope.ExecuteAsync(
                    "insert into public.project (id, org_id, name, slug, created_at, updated_at) "
                    + "values (@id, @org_id, @name, @slug, @created_at, @created_at)",
                    ("id", id),
                    ("org_id", orgId),
                    ("name", "Project " + index.ToString(CultureInfo.InvariantCulture)),
                    ("slug", slug),
                    ("created_at", new DateTime(2026, 1, 1, 0, 0, index, DateTimeKind.Utc)));
            }
            else
            {
                await scope.ExecuteAsync(
                    "insert into public.project (id, org_id, name, slug) values (@id, @org_id, @name, @slug)",
                    ("id", id),
                    ("org_id", orgId),
                    ("name", "Project " + index.ToString(CultureInfo.InvariantCulture)),
                    ("slug", slug));
            }
        }

        await scope.CommitAsync();

        return (database, orgId, projectId);
    }
}
