using System.Globalization;
using System.Reflection;
using Evidgate.Modules.Identity.Infrastructure.Persistence;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 11.5.1-T5.2 SQL level: <c>SecurityEventReadSql.Page</c> is a deliverable of the read, so it is executed
/// here against the real schema — as the application role inside a tenant scope (00 §5.5 C3/C5) — and pinned
/// to the row shape Dapper binds, the way <see cref="ReadSqlContractTests"/> pins the tenancy statements.
/// </summary>
/// <remarks>
/// The statement is one page per call (P3). Its projection is checked by NAME, because Dapper matches a column
/// to a property case-insensitively: the unquoted aliases arrive lowercased (<c>actorjson</c>,
/// <c>detailsjson</c>), and a renamed alias would otherwise leave <c>ActorJson</c>/<c>DetailsJson</c> at their
/// <c>"{}"</c> defaults instead of failing. The keyset walk is the second half: the same
/// <c>(created_at, id)</c> ordering the API pages on, with no repeat and no gap.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class SecurityEventReadSqlContractTests
{
    private readonly PostgresTenancyFixture _fixture;

    public SecurityEventReadSqlContractTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    /// <summary>
    /// The projection, in Dapper's binding order, as the reader reports it: unquoted aliases are folded to lower
    /// case, so the statement's <c>ProjectId</c>/<c>ActorJson</c>/<c>RequestId</c>/<c>CreatedAt</c> arrive as
    /// <c>projectid</c>/<c>actorjson</c>/<c>requestid</c>/<c>createdat</c>.
    /// </summary>
    private static readonly string[] ProjectedFields =
    [
        "id", "projectid", "type", "severity", "actorjson", "detailsjson", "requestid", "createdat",
    ];

    /// <summary>The statement projects exactly the row shape, in order, and resolves its filters.</summary>
    [Fact]
    public async Task Page_ProjectsExactlyTheRowShape()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var org = Guid.CreateVersion7();
        var project = Guid.CreateVersion7();
        var at = new DateTimeOffset(2026, 2, 1, 9, 0, 0, TimeSpan.Zero);
        await SeedAsync(database, org, [
            new(Guid.CreateVersion7(), "auth_failure", "info", at, project),
            new(Guid.CreateVersion7(), "auth_failure", "warning", at.AddMinutes(1)),
        ]);

        await using var connection = new NpgsqlConnection(_fixture.AppConnectionString(database));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await SetTenantAsync(connection, transaction, org);

        (await FieldNamesAsync(connection, transaction, org))
            .ShouldBe(ProjectedFields, "SecurityEventReadSql.Page must project the row's columns in this order");

        // Every projected name binds a property of the Dapper shape (case-insensitive), or a column would be
        // silently dropped into the property's default.
        var rowType = typeof(SecurityEventPageRow);
        foreach (var field in ProjectedFields)
        {
            rowType.GetProperty(field, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
                .ShouldNotBeNull($"SecurityEventPageRow has no property for the projected column '{field}'");
        }

        // Non-vacuity: the same statement, with the row present, answers one row with the seeded values.
        var rows = await PageAsync(connection, transaction, org, limit: 10, project: project);
        rows.ShouldHaveSingleItem().Type.ShouldBe("auth_failure");
    }

    /// <summary>The statement walks <c>(created_at, id)</c> descending: pages neither repeat nor skip a row.</summary>
    [Fact]
    public async Task Page_WalksTheKeysetInOrder()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var org = Guid.CreateVersion7();
        var tie = new DateTimeOffset(2026, 3, 2, 10, 0, 0, TimeSpan.Zero);
        Seed[] seeds =
        [
            new(Guid.CreateVersion7(), "auth_failure", "info", tie.AddMinutes(3)),
            new(Guid.CreateVersion7(), "cross_tenant_probe", "critical", tie),
            new(Guid.CreateVersion7(), "secret_scanner_alert", "critical", tie),
            new(Guid.CreateVersion7(), "kill_switch_used", "info", tie.AddMinutes(-2)),
            new(Guid.CreateVersion7(), "support_access", "info", tie.AddMinutes(-4)),
        ];
        await SeedAsync(database, org, seeds);

        await using var connection = new NpgsqlConnection(_fixture.AppConnectionString(database));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await SetTenantAsync(connection, transaction, org);

        var seen = new List<(DateTimeOffset CreatedAt, Guid Id)>();
        (DateTimeOffset CreatedAt, Guid Id)? cursor = null;

        for (var page = 0; page <= seeds.Length; page++)
        {
            var rows = await PageAsync(connection, transaction, org, limit: 2, cursor: cursor);
            if (rows.Count == 0)
            {
                break;
            }

            seen.AddRange(rows.Select(row => (row.CreatedAt, row.Id)));
            cursor = (rows[^1].CreatedAt, rows[^1].Id);
        }

        seen.Count.ShouldBe(seeds.Length);
        seen.Select(row => row.Id).Distinct().Count().ShouldBe(seeds.Length);
        seen.ShouldBe([.. seen
            .OrderByDescending(row => row.CreatedAt)
            .ThenByDescending(row => row.Id.ToString("N", CultureInfo.InvariantCulture), StringComparer.Ordinal)]);
        (await PageAsync(connection, transaction, org, limit: 2, cursor: cursor)).ShouldBeEmpty();
    }

    /// <summary>The reader's column names in projection order.</summary>
    private static async Task<IReadOnlyList<string>> FieldNamesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid orgId)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SecurityEventReadSql.Page;
        Bind(command, orgId: orgId, project: null, type: null, severity: null, from: null, to: null, cursor: null, limit: 5);

        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var names = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
        }

        return names;
    }

    /// <summary>One page of the real statement, filters and cursor optional.</summary>
    private static async Task<List<(Guid Id, DateTimeOffset CreatedAt, string Type)>> PageAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid orgId,
        int limit,
        Guid? project = null,
        (DateTimeOffset CreatedAt, Guid Id)? cursor = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SecurityEventReadSql.Page;
        Bind(command, orgId: orgId, project: project, type: null, severity: null, from: null, to: null, cursor: cursor, limit: limit);

        var rows = new List<(Guid, DateTimeOffset, string)>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var id = reader.GetOrdinal("id");
        var createdAt = reader.GetOrdinal("createdat");
        var type = reader.GetOrdinal("type");

        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add((
                await reader.GetFieldValueAsync<Guid>(id, TestContext.Current.CancellationToken),
                await reader.GetFieldValueAsync<DateTimeOffset>(createdAt, TestContext.Current.CancellationToken),
                await reader.GetFieldValueAsync<string>(type, TestContext.Current.CancellationToken)));
        }

        return rows;
    }

    private static void Bind(
        NpgsqlCommand command,
        Guid orgId,
        Guid? project,
        string? type,
        string? severity,
        DateTimeOffset? from,
        DateTimeOffset? to,
        (DateTimeOffset CreatedAt, Guid Id)? cursor,
        int limit)
    {
        command.Parameters.AddWithValue("org_id", orgId);
        command.Parameters.AddWithValue("project_id", (object?)project ?? DBNull.Value);
        command.Parameters.AddWithValue("type", (object?)type ?? DBNull.Value);
        command.Parameters.AddWithValue("severity", (object?)severity ?? DBNull.Value);
        command.Parameters.AddWithValue("from", (object?)from ?? DBNull.Value);
        command.Parameters.AddWithValue("to", (object?)to ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "cursor_created_at",
            cursor is { } position ? position.CreatedAt : (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "cursor_id",
            cursor is { } id ? id.Id : (object)DBNull.Value);
        command.Parameters.AddWithValue("limit", limit);
    }

    private static async Task SetTenantAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid orgId)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "select set_config('evidgate.org_id', @org_id, true)";
        command.Parameters.AddWithValue("org_id", orgId.ToString("D", CultureInfo.InvariantCulture));
        await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>One seeded row, the columns the statement reads.</summary>
    private sealed record Seed(Guid Id, string Type, string Severity, DateTimeOffset CreatedAt, Guid? ProjectId = null);

    private async Task SeedAsync(string database, Guid org, IEnumerable<Seed> seeds)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database), org);

        foreach (var seed in seeds)
        {
            await scope.ExecuteAsync(
                """
                insert into public.security_event
                    (id, org_id, project_id, type, severity, actor, details, created_at)
                values
                    (@id, @org_id, @project_id, @type, @severity, '{"kind":"user"}'::jsonb, '{"count":1}'::jsonb, @created_at)
                """,
                ("id", seed.Id),
                ("org_id", org),
                ("project_id", (object?)seed.ProjectId ?? DBNull.Value),
                ("type", seed.Type),
                ("severity", seed.Severity),
                ("created_at", seed.CreatedAt));
        }

        await scope.CommitAsync();
    }
}
