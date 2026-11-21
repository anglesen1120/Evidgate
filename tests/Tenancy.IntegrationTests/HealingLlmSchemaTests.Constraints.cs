using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class HealingLlmSchemaTests
{
    // ------------------------------------------------------------------ (d) counts, period, keys

    [Fact]
    public async Task NonNegativeCounts_AndAPositiveAttempt_AreRefused()
    {
        var world = await SeedTenantsAsync();

        // The per-attempt counters the budget and the cost report sum cannot be negative, and attempt is
        // the ordinal inside one invocation, so it starts at 1.
        await using (var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database)))
        {
            var error = await scope.CaptureErrorAsync(
                InsertCallSql,
                CallParameters(
                    Guid.CreateVersion7(), world.OrgA, world.ProjectA, "triage_hypothesis",
                    Guid.CreateVersion7(), tokens: -1));

            error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
            error.ConstraintName.ShouldBe("healing_llm_call_tokens_check");
        }

        await using (var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database)))
        {
            var error = await scope.CaptureErrorAsync(
                InsertCallSql,
                CallParameters(
                    Guid.CreateVersion7(), world.OrgA, world.ProjectA, "triage_hypothesis",
                    Guid.CreateVersion7(), cost: -0.000001m));

            error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
            error.ConstraintName.ShouldBe("healing_llm_call_cost_check");
        }

        await using (var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database)))
        {
            var error = await scope.CaptureErrorAsync(
                InsertCallSql,
                CallParameters(
                    Guid.CreateVersion7(), world.OrgA, world.ProjectA, "triage_hypothesis",
                    Guid.CreateVersion7(), attempt: 0));

            error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
            error.ConstraintName.ShouldBe("healing_llm_call_attempt_check");
        }

        // The budget's caps and usage, and its concurrency token.
        var budgetCases = new (string Name, (string Name, object Value)[] Parameters, string Constraint)[]
        {
            (
                "period_cap_cost",
                OwnerBudgetParameters(Guid.CreateVersion7(), world.OrgA, world.ProjectA, capCost: -1m),
                "healing_llm_budget_period_cap_cost_check"),
            (
                "period_cap_tokens",
                OwnerBudgetParameters(Guid.CreateVersion7(), world.OrgA, world.ProjectA, capTokens: -1),
                "healing_llm_budget_period_cap_tokens_check"),
            (
                "used_cost",
                OwnerBudgetParameters(Guid.CreateVersion7(), world.OrgA, world.ProjectA, usedCost: -1m),
                "healing_llm_budget_used_cost_check"),
            (
                "used_tokens",
                OwnerBudgetParameters(Guid.CreateVersion7(), world.OrgA, world.ProjectA, usedTokens: -1),
                "healing_llm_budget_used_tokens_check"),
            (
                "version",
                OwnerBudgetParameters(Guid.CreateVersion7(), world.OrgA, world.ProjectA, version: 0),
                "healing_llm_budget_version_check"),
        };

        foreach (var (name, parameters, constraint) in budgetCases)
        {
            await using var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
            var error = await scope.CaptureErrorAsync(InsertOwnerBudgetSql, parameters);

            error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation, name);
            error.ConstraintName.ShouldBe(constraint, name);
        }
    }

    [Fact]
    public async Task BudgetPeriod_IsDailyOnly()
    {
        var world = await SeedTenantsAsync();

        foreach (var period in new[] { "weekly", "monthly", "DAILY", "" })
        {
            await using var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
            var error = await scope.CaptureErrorAsync(
                InsertOwnerBudgetSql,
                OwnerBudgetParameters(Guid.CreateVersion7(), world.OrgA, world.ProjectA, period: period));

            error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation, period);
            error.ConstraintName.ShouldBe("healing_llm_budget_period_check", period);
        }
    }

    [Fact]
    public async Task CompositeFk_RefusesAProjectOfAnotherTenant()
    {
        var world = await SeedTenantsAsync();

        // Filed under org B (so the WITH CHECK passes) while naming org A's project: only the composite
        // foreign key can refuse it, which is exactly why the key carries org_id.
        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.OrgB))
        {
            var error = await scope.CaptureErrorAsync(
                InsertCallSql,
                CallParameters(
                    Guid.CreateVersion7(), world.OrgB, world.ProjectA, "triage_hypothesis",
                    Guid.CreateVersion7()));

            error.SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
            error.ConstraintName.ShouldBe("healing_llm_call_project_fk");
        }

        // The budget row is provisioned by the owner path (the app role holds no cap privilege, so it
        // cannot raise the cross-tenant question at all); the composite key refuses the same reference
        // for the owner, which is the enforcement this test is about.
        await using (var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database)))
        {
            var error = await scope.CaptureErrorAsync(
                InsertCapacitySql,
                CapacityParameters(Guid.CreateVersion7(), world.OrgB, world.ProjectA));

            error.SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
            error.ConstraintName.ShouldBe("healing_llm_budget_project_fk");
        }
    }

    [Fact]
    public async Task BudgetUniqueKey_RefusesASecondCounterRowForThePeriod()
    {
        var world = await SeedTenantsAsync();

        // Both rows are provisioned through the owner path: the caps are the owner's to set, and the key
        // is about the tuple, not about which role writes it.
        await using (var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database)))
        {
            (await scope.ExecuteAsync(
                    InsertCapacitySql,
                    CapacityParameters(Guid.CreateVersion7(), world.OrgA, world.ProjectA)))
                .ShouldBe(1);
            await scope.CommitAsync();
        }

        // AC3's consume statement matches exactly one row by (org, project, period, period start); a
        // second row for the same tuple would make "did the cap hold" ambiguous, so the key refuses it.
        await using (var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database)))
        {
            var error = await scope.CaptureErrorAsync(
                InsertCapacitySql,
                CapacityParameters(Guid.CreateVersion7(), world.OrgA, world.ProjectA));

            error.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
            error.ConstraintName.ShouldBe("healing_llm_budget_period_key");
        }
    }
}
