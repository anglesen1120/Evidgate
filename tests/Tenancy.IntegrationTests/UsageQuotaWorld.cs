using System.Security.Claims;
using Evidgate.Api.OperatorIngress;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Usage.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The world the quota suites share: one shared database with an org and a project, the real Api
/// composition (the operator gate chosen by the test, the quota switch chosen by the test) for the gate
/// and the operator route, and a permissive host for the in-process raise command.
/// </summary>
/// <remarks>
/// Counter rows are seeded and read as the superuser over parameterised statements, so a test can pin
/// <c>used</c>, the two threshold stamps and a second org's counter — a period's usage is an input of a
/// gate test, never an outcome the test has to earn by metering.
/// </remarks>
internal sealed partial class UsageQuotaWorld : IAsyncDisposable
{
    /// <summary>The platform operator every audit row of the operator route must name.</summary>
    internal static readonly Guid OperatorId = Guid.CreateVersion7();

    private readonly PostgresTenancyFixture _fixture;

    private UsageQuotaWorld(
        PostgresTenancyFixture fixture,
        TenancyApiFactory api,
        TenancyApiFactory commands,
        HttpClient client,
        string database,
        Guid user,
        Guid org,
        Guid project)
    {
        _fixture = fixture;
        Api = api;
        Commands = commands;
        Client = client;
        Database = database;
        User = user;
        Org = org;
        Project = project;
    }

    /// <summary>The real Api composition the gate and the operator route run in.</summary>
    internal TenancyApiFactory Api { get; }

    /// <summary>The permissive host the in-process raise command is dispatched on.</summary>
    internal TenancyApiFactory Commands { get; }

    internal HttpClient Client { get; }

    internal string Database { get; }

    internal Guid User { get; }

    internal Guid Org { get; }

    internal Guid Project { get; }

    /// <summary>Starts one world.</summary>
    /// <param name="fixture">The assembly fixture (one container, one template).</param>
    /// <param name="gate">Which operator gate the host composes.</param>
    /// <param name="configuration">Extra configuration, e.g. the quota switch a test turns off.</param>
    /// <param name="database">A private database for a test that reads table-wide state; the shared one when null.</param>
    internal static async Task<UsageQuotaWorld> StartAsync(
        PostgresTenancyFixture fixture,
        OperatorGateMode gate = OperatorGateMode.Denied,
        IReadOnlyDictionary<string, string?>? configuration = null,
        string? database = null,
        TenancyHostPool? hosts = null)
    {
        database ??= await fixture.SharedDatabaseAsync();
        var connectionString = fixture.AppConnectionString(database);

        Action<IServiceCollection>? services = gate switch
        {
            OperatorGateMode.Permitted => collection =>
                collection.AddSingleton<IOperatorGate>(new PermitGate()),
            OperatorGateMode.Refused => collection =>
                collection.AddSingleton<IOperatorGate>(new RefusingGate()),
            _ => null,
        };

        var api = hosts?.RealResolver(database, configuration, services)
            ?? TenancyApiFactory.RealResolver(connectionString, configuration, services);
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, Unique.Slug("quota"));
        var project = await CreateProjectAsync(client, user, org, Unique.Slug("meter"));

