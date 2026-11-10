using System.Net;
using System.Security.Claims;
using Evidgate.Api.OperatorIngress;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The world the billing operator's read suites share: one private database with an org and a project,
/// one host whose operator gate is chosen by the test, and the seed helpers of the three tables the
/// reads answer from (<c>usage_ledger_entry</c>, <c>usage_rollup</c>, <c>usage_duplicate_attempt</c>).
/// </summary>
/// <remarks>
/// Seeds run as the superuser over parameterised statements, so a test can write rows for a second org
/// (row security hides them from the read under test) and can pin <c>created_at</c> — the keyset's first
/// member — instead of hoping two inserts share an instant.
/// </remarks>
internal sealed partial class UsageReadsWorld : IAsyncDisposable
{
    /// <summary>The platform operator every audit row of these reads must name.</summary>
    internal static readonly Guid OperatorId = Guid.CreateVersion7();

    private readonly PostgresTenancyFixture _fixture;

    private UsageReadsWorld(
        PostgresTenancyFixture fixture,
        TenancyApiFactory api,
        WebApplicationFactory<Program> host,
        HttpClient client,
        DedicatedDatabase database,
        Guid user,
        Guid org,
        Guid project)
    {
        _fixture = fixture;
        Api = api;
        Host = host;
        Client = client;
        Database = database;
        User = user;
        Org = org;
        Project = project;
    }

    internal TenancyApiFactory Api { get; }

    internal WebApplicationFactory<Program> Host { get; }

    internal HttpClient Client { get; }

    internal DedicatedDatabase Database { get; }

    internal Guid User { get; }

    internal Guid Org { get; }

    internal Guid Project { get; }

    /// <summary>Starts one world with the operator gate the suite needs, over the clock the host reports time from.</summary>
    /// <param name="fixture">The assembly fixture (one container, one template).</param>
    /// <param name="gate">Which gate the host composes.</param>
    /// <param name="clock">The host's own clock, for the billing reads that name a month from it.</param>
    internal static Task<UsageReadsWorld> StartAsync(
        PostgresTenancyFixture fixture,
        OperatorGateMode gate,
        TimeProvider? clock = null) =>
        fixture.StartDedicatedAsync(async database =>
        {
            var api = TenancyApiFactory.RealResolver(
                fixture.AppConnectionString(database),
                services: clock is null ? null : services => services.AddSingleton<TimeProvider>(clock));

            WebApplicationFactory<Program> host = gate switch
            {
                OperatorGateMode.Permitted => api.WithWebHostBuilder(builder => builder.ConfigureTestServices(
                    services => services.AddSingleton<IOperatorGate>(new PermitGate()))),
                OperatorGateMode.Refused => api.WithWebHostBuilder(builder => builder.ConfigureTestServices(
                    services => services.AddSingleton<IOperatorGate>(new RefusingGate()))),
                _ => api,
            };

            var client = host.CreateClient();
            var user = Guid.CreateVersion7();
            var org = await CreateOrgAsync(client, user, Unique.Slug("acme"));
            var project = await CreateProjectAsync(client, user, org, Unique.Slug("checkout"));

            return new UsageReadsWorld(fixture, api, host, client, database, user, org, project);
        });

    /// <summary>Sends one request with no caller headers: these routes read the operator, not a tenant.</summary>
    /// <param name="url">The route.</param>
    internal Task<ApiAnswer> GetAsync(string url) =>
        Client.SendAsync(HttpMethod.Get, url, _ => { });

    /// <summary>The url of the ledger page of this world's org.</summary>
    /// <param name="query">The query string, without a leading <c>?</c>.</param>
    internal string LedgerUrl(string query = "") =>
        $"/internal/operator/orgs/{Org:D}/usage-ledger{(query.Length == 0 ? string.Empty : "?" + query)}";

    /// <summary>The url of the reconciliation of this world's org.</summary>
    /// <param name="query">The query string, without a leading <c>?</c>.</param>
    internal string ReconciliationUrl(string query = "") =>
        $"/internal/operator/orgs/{Org:D}/usage-reconciliation{(query.Length == 0 ? string.Empty : "?" + query)}";

