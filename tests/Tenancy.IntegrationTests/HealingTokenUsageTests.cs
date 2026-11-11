using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Healing;
using Evidgate.Modules.Healing.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The token-spend seam another module reads against a real PostgreSQL: one aggregate over an org's healing
/// attempts inside the UTC month's half-open window, the sum every attempt contributed and the count of the ones
/// whose token use is known. The read runs on the app role inside the caller's tenant scope, exactly as the cost
/// query calls it.
/// </summary>
/// <remarks>
/// Seeding runs as the owner, which is what lets a case write rows for another org and assert the read never
/// counts them. Each case gets its own database from the shared fixture and leaves no row behind.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class HealingTokenUsageTests
{
    /// <summary>The month every case reads, as UTC instants.</summary>
    private static readonly DateTimeOffset MonthStart = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The month's exclusive end: the first instant of the next one.</summary>
    private static readonly DateTimeOffset MonthEnd = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly PostgresTenancyFixture _fixture;

    public HealingTokenUsageTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    /// <summary>
    /// Every attempt of the month is summed — an error that reported tokens burned them too — while the count only
    /// covers the attempts whose token use was reported at all.
    /// </summary>
    [Fact]
    public async Task HealingTokenUsage_SumsEveryAttemptAndCountsTheMeasuredOnes()
    {
        await using var world = await StartAsync();

        await world.SeedAsync(
            world.Attempt(100, HealingLlmStatuses.Ok, MonthStart.AddDays(1)),
            world.Attempt(25, HealingLlmStatuses.Error, MonthStart.AddDays(2)),
            world.Attempt(0, HealingLlmStatuses.Timeout, MonthStart.AddDays(3)));

        var usage = await world.ReadAsync();

        usage.Tokens.ShouldBe(125m);
        usage.KnownAttempts.ShouldBe(
            2,
            "a timeout that reported no tokens says nothing about how many it burned");
    }

    /// <summary>
    /// Zero tokens is a measurement only when the attempt succeeded: an <c>ok</c> attempt that spent nothing is a
    /// measured zero, while a failed one that reported nothing is unknown spend.
    /// </summary>
    [Fact]
    public async Task HealingTokenUsage_ZeroTokens_AreMeasuredOnlyWhenTheAttemptSucceeded()
    {
        await using var world = await StartAsync();

        await world.SeedAsync(
            world.Attempt(0, HealingLlmStatuses.Ok, MonthStart.AddDays(1)),
            world.Attempt(0, HealingLlmStatuses.Error, MonthStart.AddDays(2)),
            world.Attempt(0, HealingLlmStatuses.Timeout, MonthStart.AddDays(3)));

        var usage = await world.ReadAsync();

        usage.Tokens.ShouldBe(0m);
        usage.KnownAttempts.ShouldBe(1);
    }

    /// <summary>
    /// The window is [month start, next month start) in UTC: the first instant belongs to it and the end instant
    /// does not, so an attempt on the boundary is never counted twice across two months.
    /// </summary>
    [Fact]
    public async Task HealingTokenUsage_Window_IsTheHalfOpenUtcMonth()
    {
        await using var world = await StartAsync();

        await world.SeedAsync(
            world.Attempt(10, HealingLlmStatuses.Ok, MonthStart),
            world.Attempt(20, HealingLlmStatuses.Ok, MonthEnd.AddSeconds(-1)),
            world.Attempt(40, HealingLlmStatuses.Ok, MonthStart.AddSeconds(-1)),
            world.Attempt(80, HealingLlmStatuses.Ok, MonthEnd));

        var usage = await world.ReadAsync();

        usage.Tokens.ShouldBe(30m);
        usage.KnownAttempts.ShouldBe(2);
    }

    /// <summary>Another org's attempts in the same month are invisible to the read.</summary>
    [Fact]
    public async Task HealingTokenUsage_OtherOrg_IsNeverCounted()
    {
        await using var world = await StartAsync();

        await world.SeedAsync(
            world.Attempt(5, HealingLlmStatuses.Ok, MonthStart.AddDays(1)),
            world.Attempt(5, HealingLlmStatuses.Ok, MonthStart.AddDays(1), org: world.OtherOrg));

        var usage = await world.ReadAsync();

        usage.Tokens.ShouldBe(5m);
        usage.KnownAttempts.ShouldBe(1);
    }

    /// <summary>A month with no attempt reports no measurement rather than a zero.</summary>
    [Fact]
    public async Task HealingTokenUsage_EmptyMonth_IsNullWithNoMeasuredAttempt()
    {
        await using var world = await StartAsync();

        var usage = await world.ReadAsync();

        usage.Tokens.ShouldBeNull();
        usage.KnownAttempts.ShouldBe(0);
    }

    /// <summary>Builds a world with two orgs (each with one project) over a database of its own.</summary>
    private async Task<World> StartAsync()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var org = Guid.CreateVersion7();
        var otherOrg = Guid.CreateVersion7();
        var project = Guid.CreateVersion7();
        var otherProject = Guid.CreateVersion7();

        await using (var seed = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database)))
        {
            foreach (var (id, slug) in new[] { (org, "cost-a"), (otherOrg, "cost-b") })
            {
                var name = Unique.Slug("healing-cost-" + slug);
                await seed.ExecuteAsync(
                    "insert into public.organization (id, name, slug) values (@id, @name, @slug)",
                    ("id", id),
                    ("name", name),
                    ("slug", name));
            }

            foreach (var (id, owner, slug) in new[] { (project, org, "p-a"), (otherProject, otherOrg, "p-b") })
            {
                var name = Unique.Slug("healing-project-" + slug);
                await seed.ExecuteAsync(
                    "insert into public.project (id, org_id, name, slug) values (@id, @org_id, @name, @slug)",
                    ("id", id),
                    ("org_id", owner),
                    ("name", name),
                    ("slug", name));
            }

            await seed.CommitAsync();
        }

        var dataSource = NpgsqlDataSource.Create(_fixture.AppConnectionString(database));
        var services = new ServiceCollection();
        services.AddSingleton(dataSource);
        services.AddEvidgateTenancy();

        // The provider selection is host configuration and the module needs the host environment to read it; this suite
        // never resolves an adapter, so the Development stub only keeps the unset provider on its Development default.
        var environment = new TestHostEnvironment();
        services.AddSingleton<IHostEnvironment>(environment);
        services.AddHealingModule(new ConfigurationBuilder().Build(), environment);

        return new World(
            org,
            otherOrg,
            project,
            otherProject,
            _fixture.OwnerConnectionString(database),
            dataSource,
            services.BuildServiceProvider());
    }

    /// <summary>One org and its project, the app-role data source, and the module's own composition.</summary>
    private sealed record World(
        Guid Org,
        Guid OtherOrg,
        Guid Project,
        Guid OtherProject,
        string OwnerConnectionString,
        NpgsqlDataSource DataSource,
        ServiceProvider Provider) : IAsyncDisposable
    {
        /// <summary>One attempt of this world's org, attributed to its project unless <paramref name="org"/> says otherwise.</summary>
        /// <param name="tokens">The tokens the provider reported.</param>
        /// <param name="status">One of <see cref="HealingLlmStatuses"/>.</param>
        /// <param name="createdAt">When the attempt was recorded.</param>
        /// <param name="org">The org the attempt belongs to, or null for this world's.</param>
        internal AttemptSeed Attempt(int tokens, string status, DateTimeOffset createdAt, Guid? org = null) =>
            new(Guid.CreateVersion7(), org ?? Org, org is null ? Project : OtherProject, tokens, status, createdAt);

        /// <summary>Inserts attempts as the owner, which is what lets a case seed another org's rows.</summary>
        internal async Task SeedAsync(params AttemptSeed[] attempts)
        {
            await using var connection = new NpgsqlConnection(OwnerConnectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);

            foreach (var attempt in attempts)
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    insert into public.healing_llm_call
                        (id, org_id, project_id, purpose, model, prompt_version, tokens, cost, status, attempt,
                         created_at)
                    values
                        (@id, @org_id, @project_id, 'authoring_generation', 'test-model', 'test-v1', @tokens, 0,
                         @status, 1, @created_at)
                    """;
                command.Parameters.Add(new NpgsqlParameter("id", attempt.Id));
                command.Parameters.Add(new NpgsqlParameter("org_id", attempt.Org));
                command.Parameters.Add(new NpgsqlParameter("project_id", attempt.Project));
                command.Parameters.Add(new NpgsqlParameter("tokens", attempt.Tokens));
                command.Parameters.Add(new NpgsqlParameter("status", attempt.Status));
                command.Parameters.Add(new NpgsqlParameter("created_at", attempt.CreatedAt));

                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
        }

        /// <summary>
        /// Reads this world's month exactly as the cost query does: a scoped container, the caller's tenant
        /// transaction on the app role, and the module's own registration of the seam.
        /// </summary>
        internal async Task<HealingTokenUsage> ReadAsync()
        {
            await using var services = Provider.CreateAsyncScope();
            var scopes = services.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
            await using var scope = await scopes.BeginAsync(
                Org,
                Project,
                readOnlyTransaction: true,
                TestContext.Current.CancellationToken);

            var reader = services.ServiceProvider.GetRequiredService<IHealingTokenUsage>();
            return await reader.ReadAsync(Org, MonthStart, MonthEnd, TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await Provider.DisposeAsync();
            await DataSource.DisposeAsync();
        }
    }

    /// <summary>One <c>healing_llm_call</c> row to insert.</summary>
    /// <param name="Id">The row id.</param>
    /// <param name="Org">The tenant.</param>
    /// <param name="Project">The project the attempt is attributed to.</param>
    /// <param name="Tokens">The tokens the provider reported.</param>
    /// <param name="Status">One of <see cref="HealingLlmStatuses"/>.</param>
    /// <param name="CreatedAt">When the attempt was recorded.</param>
    private sealed record AttemptSeed(
        Guid Id,
        Guid Org,
        Guid Project,
        int Tokens,
        string Status,
        DateTimeOffset CreatedAt);

    /// <summary>The smallest <see cref="IHostEnvironment"/> the module's provider selection reads: its name.</summary>
    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "Evidgate.Api";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
