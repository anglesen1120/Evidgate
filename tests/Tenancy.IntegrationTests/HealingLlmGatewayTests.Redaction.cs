using System.Text;
using Evidgate.Modules.Healing.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class HealingLlmGatewayTests
{
    /// <summary>
    /// A payload withheld as <c>blocked_leak</c> gives its whole budget hold back (the counters return to where they
    /// were), while the same world's clean call keeps its real usage: the counters are observable, so a leaked hold
    /// would show.
    /// </summary>
    [Fact]
    public async Task InvokeHealingLlm_BlockedLeak_RefundsTheBudgetHold()
    {
        var provider = new StubProvider(_ => new HealingLlmResult(HealingLlmStatuses.Ok, "{}", 12, 0.01m));
        await using var world = await StartAsync(services => services.Replace(
            ServiceDescriptor.Singleton<IHealingLlmProvider>(provider)));
        var database = await _fixture.SharedDatabaseAsync();
        await SeedBudgetAsync(database, world);

        // Base64 of a password assignment: decoded by the pipeline, masked, and still flagged as a residual.
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("password=" + "hunter2" + "hunter2"));
        var blocked = await InvokeAsync(world, AuthoringRequest(world.Project) with { Payload = "note " + encoded + " end" });

        blocked.Result.Status.ShouldBe(HealingLlmStatuses.BlockedLeak);
        provider.Calls.ShouldBe(0);
        (await UsedAsync(database, world)).ShouldBe((0m, 0));
        var row = (await ReadCallsAsync(world)).ShouldHaveSingleItem();
        row.Status.ShouldBe(HealingLlmStatuses.BlockedLeak);
        row.Tokens.ShouldBe(0);
        row.Cost.ShouldBe(0m);

        var clean = await InvokeAsync(world, AuthoringRequest(world.Project));

        clean.Result.Status.ShouldBe(HealingLlmStatuses.Ok);
        provider.Calls.ShouldBe(1);
        (await UsedAsync(database, world)).ShouldBe((0.01m, 12));
    }

    /// <summary>Today's daily budget row with room, so the hold is taken and its counters can be read back.</summary>
    private async Task SeedBudgetAsync(string database, World world)
    {
        await using var seed = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database));
        await seed.ExecuteAsync(
            """
            insert into public.healing_llm_budget
                (id, org_id, project_id, period, period_start, period_cap_cost, period_cap_tokens, used_cost, used_tokens)
            values
                (@id, @org_id, @project_id, 'daily', (now() at time zone 'utc')::date, 1.0, 100000, 0, 0)
            """,
            ("id", Guid.CreateVersion7()),
            ("org_id", world.Org),
            ("project_id", world.Project));
        await seed.CommitAsync();
    }

    /// <summary>The counters as the owner role sees them, so RLS cannot hide the row.</summary>
    private async Task<(decimal Cost, int Tokens)> UsedAsync(string database, World world)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database));
        var cost = await scope.ScalarAsync<decimal>(
            "select used_cost from public.healing_llm_budget where project_id = @project_id",
            ("project_id", world.Project));
        var tokens = await scope.ScalarAsync<int>(
            "select used_tokens from public.healing_llm_budget where project_id = @project_id",
            ("project_id", world.Project));
        return (cost, tokens);
    }
}