    /// <summary>A second org with a project, founded by its own user: the tenant the read must never reach.</summary>
    internal async Task<(Guid Org, Guid Project)> AddOtherOrgAsync()
    {
        var founder = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(Client, founder, Unique.Slug("other"));
        var otherProject = await CreateProjectAsync(Client, founder, otherOrg, Unique.Slug("other"));

        return (otherOrg, otherProject);
    }

    /// <summary>
    /// Inserts many ledger rows of one period without a round trip each: the shape a large period takes,
    /// where the read must stay one aggregate statement whatever the row count.
    /// </summary>
    /// <param name="count">Rows to insert.</param>
    /// <param name="period">Their period.</param>
    /// <param name="metric">Their metric.</param>
    /// <param name="quantityEach">The quantity each row carries.</param>
    internal async Task SeedManyLedgerRowsAsync(int count, string period, string metric, decimal quantityEach)
    {
        await using var connection = await OpenOwnerAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            insert into public.usage_ledger_entry
                (id, org_id, project_id, metric, quantity, unit, period, billing_key, occurred_at)
            select gen_random_uuid(), @org_id, @project_id, @metric, @quantity, 'batch', @period,
                   'bulk:' || @period || ':' || @metric || ':' || n, now()
              from generate_series(1, @count) as n
            """;
        command.Parameters.Add(new NpgsqlParameter("org_id", Org));
        command.Parameters.Add(new NpgsqlParameter("project_id", Project));
        command.Parameters.Add(new NpgsqlParameter("metric", metric));
        command.Parameters.Add(new NpgsqlParameter("quantity", quantityEach));
        command.Parameters.Add(new NpgsqlParameter("period", period));
        command.Parameters.Add(new NpgsqlParameter("count", count));

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Inserts one global rate-card row, so a ledger row may name it as its <c>price_ref</c>: the ledger's key
    /// accepts a price its org may see, and a global price is visible to every org.
    /// </summary>
    /// <param name="metric">The priced metric.</param>
    /// <returns>The price's id, which the ledger row stores.</returns>
    internal async Task<Guid> SeedGlobalPriceAsync(string metric)
    {
        var id = Guid.CreateVersion7();
        await using var connection = await OpenOwnerAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            insert into public.usage_price
                (id, org_id, metric, unit_price, currency, effective_from, rate_card_version)
            values (@id, null, @metric, 0.5, 'USD', '2026-01-01T00:00:00Z', 'card-read')
            """;
        command.Parameters.Add(new NpgsqlParameter("id", id));
        command.Parameters.Add(new NpgsqlParameter("metric", metric));

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        return id;
    }