        return new UsageQuotaWorld(
            fixture, api, hosts?.GrantAll(database) ?? TenancyApiFactory.GrantAll(connectionString), client, database, user, org, project);
    }

    /// <summary>A second org with a project, founded by its own user: the tenant no write may reach.</summary>
    internal async Task<(Guid Org, Guid Project)> AddOtherOrgAsync()
    {
        var founder = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(Client, founder, Unique.Slug("other"));
        var otherProject = await CreateProjectAsync(Client, founder, otherOrg, Unique.Slug("slip"));

        return (otherOrg, otherProject);
    }

    /// <summary>
    /// How many gate questions one test may have in flight. Each opens its own tenant scope and so its own
    /// connection, and the container answers 53300 (no slots for a non-superuser) long before a fifty-caller race has
    /// raced anything; the bound keeps the race and stays inside that budget.
    /// </summary>
    private const int GateCallConcurrency = 8;

    private static readonly SemaphoreSlim GateCalls = new(GateCallConcurrency);

    /// <summary>
    /// Asks the registered gate one question inside its own tenant scope and commits unless the test asks
    /// otherwise — the caller's transaction of the real hot path.
    /// </summary>
    /// <param name="request">The question.</param>
    /// <param name="org">The tenant to ask as, or null for this world's org.</param>
    /// <param name="commit">False leaves the caller's transaction open and rolls it back on dispose.</param>
    internal async Task<QuotaCheckResult> CheckAsync(
        QuotaCheckRequest request,
        Guid? org = null,
        bool commit = true)
    {
        var token = TestContext.Current.CancellationToken;

        await GateCalls.WaitAsync(token).ConfigureAwait(false);

        try
        {
            await using var services = Api.Services.CreateAsyncScope();
            var provider = services.ServiceProvider;
            var scopes = provider.GetRequiredService<ITenantScopeFactory>();
            await using var scope = await scopes.BeginAsync(org ?? Org, null, false, token);
            var gate = provider.GetRequiredService<IQuotaGate>();

            var result = await gate.CheckAsync(request, token).ConfigureAwait(false);
            if (commit)
            {
                await scope.CommitAsync(token).ConfigureAwait(false);
            }

            return result;
        }
        finally
        {
            GateCalls.Release();
        }
    }

    /// <summary>
    /// Asks the gate and records the metered work in the same tenant transaction, exactly as the ingest
    /// hot path does: the reservation and the ledger row commit or roll back together.
    /// </summary>
    /// <param name="request">The reservation.</param>
    /// <param name="entry">The ledger row the accepted batch writes.</param>
    internal async Task<QuotaCheckResult> CheckAndRecordAsync(QuotaCheckRequest request, UsageEntryInput entry)
    {
        var token = TestContext.Current.CancellationToken;

        await using var services = Api.Services.CreateAsyncScope();
        var provider = services.ServiceProvider;
        var scopes = provider.GetRequiredService<ITenantScopeFactory>();
        await using var scope = await scopes.BeginAsync(Org, null, false, token);
        var gate = provider.GetRequiredService<IQuotaGate>();

        var result = await gate.CheckAsync(request, token).ConfigureAwait(false);
        if (result.Decision == QuotaDecision.Block)
        {
            return result;
        }

        await provider.GetRequiredService<IUsageLedger>()
            .RecordAsync(new RecordUsageBatchRequest([entry]), token)
            .ConfigureAwait(false);
        await scope.CommitAsync(token).ConfigureAwait(false);

        return result;
    }

    /// <summary>Inserts one counter row as the owner and answers its id.</summary>
    /// <param name="seed">The row to insert.</param>
    internal async Task<Guid> SeedCounterAsync(CounterSeed seed)
    {
        await using var connection = await OpenOwnerAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            insert into public.quota_counter
                (id, org_id, project_id, period, metric, used, quota_limit, warn_ratio, warned_at, blocked_at)
            values
                (@id, @org_id, @project_id, @period, @metric, @used, @quota_limit, @warn_ratio, @warned_at, @blocked_at)
            """;
        command.Parameters.Add(new NpgsqlParameter("id", seed.Id));
        command.Parameters.Add(new NpgsqlParameter("org_id", seed.Org ?? Org));
        command.Parameters.Add(new NpgsqlParameter("project_id", (object?)seed.ProjectId ?? DBNull.Value));
        command.Parameters.Add(new NpgsqlParameter("period", seed.Period));
        command.Parameters.Add(new NpgsqlParameter("metric", seed.Metric));
        command.Parameters.Add(new NpgsqlParameter("used", seed.Used));
        command.Parameters.Add(new NpgsqlParameter("quota_limit", seed.Limit));
        command.Parameters.Add(new NpgsqlParameter("warn_ratio", seed.WarnRatio));
        command.Parameters.Add(new NpgsqlParameter("warned_at", (object?)seed.WarnedAt ?? DBNull.Value));
        command.Parameters.Add(new NpgsqlParameter("blocked_at", (object?)seed.BlockedAt ?? DBNull.Value));

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);

        return seed.Id;
    }

    /// <summary>The one counter row of a scope, or null when the scope holds none.</summary>
    /// <param name="org">The tenant, or null for this world's org.</param>
    /// <param name="project">The project, null for the tenant-level counter.</param>
    /// <param name="period">The billing month.</param>
    /// <param name="metric">The wire metric name.</param>
    internal async Task<CounterState?> CounterAsync(
        Guid? org = null,
        Guid? project = null,
        string period = "2026-10",
        string metric = "results")
    {
        await using var connection = await OpenOwnerAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select used, quota_limit, warn_ratio, warned_at, blocked_at, version
            from public.quota_counter
            where org_id = @org_id
              and coalesce(project_id, '00000000-0000-0000-0000-000000000000'::uuid)
                  = coalesce(@project_id::uuid, '00000000-0000-0000-0000-000000000000'::uuid)
              and period = @period
              and metric = @metric
            """;
        command.Parameters.Add(new NpgsqlParameter("org_id", org ?? Org));
        command.Parameters.Add(new NpgsqlParameter("project_id", (object?)project ?? DBNull.Value));
        command.Parameters.Add(new NpgsqlParameter("period", period));
        command.Parameters.Add(new NpgsqlParameter("metric", metric));

        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var token = TestContext.Current.CancellationToken;

        return new CounterState(
            reader.GetDecimal(0),
            reader.GetDecimal(1),
            reader.GetDecimal(2),
            await StampAsync(reader, 3, token).ConfigureAwait(false),
            await StampAsync(reader, 4, token).ConfigureAwait(false),
            reader.GetInt32(5));
    }

    /// <summary>A nullable instant column, read without blocking the flow.</summary>
    private static async Task<DateTimeOffset?> StampAsync(
        NpgsqlDataReader reader,
        int ordinal,
        CancellationToken cancellationToken) =>
        await reader.IsDBNullAsync(ordinal, cancellationToken).ConfigureAwait(false)
            ? null
            : await reader.GetFieldValueAsync<DateTimeOffset>(ordinal, cancellationToken).ConfigureAwait(false);

    /// <summary>How many counter rows the org holds.</summary>
    /// <param name="org">The tenant, or null for this world's org.</param>
    internal Task<long> CounterCountAsync(Guid? org = null) =>
        _fixture.ScalarAsync<long>(
            Database,
            $"select count(*) from public.quota_counter where org_id = '{(org ?? Org):D}'");

    /// <summary>One read on the world's database, for the values only it can answer, such as its own clock.</summary>
    /// <param name="sql">The read; it must return one row and one column.</param>
    internal Task<T> ScalarAsync<T>(string sql) => _fixture.ScalarAsync<T>(Database, sql);

    /// <summary>The id of one scope's counter, or null when the scope holds none.</summary>
    /// <param name="org">The tenant, or null for this world's org.</param>
    /// <param name="project">The project, null for the tenant-level counter.</param>
    /// <param name="period">The billing month.</param>
    /// <param name="metric">The wire metric name.</param>
    internal async Task<Guid?> CounterIdAsync(
        Guid? org = null,
        Guid? project = null,
        string period = "2026-10",
        string metric = "results")
    {
        var scope = $"{(project ?? Guid.Empty):D}";
        var rows = await _fixture.ColumnAsync<string>(
                Database,
                $"""
                select id::text from public.quota_counter
                where org_id = '{(org ?? Org):D}'
                  and coalesce(project_id, '00000000-0000-0000-0000-000000000000'::uuid)
                      = '{scope}'::uuid
                  and period = '{period}'
                  and metric = '{metric}'
                """)
            .ConfigureAwait(false);

        return rows.Count == 0 ? null : Guid.Parse(rows[0]);
    }

    /// <summary>How many outbox rows of one task type the org holds.</summary>
    /// <param name="taskType">The outbox task type.</param>
    /// <param name="org">The tenant, or null for this world's org.</param>
    internal Task<long> OutboxCountAsync(string taskType, Guid? org = null) =>
        _fixture.ScalarAsync<long>(
            Database,
            $"select count(*) from public.outbox where org_id = '{(org ?? Org):D}' and task_type = '{taskType}'");

    /// <summary>
    /// The org's outbox rows of one task type, as <c>task_type|aggregate_type|aggregate_id|payload</c>,
    /// oldest first: what a consumer would read.
    /// </summary>
    /// <param name="taskType">The outbox task type.</param>
    /// <param name="org">The tenant, or null for this world's org.</param>
    internal Task<IReadOnlyList<string>> OutboxRowsAsync(string taskType, Guid? org = null) =>
        _fixture.ColumnAsync<string>(
            Database,
            $"""
            select task_type || '|' || aggregate_type || '|' || aggregate_id::text || '|' || payload::text
            from public.outbox
            where org_id = '{(org ?? Org):D}' and task_type = '{taskType}'
            order by created_at, id
            """);

    /// <summary>How many audit rows one action of the org wrote.</summary>
    /// <param name="org">The tenant, or null for this world's org.</param>
    /// <param name="action">The audited action.</param>
    internal Task<long> AuditCountAsync(Guid? org, string action) =>
        _fixture.ScalarAsync<long>(
            Database,
            $"""
            select count(*) from public.audit_log
            where org_id = '{(org ?? Org):D}' and action = '{action}'
            """);

    /// <summary>The org's audit rows of one action, as <c>actor_type|actor_id|object_type|object_id|metadata</c>.</summary>
    /// <param name="org">The tenant, or null for this world's org.</param>
    /// <param name="action">The audited action.</param>
    internal Task<IReadOnlyList<string>> AuditRowsAsync(Guid? org, string action) =>
        _fixture.ColumnAsync<string>(
            Database,
            $"""
            select actor_type || '|' || actor_id || '|' || object_type || '|' || object_id::text || '|' || metadata::text
            from public.audit_log
            where org_id = '{(org ?? Org):D}' and action = '{action}'
            order by seq
            """);

    /// <summary>The statements the Api host executed that name the counter table.</summary>
    internal IReadOnlyList<string> CounterStatements() =>
        [.. Api.Statements.Snapshot().Where(statement =>
            statement.Contains("public.quota_counter", StringComparison.Ordinal))];

    /// <summary>Forgets the statements captured so far, so a test can budget one call.</summary>
    internal void ClearStatementLog() => Api.Statements.Clear();

    /// <summary>
    /// Opens one app-role tenant transaction the test drives by hand: the caller's side of a race with the
    /// gate, committed or rolled back when the test says so.
    /// </summary>
    internal Task<TenantScope> BeginAppScopeAsync() =>
        TenantScope.BeginAsync(_fixture.AppConnectionString(Database), Org);

    /// <summary>
    /// Waits until some backend is blocked by <paramref name="blockerPid"/>, the observable condition of a
    /// statement queued behind that transaction's row lock. Polling with a deadline, never a fixed sleep.
    /// </summary>
    /// <param name="blockerPid">The backend whose lock another statement waits on.</param>
    internal async Task WaitForBlockedByAsync(int blockerPid)
    {
        for (var attempt = 0; attempt < 400; attempt++)
        {
            var blocked = await _fixture.ScalarAsync<long>(
                    Database,
                    $"select count(*) from pg_stat_activity where {blockerPid} = any(pg_blocking_pids(pid))")
                .ConfigureAwait(false);

            if (blocked > 0)
            {
                return;
            }

            await Task.Delay(25, TestContext.Current.CancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"no statement waited on backend {blockerPid}");
    }

    /// <summary>The operator route of one org's quota raise.</summary>
    /// <param name="org">The org the route names, or null for this world's org.</param>
    internal string RaiseUrl(Guid? org = null) =>
        $"/internal/operator/billing/orgs/{(org ?? Org):D}/quota-raise";

    /// <summary>Posts one raise to the operator route with no tenant caller headers.</summary>
    /// <param name="body">The request body.</param>
    /// <param name="org">The org the route names, or null for this world's org.</param>
    internal Task<ApiAnswer> PostRaiseAsync(object body, Guid? org = null) =>
        Client.SendAsync(HttpMethod.Post, RaiseUrl(org), _ => { }, body);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Api.DisposeAsync();
        await Commands.DisposeAsync();
    }

    private async Task<NpgsqlConnection> OpenOwnerAsync()
    {
        var connection = new NpgsqlConnection(_fixture.OwnerConnectionString(Database));
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);

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

