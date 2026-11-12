using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Healing;
using Evidgate.Modules.Healing.Application;
using Evidgate.Modules.Healing.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The two-phase budget of the healing gateway against a real PostgreSQL: the hold is committed before the provider is
/// called, no tenant scope is open while it runs, and concurrent holds never pass the caps. A call cancelled before the
/// request is sent gives its hold back; once it may have been sent, any failure keeps the estimate charged. A hold that is never given back (a hard process kill) can only over-count.
/// </summary>
[Trait("Category", "Docker")]
public sealed class HealingLlmTwoPhaseBudgetTests
{
    private const string Payload = "Design the checkout checklist for the shop.";
    private const string Answer = """{"checklist":["open the cart"]}""";

    // Prompt estimate (43 / 4 + 1) plus the default output ceiling of 1024 tokens.
    private const int HoldTokens = 1035;

    private readonly PostgresTenancyFixture _fixture;

    public HealingLlmTwoPhaseBudgetTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    /// <summary>AC3: a spent budget places no call, writes no row and leaves the counters where they were.</summary>
    [Fact]
    public async Task InvokeHealingLlm_BudgetExceeded_NoCallPlaced()
    {
        var provider = new ScriptedProvider(_ => Task.FromResult(Ok()));
        await using var world = await StartAsync(provider);
        await world.SeedBudgetAsync(1.0m, 100, 0.2m, 100);

        var result = await InvokeAsync(world, Request(world), TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Details!.ShouldHaveSingleItem().Reason.ShouldBe(HealingLlmRefusals.BudgetExceeded);
        provider.Calls.ShouldBe(0);
        (await world.CallCountAsync()).ShouldBe(0);
        (await world.UsedAsync()).ShouldBe((0.2m, 100));
    }

    /// <summary>
    /// The hold is committed and visible before the provider is reached, and the provider runs with no tenant scope open
    /// (AC3, P5): the same flow that called the gateway holds no connection while the provider works.
    /// </summary>
    [Fact]
    public async Task InvokeHealingLlm_ProviderRuns_OutsideTransactionWithCommittedHold()
    {
        (decimal Cost, int Tokens) seenByOtherConnection = default;
        var scopeOpen = true;
        ScriptedProvider? provider = null;
        provider = new ScriptedProvider(async _ =>
        {
            scopeOpen = provider!.Scopes!.Current is not null;
            seenByOtherConnection = await provider.World!.UsedAsync();
            return Ok();
        });

        await using var world = await StartAsync(provider);
        await world.SeedBudgetAsync(1.0m, 5000, 0m, 0);

        (await InvokeAsync(world, Request(world), TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();

        scopeOpen.ShouldBeFalse();
        seenByOtherConnection.ShouldBe((0.05m, HoldTokens));

        // Settled to what the provider reported, replacing the hold.
        (await world.UsedAsync()).ShouldBe((0.01m, Answer.Length));
        (await world.CallCountAsync()).ShouldBe(1);
    }

    /// <summary>
    /// N parallel calls against a budget that fits four holds: exactly four reach the provider while all are in flight,
    /// the rest are refused, and the counters never pass the cap at any observation.
    /// </summary>
    [Fact]
    public async Task InvokeHealingLlm_ParallelReservations_NeverExceedTheBudget()
    {
        const int Callers = 12;
        const int TokenCap = 5000; // four holds of 1035 fit (4140), a fifth does not (5175).
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ScriptedProvider? provider = null;
        provider = new ScriptedProvider(async ct =>
        {
            await release.Task.WaitAsync(ct);
            return Ok();
        });

        await using var world = await StartAsync(provider);
        await world.SeedBudgetAsync(10m, TokenCap, 0m, 0);

        var calls = Enumerable.Range(0, Callers)
            .Select(_ => Task.Run(() => InvokeAsync(world, Request(world), TestContext.Current.CancellationToken), TestContext.Current.CancellationToken))
            .ToArray();

        // Wait until every caller is either inside the provider or refused.
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (provider.Calls < 4 || calls.Count(call => call.IsCompleted) < Callers - 4)
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline, "the callers never settled into held and refused");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        var (cost, tokens) = await world.UsedAsync();
        tokens.ShouldBe(4 * HoldTokens);
        tokens.ShouldBeLessThanOrEqualTo(TokenCap);
        cost.ShouldBe(0.2m);

        release.SetResult();
        var results = await Task.WhenAll(calls);

        provider.Calls.ShouldBe(4);
        results.Count(result => result.IsSuccess).ShouldBe(4);
        results.Where(result => !result.IsSuccess)
            .ShouldAllBe(result => result.Error!.Details!.Single().Reason == HealingLlmRefusals.BudgetExceeded);
        (await world.UsedAsync()).ShouldBe((0.04m, 4 * Answer.Length));
        (await world.CallCountAsync()).ShouldBe(4);
    }

    /// <summary>
    /// A provider that throws after the request may have left keeps the estimate charged (the provider may have billed it)
    /// and surfaces the failure; one error row records the charge.
    /// </summary>
    [Fact]
    public async Task InvokeHealingLlm_ProviderThrows_ChargesTheEstimate()
    {
        var provider = new ScriptedProvider(_ => throw new InvalidOperationException("provider exploded"));
        await using var world = await StartAsync(provider);
        await world.SeedBudgetAsync(1.0m, 5000, 0.1m, 10);

        await Should.ThrowAsync<InvalidOperationException>(() => InvokeAsync(world, Request(world), TestContext.Current.CancellationToken));

        (await world.UsedAsync()).ShouldBe((0.15m, 10 + HoldTokens));
        (await world.CallCountAsync()).ShouldBe(1);
    }

    /// <summary>
    /// A refusal by the provider's own handlers happens before any byte is sent, so nothing can have been billed: the
    /// hold goes back whole and one zero-usage error row records the attempt.
    /// </summary>
    [Fact]
    public async Task InvokeHealingLlm_RefusedBeforeSend_ReleasesTheHold()
    {
        var provider = new ScriptedProvider(_ => throw new HealingLlmNotSentException("refused before the send"));
        await using var world = await StartAsync(provider);
        await world.SeedBudgetAsync(1.0m, 5000, 0.1m, 10);

        var invocation = await InvokeAsync(world, Request(world), TestContext.Current.CancellationToken);

        invocation.IsSuccess.ShouldBeTrue();
        invocation.Value.Result.Status.ShouldBe(HealingLlmStatuses.Error);
        (await world.UsedAsync()).ShouldBe((0.1m, 10));
        (await world.CallCountAsync()).ShouldBe(1);
    }

    /// <summary>
    /// A provider that reports an extreme token count cannot abort the settlement: the paid attempt is recorded and the
    /// counter is clamped to its column range instead of overflowing.
    /// </summary>
    [Fact]
    public async Task InvokeHealingLlm_ExtremeProviderUsage_IsRecordedAndClamped()
    {
        var provider = new ScriptedProvider(_ =>
            Task.FromResult(new HealingLlmResult(HealingLlmStatuses.Ok, Answer, int.MaxValue, 0.01m)));
        await using var world = await StartAsync(provider);
        await world.SeedBudgetAsync(1.0m, 5000, 0m, 1);

        (await InvokeAsync(world, Request(world), TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();

        (await world.UsedAsync()).ShouldBe((0.01m, int.MaxValue));
        (await world.CallCountAsync()).ShouldBe(1);
    }

    /// <summary>
    /// An adapter that returns a timeout, an error or an answer without usage says nothing about what the provider billed:
    /// the held estimate stays charged and the row keeps the real status.
    /// </summary>
    /// <param name="status">The status the adapter returned.</param>
    /// <param name="tokens">The tokens it reported.</param>
    /// <param name="cost">The cost it reported.</param>
    [Theory]
    [InlineData("timeout", 0, 0)]
    [InlineData("error", 0, 0)]
    [InlineData("ok", 0, 0)]
    public async Task InvokeHealingLlm_ResultWithoutUsage_ChargesTheEstimate(string status, int tokens, double cost)
    {
        var provider = new ScriptedProvider(_ =>
            Task.FromResult(new HealingLlmResult(status, status == "ok" ? Answer : null, tokens, (decimal)cost)));
        await using var world = await StartAsync(provider);
        await world.SeedBudgetAsync(1.0m, 5000, 0.1m, 10);

        _ = await InvokeAsync(world, Request(world), TestContext.Current.CancellationToken);

        (await world.UsedAsync()).ShouldBe((0.15m, 10 + HoldTokens));
        (await world.CallCountAsync()).ShouldBe(1);
    }

    /// <summary>A call cancelled while the provider runs keeps the estimate charged, like a throw.</summary>
    [Fact]
    public async Task InvokeHealingLlm_CancelledAfterSend_ChargesTheEstimate()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var provider = new ScriptedProvider(async ct =>
        {
            await cancel.CancelAsync();
            await Task.Delay(Timeout.Infinite, ct);
            return Ok();
        });
        await using var world = await StartAsync(provider);
        await world.SeedBudgetAsync(1.0m, 5000, 0.1m, 10);

        await Should.ThrowAsync<OperationCanceledException>(() => InvokeAsync(world, Request(world), cancel.Token));

        (await world.UsedAsync()).ShouldBe((0.15m, 10 + HoldTokens));
        (await world.CallCountAsync()).ShouldBe(1);
    }

    /// <summary>
    /// A settlement that fails (the attempt row cannot be inserted) rolls back whole; the provider answered, so the hold
    /// stays charged rather than refunding spend the ledger never saw.
    /// </summary>
    [Fact]
    public async Task InvokeHealingLlm_SettlementFails_KeepsTheHold()
    {
        ScriptedProvider? provider = null;
        provider = new ScriptedProvider(async _ =>
        {
            // Break the next insert into the ledger, after the hold was committed.
            await provider!.World!.ExecuteOwnerAsync(
                """
                create function public.healing_llm_call_test_fail() returns trigger language plpgsql as
                $$ begin raise exception 'settlement fault'; end $$;
                create trigger healing_llm_call_test_fail before insert on public.healing_llm_call
                  for each row execute function public.healing_llm_call_test_fail();
                """);
            return Ok();
        });
        await using var world = await StartAsync(provider);
        await world.SeedBudgetAsync(1.0m, 5000, 0.1m, 10);

        await Should.ThrowAsync<PostgresException>(() => InvokeAsync(world, Request(world), TestContext.Current.CancellationToken));

        (await world.UsedAsync()).ShouldBe((0.15m, 10 + HoldTokens));
        (await world.CallCountAsync()).ShouldBe(0);
    }

    /// <summary>
    /// A budget update that fails after the attempt row was inserted rolls the row back too; the hold stays charged.
    /// </summary>
    [Fact]
    public async Task InvokeHealingLlm_BudgetUpdateFailsAfterRowInsert_RollsBackTheRowAndKeepsTheHold()
    {
        ScriptedProvider? provider = null;
        provider = new ScriptedProvider(async _ =>
        {
            // Fails the settlement that lands on 0.11 (0.15 held - 0.05 + 0.01).
            await provider!.World!.ExecuteOwnerAsync(
                """
                create function public.healing_llm_budget_test_fail() returns trigger language plpgsql as
                $$ begin if new.used_cost = 0.11 then raise exception 'budget fault'; end if; return new; end $$;
                create trigger healing_llm_budget_test_fail before update on public.healing_llm_budget
                  for each row execute function public.healing_llm_budget_test_fail();
                """);
            return Ok();
        });
        await using var world = await StartAsync(provider);
        await world.SeedBudgetAsync(1.0m, 5000, 0.1m, 10);

        await Should.ThrowAsync<PostgresException>(() => InvokeAsync(world, Request(world), TestContext.Current.CancellationToken));

        (await world.UsedAsync()).ShouldBe((0.15m, 10 + HoldTokens));
        (await world.CallCountAsync()).ShouldBe(0);
    }

    /// <summary>
    /// A hold that was never given back (a process killed between the phases) only over-counts: the next hold that would
    /// pass the cap is refused with no call placed, and the counters never exceed the cap.
    /// </summary>
    [Fact]
    public async Task InvokeHealingLlm_LeakedHold_OnlyOvercounts()
    {
        var provider = new ScriptedProvider(_ => Task.FromResult(Ok()));
        await using var world = await StartAsync(provider);
        await world.SeedBudgetAsync(1.0m, 1500, 0m, 0);

        // The phase-one transaction of a call whose process then died: committed, never settled.
        await using (var services = world.Provider.CreateAsyncScope())
        {
            var transactions = services.ServiceProvider.GetRequiredService<HealingLlmTransactions>();
            var ledger = services.ServiceProvider.GetRequiredService<HealingLlmBudgetLedger>();
            var (verdict, _) = await transactions.RunAsync(
                world.Org,
                world.Project,
                ct => ledger.ReserveAsync(world.Project, HoldTokens, 0.05m, ct),
                TestContext.Current.CancellationToken);
            verdict.ShouldBe(HealingLlmBudgetVerdict.Reserved);
        }

        var result = await InvokeAsync(world, Request(world), TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Details!.ShouldHaveSingleItem().Reason.ShouldBe(HealingLlmRefusals.BudgetExceeded);
        provider.Calls.ShouldBe(0);
        (await world.UsedAsync()).ShouldBe((0.05m, HoldTokens));
    }

    // ------------------------------------------------------------------ the world

    private static HealingLlmResult Ok() => new(HealingLlmStatuses.Ok, Answer, Answer.Length, 0.01m);

    private static HealingLlmRequest Request(World world) =>
        new(world.Project, HealingLlmPurposes.AuthoringGeneration, null, null, "healing-test-model", "v1", Payload, null);

    private static async Task<Evidgate.BuildingBlocks.SharedKernel.Results.Result<HealingLlmInvocation>> InvokeAsync(
        World world,
        HealingLlmRequest request,
        CancellationToken cancellationToken)
    {
        await using var services = world.Provider.CreateAsyncScope();
        var gateway = services.ServiceProvider.GetRequiredService<HealingLlmGateway>();
        return await gateway.InvokeAsync(world.Org, request, cancellationToken);
    }

    private sealed class ScriptedProvider(Func<CancellationToken, Task<HealingLlmResult>> script) : IHealingLlmProvider
    {
        private int _calls;

        internal World? World { get; set; }

        internal ITenantScopeAccessor? Scopes { get; set; }

        internal int Calls => Volatile.Read(ref _calls);

        public Task<HealingLlmResult> InvokeAsync(HealingLlmRequest request, CancellationToken cancellationToken)
        {
            _ = Interlocked.Increment(ref _calls);
            return script(cancellationToken);
        }
    }

    private sealed record World(
        Guid Org,
        Guid Project,
        DedicatedDatabase Database,
        string OwnerConnectionString,
        NpgsqlDataSource DataSource,
        ServiceProvider Provider) : IAsyncDisposable
    {
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

        internal async Task ExecuteOwnerAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(OwnerConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            _ = await command.ExecuteNonQueryAsync();
        }

        /// <summary>The counters as another connection sees them (owner role, so RLS does not hide the row).</summary>
        internal async Task<(decimal Cost, int Tokens)> UsedAsync()
        {
            await using var connection = new NpgsqlConnection(OwnerConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "select used_cost, used_tokens from public.healing_llm_budget where project_id = @p",
                connection);
            _ = command.Parameters.AddWithValue("p", Project);
            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).ShouldBeTrue();
            return (reader.GetDecimal(0), reader.GetInt32(1));
        }

        internal async Task<long> CallCountAsync()
        {
            await using var connection = new NpgsqlConnection(OwnerConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "select count(*) from public.healing_llm_call where project_id = @p",
                connection);
            _ = command.Parameters.AddWithValue("p", Project);
            return (long)(await command.ExecuteScalarAsync())!;
        }

        public async ValueTask DisposeAsync()
        {
            await Provider.DisposeAsync();
            await DataSource.DisposeAsync();
            await Database.DisposeAsync();
        }
    }

    private Task<World> StartAsync(ScriptedProvider provider) =>
        // A private database per world: the settlement-fault case installs a trigger.
        _fixture.StartDedicatedAsync(async database =>
        {
            var org = Guid.CreateVersion7();
            var project = Guid.CreateVersion7();

            await using (var seed = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database)))
            {
                var orgSlug = Unique.Slug("healing-2p");
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
                HealingLlmTestConfiguration.Build(HealingProviderSelection.Mock),
                HealingLlmTestConfiguration.Host(Environments.Development));
            services.Replace(ServiceDescriptor.Singleton<IHealingLlmProvider>(provider));

            var built = services.BuildServiceProvider();
            var world = new World(
                org,
                project,
                database,
                _fixture.OwnerConnectionString(database),
                dataSource,
                built);
            provider.World = world;
            provider.Scopes = built.GetRequiredService<ITenantScopeFactory>();
            return world;
        });
}
