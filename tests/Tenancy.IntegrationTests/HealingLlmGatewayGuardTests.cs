using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.BuildingBlocks.SharedKernel.Secrets;
using Evidgate.Modules.Healing;
using Evidgate.Modules.Healing.Application;
using Evidgate.Modules.Healing.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The guards a healing call passes before any provider is reached, against a real PostgreSQL: the provider selection,
/// the kill switch at every scope and the project's daily budget. Each refusal answers one typed reason, counts once on
/// the refusal counter, reaches no adapter and writes no <c>healing_llm_call</c> row — and each guard has a positive
/// control that proceeds with only the guarded variable changed.
/// </summary>
/// <remarks>
/// Switch rows and budget rows are seeded through the owner connection, because the app role can neither write the
/// system switch row nor provision a budget cap. The adapter is a spy that counts calls: a real client would leave the
/// question "was the adapter reached?" to inference, while a call count answers it directly.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class HealingLlmGatewayGuardTests
{
    private const string Payload = "Design the checkout checklist for the shop.";
    private const string Model = "healing-test-model";
    private const string PromptVersion = "v1";
    private const string Answer = """{"checklist":["open the cart"]}""";

    private readonly PostgresTenancyFixture _fixture;

    public HealingLlmGatewayGuardTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    // ------------------------------------------------------------------ the kill switch

    /// <summary>
    /// A scope that stores <c>disabled</c> stops the call wherever it is set: system, tenant or project, each alone,
    /// refuses with <c>healing_disabled</c>, reaches no adapter and leaves the ledger empty.
    /// </summary>
    /// <param name="scope">The scope whose row carries <c>disabled</c>.</param>
    [Theory]
    [InlineData("system")]
    [InlineData("tenant")]
    [InlineData("project")]
    public async Task InvokeHealingLlm_SwitchDisabled_RefusesWithoutProviderCall(string scope)
    {
        var spy = new SpyProvider();
        await using var world = await StartAsync(HealingProviderSelection.Mock, Environments.Development, spy);
        await world.SeedSwitchAsync(scope, HealingAvailabilityModes.Disabled);

        using var listener = HealingTelemetryCapture.RefusedCalls(out var reasons);
        var result = await InvokeAsync(world, AuthoringRequest(world.Project));

        ReasonOf(result).ShouldBe(HealingLlmRefusals.HealingDisabled);
        spy.Calls.ShouldBe(0);
        (await ReadCallsAsync(world)).ShouldBeEmpty();
        reasons.ToArray().ShouldBe([HealingLlmRefusals.HealingDisabled]);
    }

    /// <summary>
    /// A scope that stores <c>shadow</c> still analyses: the call proceeds, the attempt is recorded and nothing is
    /// counted as refused — the positive control of the kill-switch refusals.
    /// </summary>
    [Fact]
    public async Task InvokeHealingLlm_SwitchShadow_PlacesTheCall()
    {
        var spy = new SpyProvider();
        await using var world = await StartAsync(HealingProviderSelection.Mock, Environments.Development, spy);
        await world.SeedSwitchAsync("system", HealingAvailabilityModes.Shadow);

        using var listener = HealingTelemetryCapture.RefusedCalls(out var reasons);
        var result = await InvokeAsync(world, AuthoringRequest(world.Project));

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        spy.Calls.ShouldBe(1);
        (await ReadCallsAsync(world)).ShouldHaveSingleItem().Status.ShouldBe(HealingLlmStatuses.Ok);
        reasons.ShouldBeEmpty();
    }

    /// <summary>
    /// A scope that stores <c>enabled</c> proceeds when the deployment may apply healing — the other half of the clamp:
    /// with the same row and the flag absent, the resolver holds it at shadow and the call still proceeds, so the flag
    /// changes what healing may do, never whether the call is placed.
    /// </summary>
    [Fact]
    public async Task InvokeHealingLlm_SwitchEnabledWithApplyOn_PlacesTheCall()
    {
        var spy = new SpyProvider();
        await using var world = await StartAsync(
            HealingProviderSelection.Mock,
            Environments.Development,
            spy,
            applyEnabled: true);
        await world.SeedSwitchAsync("tenant", HealingAvailabilityModes.Enabled);

        var result = await InvokeAsync(world, AuthoringRequest(world.Project));

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        spy.Calls.ShouldBe(1);
        (await ReadCallsAsync(world)).ShouldHaveSingleItem().Status.ShouldBe(HealingLlmStatuses.Ok);
    }

    // ------------------------------------------------------------------ the provider selection

    /// <summary>
    /// A deployment that selected no provider places no call: <c>provider_disabled</c>, no adapter call, no row.
    /// </summary>
    [Fact]
    public async Task InvokeHealingLlm_NoProviderSelected_RefusesWithoutProviderCall()
    {
        var spy = new SpyProvider();
        await using var world = await StartAsync(HealingProviderSelection.None, Environments.Development, spy);

        using var listener = HealingTelemetryCapture.RefusedCalls(out var reasons);
        var result = await InvokeAsync(world, AuthoringRequest(world.Project));

        ReasonOf(result).ShouldBe(HealingLlmRefusals.ProviderDisabled);
        spy.Calls.ShouldBe(0);
        (await ReadCallsAsync(world)).ShouldBeEmpty();
        reasons.ToArray().ShouldBe([HealingLlmRefusals.ProviderDisabled]);
    }

    // ------------------------------------------------------------------ the daily budget

    /// <summary>
    /// A budget whose cost cap or whose token cap is reached refuses the call before the adapter is asked: both
    /// comparisons are load-bearing, so each cap is fired on its own.
    /// </summary>
    /// <param name="capCost">The row's cost cap.</param>
    /// <param name="capTokens">The row's token cap.</param>
    /// <param name="usedCost">The cost the period spent.</param>
    /// <param name="usedTokens">The tokens the period spent.</param>
    [Theory]
    [InlineData(1.0, 100, 1.0, 40)]
    [InlineData(1.0, 100, 0.4, 100)]
    public async Task InvokeHealingLlm_BudgetSpent_RefusesWithoutProviderCall(
        double capCost,
        int capTokens,
        double usedCost,
        int usedTokens)
    {
        var spy = new SpyProvider();
        await using var world = await StartAsync(HealingProviderSelection.Mock, Environments.Development, spy);
        await world.SeedBudgetAsync((decimal)capCost, capTokens, (decimal)usedCost, usedTokens);

        using var listener = HealingTelemetryCapture.RefusedCalls(out var reasons);
        var result = await InvokeAsync(world, AuthoringRequest(world.Project));

        ReasonOf(result).ShouldBe(HealingLlmRefusals.BudgetExceeded);
        spy.Calls.ShouldBe(0);
        (await ReadCallsAsync(world)).ShouldBeEmpty();
        reasons.ToArray().ShouldBe([HealingLlmRefusals.BudgetExceeded]);
    }

    /// <summary>A budget with room under both caps proceeds and records the attempt.</summary>
    [Fact]
    public async Task InvokeHealingLlm_BudgetWithRoom_PlacesTheCall()
    {
        var spy = new SpyProvider();
        await using var world = await StartAsync(HealingProviderSelection.Mock, Environments.Development, spy);
        await world.SeedBudgetAsync(1.0m, 100, 0.4m, 40);

        var result = await InvokeAsync(world, AuthoringRequest(world.Project));

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        spy.Calls.ShouldBe(1);
        (await ReadCallsAsync(world)).ShouldHaveSingleItem().Tokens.ShouldBe(Answer.Length);
    }

    /// <summary>
    /// A host outside Development whose project has no budget row places no call: the HTTP provider may not spend where
    /// nothing measures the spend, so the refusal is <c>budget_not_provisioned</c> and no adapter is reached.
    /// </summary>
    [Fact]
    public async Task InvokeHealingLlm_BudgetAbsentWithHttpProvider_RefusesWithoutProviderCall()
    {
        var spy = new SpyProvider();
        await using var world = await StartAsync(
            HealingProviderSelection.Http,
            Environments.Production,
            spy,
            baseAddress: "https://llm.example.test/",
            apiKeySecret: SecretNames.HealingLlmApiKey);

        world.Provider.GetRequiredService<IOptions<HealingProviderOptions>>().Value.Kind
            .ShouldBe(HealingProviderKind.Http);

        using var listener = HealingTelemetryCapture.RefusedCalls(out var reasons);
        var result = await InvokeAsync(world, AuthoringRequest(world.Project));

        ReasonOf(result).ShouldBe(HealingLlmRefusals.BudgetNotProvisioned);
        spy.Calls.ShouldBe(0);
        (await ReadCallsAsync(world)).ShouldBeEmpty();
        reasons.ToArray().ShouldBe([HealingLlmRefusals.BudgetNotProvisioned]);
    }

    /// <summary>
    /// The Development adapter spends nothing, so an absent budget row does not stop it — the positive control of
    /// <see cref="InvokeHealingLlm_BudgetAbsentWithHttpProvider_RefusesWithoutProviderCall"/>.
    /// </summary>
    [Fact]
    public async Task InvokeHealingLlm_BudgetAbsentWithDevelopmentProvider_PlacesTheCall()
    {
        var spy = new SpyProvider();
        await using var world = await StartAsync(HealingProviderSelection.Mock, Environments.Development, spy);

        var result = await InvokeAsync(world, AuthoringRequest(world.Project));

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        spy.Calls.ShouldBe(1);
        (await ReadCallsAsync(world)).ShouldHaveSingleItem().ProjectId.ShouldBe(world.Project);
    }

    // ------------------------------------------------------------------ the world

    private sealed record World(
        Guid Org,
        Guid Project,
        DedicatedDatabase Database,
        string OwnerConnectionString,
        NpgsqlDataSource DataSource,
        ServiceProvider Provider) : IAsyncDisposable
    {
        /// <summary>Seeds one scope's switch row as the owner, the only role that may write the system row.</summary>
        /// <param name="scope"><c>system</c>, <c>tenant</c> or <c>project</c>.</param>
        /// <param name="mode">One of the three storable modes (<c>shadow</c>, <c>disabled</c>, <c>enabled</c>).</param>
        internal async Task SeedSwitchAsync(string scope, string mode)
        {
            // One statement per scope, because the nullable columns are literals: the owner policy accepts every row,
            // and a null parameter would need a cast the shared scope helper does not add.
            var (sql, parameters) = scope switch
            {
                "system" => (
                    """
                    insert into public.healing_switch (id, scope, org_id, project_id, mode, reason, set_by, at)
                    values (@id, 'system', null, null, @mode, null, @set_by, now())
                    """,
                    new (string, object)[] { ("id", Guid.CreateVersion7()), ("mode", mode), ("set_by", Guid.CreateVersion7()) }),
                "tenant" => (
                    """
                    insert into public.healing_switch (id, scope, org_id, project_id, mode, reason, set_by, at)
                    values (@id, 'tenant', @org_id, null, @mode, null, @set_by, now())
                    """,
                    new (string, object)[]
                    {
                        ("id", Guid.CreateVersion7()),
                        ("org_id", Org),
                        ("mode", mode),
                        ("set_by", Guid.CreateVersion7()),
                    }),
                _ => (
                    """
                    insert into public.healing_switch (id, scope, org_id, project_id, mode, reason, set_by, at)
                    values (@id, 'project', @org_id, @project_id, @mode, null, @set_by, now())
                    """,
                    new (string, object)[]
                    {
                        ("id", Guid.CreateVersion7()),
                        ("org_id", Org),
                        ("project_id", Project),
                        ("mode", mode),
                        ("set_by", Guid.CreateVersion7()),
                    }),
            };

            await using var seed = await TenantScope.BeginAsync(OwnerConnectionString);
            await seed.ExecuteAsync(sql, parameters);
            await seed.CommitAsync();
        }

        /// <summary>Seeds the project's daily budget row for today, in UTC.</summary>
        /// <param name="capCost">The cost cap.</param>
        /// <param name="capTokens">The token cap.</param>
        /// <param name="usedCost">The cost already spent.</param>
        /// <param name="usedTokens">The tokens already spent.</param>
        internal async Task SeedBudgetAsync(decimal capCost, int capTokens, decimal usedCost, int usedTokens)
        {
            await using var seed = await TenantScope.BeginAsync(OwnerConnectionString);
            await seed.ExecuteAsync(
                """
                insert into public.healing_llm_budget
                    (id, org_id, project_id, period, period_start, period_cap_cost, period_cap_tokens,
                     used_cost, used_tokens)
                values
                    (@id, @org_id, @project_id, 'daily', (now() at time zone 'utc')::date, @cap_cost, @cap_tokens,
                     @used_cost, @used_tokens)
                """,
                ("id", Guid.CreateVersion7()),
                ("org_id", Org),
                ("project_id", Project),
                ("cap_cost", capCost),
                ("cap_tokens", capTokens),
                ("used_cost", usedCost),
                ("used_tokens", usedTokens));
            await seed.CommitAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Provider.DisposeAsync();
            await DataSource.DisposeAsync();
            await Database.DisposeAsync();
        }
    }

    /// <summary>One org and its project, the module's composition, and the adapter the gateway would call.</summary>
    /// <param name="provider">The configured provider word.</param>
    /// <param name="environmentName">The host environment the module is composed under.</param>
    /// <param name="spy">The adapter that counts calls.</param>
    /// <param name="applyEnabled">Whether the composition turns the V1 clamp on.</param>
    /// <param name="baseAddress">The HTTP kind's base address.</param>
    /// <param name="apiKeySecret">The HTTP kind's secret name.</param>
    private Task<World> StartAsync(
        string provider,
        string environmentName,
        SpyProvider spy,
        bool applyEnabled = false,
        string? baseAddress = null,
        string? apiKeySecret = null) =>
        // A private database per world: these cases commit a system switch row, which is global, and the partial unique
        // index allows exactly one — a shared database would make the second case fail and leak the first case's mode.
        _fixture.StartDedicatedAsync(async database =>
        {
            var org = Guid.CreateVersion7();
            var project = Guid.CreateVersion7();

            await using (var seed = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database)))
            {
                var orgSlug = Unique.Slug("healing-guard");
                var projectSlug = Unique.Slug("checkout");
                await seed.ExecuteAsync(
                    "insert into public.organization (id, name, slug) values (@id, @name, @slug)",
                    ("id", org),
                    ("name", orgSlug),
                    ("slug", orgSlug));
                await seed.ExecuteAsync(
                    "insert into public.project (id, org_id, name, slug) values (@id, @org_id, @name, @slug)",
                    ("id", project),
                    ("org_id", org),
                    ("name", projectSlug),
                    ("slug", projectSlug));
                await seed.CommitAsync();
            }

            var dataSource = NpgsqlDataSource.Create(_fixture.AppConnectionString(database));
            var services = new ServiceCollection();
            services.AddSingleton(dataSource);
            services.AddEvidgateTenancy();
            services.AddHealingModule(
                HealingLlmTestConfiguration.Build(provider, baseAddress, apiKeySecret, applyEnabled),
                HealingLlmTestConfiguration.Host(environmentName));

            // The only product substitution in this suite: the adapter, so a call that should not happen is counted
            // rather than answered.
            services.Replace(ServiceDescriptor.Singleton<IHealingLlmProvider>(spy));

            return new World(
                org,
                project,
                database,
                _fixture.OwnerConnectionString(database),
                dataSource,
                services.BuildServiceProvider());
        });

    /// <summary>Invokes the gateway inside a tenant scope on the app role, as the caller does.</summary>
    /// <param name="world">The composed world.</param>
    /// <param name="request">The call.</param>
    private static async Task<Result<HealingLlmInvocation>> InvokeAsync(World world, HealingLlmRequest request)
    {
        await using var services = world.Provider.CreateAsyncScope();
        var scopes = services.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
        await using var scope = await scopes.BeginAsync(
            world.Org,
            world.Project,
            readOnlyTransaction: false,
            TestContext.Current.CancellationToken);

        var gateway = services.ServiceProvider.GetRequiredService<HealingLlmGateway>();
        var result = await gateway.InvokeAsync(request, TestContext.Current.CancellationToken);

        // The caller commits: every recorded attempt rides the caller's tenant transaction (00 §5.5 C3).
        await scope.CommitAsync(TestContext.Current.CancellationToken);

        return result;
    }

    /// <summary>The rows this org recorded, in attempt order, read through the same tenant path.</summary>
    /// <param name="world">The composed world.</param>
    private static async Task<IReadOnlyList<HealingLlmCallRow>> ReadCallsAsync(World world)
    {
        await using var services = world.Provider.CreateAsyncScope();
        var scopes = services.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
        await using var scope = await scopes.BeginAsync(
            world.Org,
            world.Project,
            readOnlyTransaction: true,
            TestContext.Current.CancellationToken);

        var db = services.ServiceProvider.GetRequiredService<ITenantDb>();
        return await db.QueryAsync<HealingLlmCallRow>(
            """
            select id, org_id as "OrgId", project_id as "ProjectId", incident_id as "IncidentId",
                   candidate_id as "CandidateId", purpose, model, prompt_version as "PromptVersion",
                   tokens, cost, status, attempt
              from public.healing_llm_call
             order by attempt
            """,
            null,
            TestContext.Current.CancellationToken);
    }

    /// <summary>The single refusal reason a failed answer carries.</summary>
    /// <param name="result">The gateway's answer.</param>
    private static string ReasonOf(Result<HealingLlmInvocation> result)
    {
        result.IsSuccess.ShouldBeFalse("the call was expected to be refused");
        result.Error!.Details.ShouldNotBeNull();
        return result.Error.Details!.ShouldHaveSingleItem().Reason;
    }

    private static HealingLlmRequest AuthoringRequest(Guid project) =>
        new(project, HealingLlmPurposes.AuthoringGeneration, null, null, Model, PromptVersion, Payload, null);

    /// <summary>An adapter that counts the calls it receives and answers schema-valid content.</summary>
    private sealed class SpyProvider : IHealingLlmProvider
    {
        private int _calls;

        internal int Calls => Volatile.Read(ref _calls);

        public Task<HealingLlmResult> InvokeAsync(HealingLlmRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = Interlocked.Increment(ref _calls);
            return Task.FromResult(new HealingLlmResult(HealingLlmStatuses.Ok, Answer, Answer.Length, 0.01m));
        }
    }
}
