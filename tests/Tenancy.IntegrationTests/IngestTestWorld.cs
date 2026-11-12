using System.Collections.Concurrent;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The shared world of the ingest suite: a migrated database private to each world (its suites count whole
/// tables and read one run's event sequence, so they cannot share), the registry host used to create the org and
/// the project, a host whose every request carries the caller's service principal (the same seam
/// <see cref="EvidenceUploadUrlTests"/> uses, because a service principal with a <c>CredentialId</c> — the slot-binding
/// identity, T9 — and a project scope cannot be expressed through request headers), the seeded run with its planned
/// executions and producer slots, and the reads the tests assert on.
/// <para>
/// The app role writes the seeded <c>producer_slot</c> rows inside a tenant scope (0029 grants it INSERT and the
/// FORCE-RLS policy admits the row); the run, its catalogue parents and its planned executions come from
/// <see cref="PlannedExecutionSeeder"/>, which writes them as the migration role.
/// </para>
/// </summary>
internal sealed class IngestTestWorld : IAsyncDisposable
{
    /// <summary>The caller's token id: the slot-binding identity every seeded slot is bound to (T9).</summary>
    internal const string Jti = "jti-ingest-1";

    /// <summary>Another identity's token id, for the <c>producer_slot_taken</c> case.</summary>
    internal const string OtherJti = "jti-ingest-2";

    /// <summary>The producer id of the seeded shard (05.2.1 registers <c>shard-N</c> per manifest shard).</summary>
    internal const string ProducerId = "shard-1";

    private IngestTestWorld(
        PostgresTenancyFixture fixture,
        TenancyApiFactory api,
        TenancyApiFactory host,
        HttpClient client,
        string database,
        Guid user,
        Guid org,
        Guid project,
        Guid runId,
        PlannedExecutionSeeder.SeededRun plan)
    {
        Fixture = fixture;
        Api = api;
        Host = host;
        Client = client;
        Database = database;
        User = user;
        Org = org;
        Project = project;
        RunId = runId;
        Plan = plan;
    }

    internal PostgresTenancyFixture Fixture { get; }

    /// <summary>The registry host (real resolver) used for org/project setup and second-tenant reads.</summary>
    internal TenancyApiFactory Api { get; }

    /// <summary>The host whose requests carry the caller's service principal.</summary>
    internal TenancyApiFactory Host { get; }

    internal HttpClient Client { get; }

    internal string Database { get; }

    /// <summary>The org-owning user the setup client acts as: a member principal, needed to add a second project.</summary>
    internal Guid User { get; }

    internal Guid Org { get; }

    internal Guid Project { get; }

    internal Guid RunId { get; }

    internal PlannedExecutionSeeder.SeededRun Plan { get; }

    /// <summary>The caller's service principal: <c>event:write</c>, scoped to the run's project, bound to <see cref="Jti"/>.</summary>
    internal static RequestPrincipal Writer(string? jti = null, Guid? projectScope = null) =>
        RequestPrincipal.Service(projectScope, [Permissions.Event.Write], credentialId: jti ?? Jti);

    /// <summary>A service principal without <c>event:write</c> (the denied-permission case).</summary>
    internal static RequestPrincipal Reader() =>
        RequestPrincipal.Service(projectScope: null, [Permissions.Run.Read], credentialId: Jti);

    /// <summary>
    /// A fresh org (unique slug) and its first project (real registry routes) in the shared database, or in
    /// <paramref name="database"/> when a suite needs whole-database state and owns a dedicated one; one run with
    /// <paramref name="planned"/> planned executions and <paramref name="slots"/> producer slots, plus a host whose
    /// requests carry <paramref name="principal"/>. Assertions on this world filter by <see cref="Org"/> or <see cref="RunId"/>.
    /// </summary>
    internal static async Task<IngestTestWorld> StartAsync(
        PostgresTenancyFixture fixture,
        RequestPrincipal principal,
        int planned = 2,
        int slots = 1,
        string? boundJti = null,
        int? planCount = null,
        IReadOnlyList<(int Parent, int Child)>? edges = null,
        Action<IServiceCollection>? services = null,
        IReadOnlyDictionary<string, string?>? configuration = null,
        string? database = null,
        TenancyHostPool? hosts = null)
    {
        database ??= await fixture.SharedDatabaseAsync();
        var api = hosts?.RealResolver(database) ?? TenancyApiFactory.RealResolver(fixture.AppConnectionString(database));
        Guid user;
        Guid org;
        Guid project;

        using (var setup = api.CreateClient())
        {
            user = Guid.CreateVersion7();
            org = await RegistryApi.CreateOrgAsync(setup, user, Unique.Slug("acme"));
            project = await RegistryApi.CreateProjectAsync(setup, user, org, "checkout");
        }

        // The events route keys its burst budget on the token's project scope, so a world's own
        // writer always carries the project it writes into; another scope stays as the caller set it.
        principal = principal is { Kind: PrincipalKind.ServicePrincipal, ProjectScope: null, CredentialId: { Length: > 0 } } unscoped
            ? RequestPrincipal.Service(project, unscoped.ClaimedPermissions, unscoped.CredentialId, unscoped.CiRun)
            : principal;

        var host = hosts is null
            ? TenancyApiFactory.WithPrincipal(
                fixture.AppConnectionString(database), org, project, principal, configuration, services)
            : hosts.WithPrincipal(database, org, project, principal, configuration, services);

        try
        {
            var plan = await PlannedExecutionSeeder.SeedPlanAsync(fixture, database, org, planCount ?? planned, edges: edges);
            await SeedSlotsAsync(fixture, database, org, plan.RunId, slots, boundJti);

            return new IngestTestWorld(
                fixture, api, host, host.CreateClient(), database, user, org, project, plan.RunId, plan);
        }
        catch
        {
            // A world that never returns owns nothing: the host graph it built is disposed here, so a failed
            // arrange does not leave a server and its connections behind for the rest of the run. The database
            // is the caller's (its `await using` drops it, or it was never dedicated at all).
            await host.DisposeAsync();
            await api.DisposeAsync();
            throw;
        }
    }

    /// <summary>One committed producer slot per shard, written as the app role inside a tenant scope.</summary>
    internal static async Task SeedSlotsAsync(
        PostgresTenancyFixture fixture,
        string database,
        Guid org,
        Guid runId,
        int count,
        string? boundJti)
    {
        await using var scope = await TenantScope.BeginAsync(fixture.AppConnectionString(database), org);

        for (var i = 1; i <= count; i++)
        {
            (await scope.ExecuteAsync(
                "insert into public.producer_slot (id, org_id, run_id, shard_index, producer_id, bound_token_jti) "
                + "values (@id, @org_id, @run_id, @shard_index, @producer_id, @jti)",
                ("id", Guid.CreateVersion7()), ("org_id", org), ("run_id", runId), ("shard_index", i),
                ("producer_id", $"shard-{i}"), ("jti", (object?)boundJti ?? DBNull.Value))).ShouldBe(1);
        }

        await scope.CommitAsync();
    }

    /// <summary>A second org with a project, created through the real registry routes.</summary>
    internal async Task<(Guid Org, Guid Project)> CreateOtherOrgAsync(string slug)
    {
        using var setup = Api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await RegistryApi.CreateOrgAsync(setup, user, slug);

        return (org, await RegistryApi.CreateProjectAsync(setup, user, org, slug + "-project"));
    }

    /// <summary>One committed run of <paramref name="org"/> with <paramref name="planned"/> planned executions.</summary>
    internal Task<PlannedExecutionSeeder.SeededRun> SeedOtherRunAsync(Guid org, int planned = 1) =>
        PlannedExecutionSeeder.SeedPlanAsync(Fixture, Database, org, planned);

    /// <summary>A committed run of <paramref name="project"/> (same org), for the project-scope case.</summary>
    internal async Task<PlannedExecutionSeeder.SeededRun> SeedRunInProjectAsync(Guid project, int planned = 1)
    {
        var plan = await PlannedExecutionSeeder.SeedPlanAsync(Fixture, Database, Org, planned);

        await SiblingProjectParents.MoveRunAsync(Fixture, Database, Org, plan.RunId, project);

        return plan;
    }

    /// <summary>Moves the seeded run to another state along the guard's declared edges (the canceled/complete cases).</summary>
    internal Task SetRunStateAsync(string state) =>
        RunStateWalk.MoveAsync(Fixture, Database, RunId, state);

    internal Task<long> CountAsync(string table, string? where = null) =>
        Fixture.ScalarAsync<long>(
            Database, $"select count(*) from public.{table}" + (where is null ? string.Empty : $" where {where}"));

    internal Task<T> ScalarAsync<T>(string sql) => Fixture.ScalarAsync<T>(Database, sql);

    /// <summary>Reads a column of the run's event rows in request order (producer sequence).</summary>
    internal Task<IReadOnlyList<T>> EventColumnAsync<T>(string column) =>
        Fixture.ColumnAsync<T>(Database, $"select {column} from public.event where org_id = '{Org:D}' and run_id = '{RunId:D}' order by sequence");

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        try
        {
            await Host.DisposeAsync();
        }
        finally
        {
            // The registry host is disposed even when the principal host's own disposal throws.
            await Api.DisposeAsync();
        }
    }
}

/// <summary>
/// A minimal <see cref="ILoggerProvider"/> that keeps every rendered line, so a test can assert a secret or an event
/// body never reaches a log (S3). The same technique as <c>Hosts.IntegrationTests.CaptureLoggerProvider</c>; it is
/// re-declared here because that type lives in another test assembly.
/// </summary>
internal sealed class IngestLogSink : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _lines = new();

    /// <summary>Everything captured since the last <see cref="Clear"/>, in order.</summary>
    internal IReadOnlyList<string> Lines => [.. _lines];

    /// <summary>Forgets every captured line.</summary>
    internal void Clear() => _lines.Clear();

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new Sink(categoryName, _lines);

    /// <inheritdoc />
    public void Dispose()
    {
        // Nothing to release.
    }

    private sealed class Sink(string category, ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var pairs = state as IReadOnlyList<KeyValuePair<string, object?>>;
            var text = pairs is null
                ? formatter(state, exception)
                : $"{formatter(state, exception)} | {string.Join(" | ", pairs.Select(pair => $"{pair.Key}={pair.Value}"))}";

            lines.Enqueue($"{category}: {text}");
        }
    }
}