    /// <summary>Inserts the given roll-up rows.</summary>
    /// <param name="rows">The rows to insert.</param>
    internal async Task SeedRollupAsync(params RollupSeed[] rows)
    {
        await using var connection = await OpenOwnerAsync();

        foreach (var row in rows)
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                insert into public.usage_rollup
                    (id, org_id, project_id, period, metric, total_quantity, total_amount, currency, row_count,
                     gap_flag, computed_at)
                values
                    (@id, @org_id, @project_id, @period, @metric, @total_quantity, @total_amount, @currency,
                     @row_count, @gap_flag, @computed_at)
                """;
            command.Parameters.Add(new NpgsqlParameter("id", row.Id));
            command.Parameters.Add(new NpgsqlParameter("org_id", row.Org ?? Org));
            command.Parameters.Add(new NpgsqlParameter("project_id", (object?)row.ProjectId ?? DBNull.Value));
            command.Parameters.Add(new NpgsqlParameter("period", row.Period));
            command.Parameters.Add(new NpgsqlParameter("metric", row.Metric));
            command.Parameters.Add(new NpgsqlParameter("total_quantity", row.TotalQuantity));
            command.Parameters.Add(new NpgsqlParameter("total_amount", (object?)row.TotalAmount ?? DBNull.Value));
            command.Parameters.Add(new NpgsqlParameter("currency", (object?)row.Currency ?? DBNull.Value));
            command.Parameters.Add(new NpgsqlParameter("row_count", row.RowCount));
            command.Parameters.Add(new NpgsqlParameter("gap_flag", row.GapFlag));
            command.Parameters.Add(new NpgsqlParameter("computed_at", row.ComputedAt));

            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// Seeds <paramref name="count"/> repeated keys of one period: a ledger row each (the join the duplicate
    /// read uses) and an attempt row each. Two statements, so the count is the only thing that varies.
    /// </summary>
    /// <param name="count">How many keys.</param>
    /// <param name="period">The period the keys belong to.</param>
    internal async Task SeedRepeatedKeysAsync(int count, string period)
    {
        await using var connection = await OpenOwnerAsync();

        foreach (var statement in new[] { LedgerForRepeatedKeys, AttemptsForRepeatedKeys })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = statement;
            command.Parameters.Add(new NpgsqlParameter("org_id", Org));
            command.Parameters.Add(new NpgsqlParameter("period", period));
            command.Parameters.Add(new NpgsqlParameter("count", count));

            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>The ledger rows behind the repeated keys, so the duplicate read joins them to the period.</summary>
    private const string LedgerForRepeatedKeys =
        """
        insert into public.usage_ledger_entry
            (id, org_id, project_id, metric, quantity, unit, period, billing_key, occurred_at)
        select gen_random_uuid(), @org_id, null, 'results', 1, 'batch', @period, 'bulk-key:' || n, now()
          from generate_series(1, @count) as n
        """;

    /// <summary>One attempt row per repeated key.</summary>
    private const string AttemptsForRepeatedKeys =
        """
        insert into public.usage_duplicate_attempt
            (id, org_id, billing_key, attempts, first_seen_at, last_seen_at)
        select gen_random_uuid(), @org_id, 'bulk-key:' || n, 2, now(), now()
          from generate_series(1, @count) as n
        """;

    /// <summary>Inserts the given duplicate-attempt rows.</summary>
    /// <param name="rows">The rows to insert.</param>
    internal async Task SeedDuplicateAttemptsAsync(params DuplicateSeed[] rows)
    {
        await using var connection = await OpenOwnerAsync();

        foreach (var row in rows)
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                insert into public.usage_duplicate_attempt
                    (id, org_id, billing_key, attempts, first_seen_at, last_seen_at)
                values
                    (@id, @org_id, @billing_key, @attempts, @first_seen_at, @last_seen_at)
                """;
            command.Parameters.Add(new NpgsqlParameter("id", Guid.CreateVersion7()));
            command.Parameters.Add(new NpgsqlParameter("org_id", row.Org ?? Org));
            command.Parameters.Add(new NpgsqlParameter("billing_key", row.BillingKey));
            command.Parameters.Add(new NpgsqlParameter("attempts", row.Attempts));
            command.Parameters.Add(new NpgsqlParameter("first_seen_at", row.FirstSeenAt));
            command.Parameters.Add(new NpgsqlParameter("last_seen_at", row.LastSeenAt));

            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>How many audit rows of <paramref name="action"/> this world's org carries.</summary>
    /// <param name="action">The audited action.</param>
    internal async Task<long> AuditCountAsync(string action)
    {
        await using var connection = await OpenOwnerAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "select count(*) from public.audit_log where org_id = @org_id and action = @action";
        command.Parameters.Add(new NpgsqlParameter("org_id", Org));
        command.Parameters.Add(new NpgsqlParameter("action", action));

        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    /// <summary>How many audit rows one org carries for an action, for a read that named another org.</summary>
    /// <param name="org">The org the audit row would be chained to.</param>
    /// <param name="action">The audited action.</param>
    internal async Task<long> AuditCountForAsync(Guid org, string action)
    {
        await using var connection = await OpenOwnerAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "select count(*) from public.audit_log where org_id = @org_id and action = @action";
        command.Parameters.Add(new NpgsqlParameter("org_id", org));
        command.Parameters.Add(new NpgsqlParameter("action", action));

        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    /// <summary>
    /// The org's audit rows of one action as <c>actor_type|actor_id|object_type|object_id|metadata</c>, in
    /// chain order, so a test can assert the actor and the recorded query parameters.
    /// </summary>
    /// <param name="action">The audited action.</param>
    internal async Task<IReadOnlyList<string>> AuditRowsAsync(string action)
    {
        await using var connection = await OpenOwnerAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select actor_type || '|' || actor_id || '|' || object_type || '|' || object_id || '|' || metadata::text
              from public.audit_log
             where org_id = @org_id and action = @action
             order by seq
            """;
        command.Parameters.Add(new NpgsqlParameter("org_id", Org));
        command.Parameters.Add(new NpgsqlParameter("action", action));

        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

    /// <summary>The statements the host executed that name any of <paramref name="tables"/>.</summary>
    /// <param name="tables">The table names, as they appear in the SQL text.</param>
    internal IReadOnlyList<string> StatementsTouching(params string[] tables) =>
        [.. Api.Statements.Snapshot().Where(statement =>
            tables.Any(table => statement.Contains(table, StringComparison.Ordinal)))];

    /// <summary>The ids of a page response, in page order.</summary>
    /// <param name="answer">The response.</param>
    internal static IReadOnlyList<Guid> ItemIds(ApiAnswer answer) =>
        [.. answer.Body.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid())];

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        try
        {
            if (!ReferenceEquals(Host, Api))
            {
                await Host.DisposeAsync();
            }

            await Api.DisposeAsync();
        }
        finally
        {
            await Database.DisposeAsync();
        }
    }

    private static void EmptyCaller(System.Net.Http.Headers.HttpRequestHeaders headers)
    {
        // These routes carry no caller headers: the operator is the gate's, not a tenant header's.
    }

    /// <summary>One owner connection of this world's database: the seed path, which row security must not narrow.</summary>
    private async Task<NpgsqlConnection> OpenOwnerAsync() =>
        await OpenOwnerAsync(TestContext.Current.CancellationToken);

    /// <summary>The same connection opened under a caller-chosen token, for a step that must survive cancellation.</summary>
    private async Task<NpgsqlConnection> OpenOwnerAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(_fixture.OwnerConnectionString(Database));
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    /// <summary>A gate that permits and stamps the platform operator's principal.</summary>
    private sealed class PermitGate : IOperatorGate
    {
        public ValueTask<bool> IsOperatorAsync(HttpContext httpContext)
        {
            httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, OperatorId.ToString("D"))],
                authenticationType: "operator-test"));

            return ValueTask.FromResult(true);
        }
    }

    /// <summary>A gate that refuses an authenticated caller: the 403 half of the same filter.</summary>
    private sealed class RefusingGate : IOperatorGate
    {
        public ValueTask<bool> IsOperatorAsync(HttpContext httpContext)
        {
            httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, OperatorId.ToString("D"))],
                authenticationType: "operator-test"));

            return ValueTask.FromResult(false);
        }
    }
}

/// <summary>Which operator gate a world's host composes.</summary>
internal enum OperatorGateMode
{
    /// <summary>The host's fail-closed default (<c>DenyAllOperatorGate</c>).</summary>
    Denied,

    /// <summary>A gate that permits and stamps the operator principal.</summary>
    Permitted,

    /// <summary>A gate that refuses an authenticated caller (the 403 path).</summary>
    Refused,
}

/// <summary>One ledger row to seed: every column the read returns, and the instant the keyset orders by.</summary>
internal sealed record LedgerSeed(
    Guid Id,
    Guid? ProjectId,
    string Metric,
    decimal Quantity,
    string Period,
    string BillingKey,
    DateTimeOffset CreatedAt,
    DateTimeOffset? OccurredAt = null,
    Guid? AdjustmentOf = null,
    string? Reason = null,
    string Unit = "batch",
    Guid? Org = null,
    Guid? PriceRef = null,
    decimal? Amount = null,
    string? Currency = null,
    string? OriginalPeriod = null);

/// <summary>One roll-up row to seed.</summary>
internal sealed record RollupSeed(
    Guid Id,
    Guid? ProjectId,
    string Period,
    string Metric,
    decimal TotalQuantity,
    int RowCount,
    DateTimeOffset ComputedAt,
    bool GapFlag = false,
    Guid? Org = null,
    decimal? TotalAmount = null,
    string? Currency = null);

/// <summary>One duplicate-attempt row to seed.</summary>
internal sealed record DuplicateSeed(
    string BillingKey,
    int Attempts,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    Guid? Org = null);