/// <summary>One counter row to seed as the owner.</summary>
/// <param name="Id">The row's id.</param>
/// <param name="ProjectId">The project, or null for the tenant-level counter.</param>
/// <param name="Period">The billing month.</param>
/// <param name="Metric">The wire metric name.</param>
/// <param name="Used">The usage the period already holds.</param>
/// <param name="Limit">The scope's limit.</param>
/// <param name="WarnRatio">The fraction of the limit at which the scope warns.</param>
/// <param name="WarnedAt">The warning stamp, if the scope already warned.</param>
/// <param name="BlockedAt">The refusal stamp, if the scope is already blocked.</param>
/// <param name="Org">The tenant, or null for this world's org.</param>
internal sealed record CounterSeed(
    Guid Id,
    Guid? ProjectId,
    string Period,
    string Metric,
    decimal Used,
    decimal Limit,
    decimal WarnRatio = 0.8m,
    DateTimeOffset? WarnedAt = null,
    DateTimeOffset? BlockedAt = null,
    Guid? Org = null);

/// <summary>A stored counter as the owner reads it.</summary>
/// <param name="Used">The usage the period holds.</param>
/// <param name="Limit">The scope's limit.</param>
/// <param name="WarnRatio">The warning line.</param>
/// <param name="WarnedAt">The warning stamp.</param>
/// <param name="BlockedAt">The refusal stamp.</param>
/// <param name="Version">The row's concurrency token, moved by every write.</param>
internal sealed record CounterState(
    decimal Used,
    decimal Limit,
    decimal WarnRatio,
    DateTimeOffset? WarnedAt,
    DateTimeOffset? BlockedAt,
    int Version);
