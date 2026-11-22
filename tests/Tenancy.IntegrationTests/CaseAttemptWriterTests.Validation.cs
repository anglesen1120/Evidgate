using Evidgate.Modules.Orchestrator.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class CaseAttemptWriterTests
{
    // ------------------------------------------------------------------ unknown executions

    [Fact]
    public async Task WriteAttempt_UnplannedExecutionKey_UnknownExecution_NoRow()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world);

        await using var scope = await AttemptScope.BeginAsync(world.Api, world.Org);
        var outcome = (await scope.WriteAsync(plan.RunId, new CaseAttemptInput(UnplannedKey, 1, "passed")))
            .ShouldHaveSingleItem();

        outcome.Status.ShouldBe(CaseAttemptWriteStatus.UnknownExecution);
        outcome.IsRefusal.ShouldBeTrue();
        (await CountRowsAsync(world, plan.RunId)).ShouldBe(0);
    }

    [Fact]
    public async Task WriteAttempt_OtherRunsKey_UnknownExecution()
    {
        await using var world = await StartAsync();
        var runA = await SeedPlanAsync(world);
        var runB = await SeedPlanAsync(world);

        // The key is planned, but in run A: called for run B it is unknown.
        await using var scope = await AttemptScope.BeginAsync(world.Api, world.Org);
        var outcome = (await scope.WriteAsync(runB.RunId, new CaseAttemptInput(runA.Executions[0].ExecutionKey, 1, "passed")))
            .ShouldHaveSingleItem();

        outcome.Status.ShouldBe(CaseAttemptWriteStatus.UnknownExecution);
        (await CountRowsAsync(world, runB.RunId)).ShouldBe(0);
    }

    [Fact]
    public async Task WriteAttempt_CrossTenant_UnknownExecution()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world);

        // Another org's run: RLS makes it invisible, so the writer reports the same unknown as an unplanned key.
        var (otherOrg, otherProject) = await CreateOtherTenantAsync(world, "rival");
        var otherPlan = await PlannedExecutionSeeder.SeedPlanAsync(_fixture, world.Database, otherOrg, 1);

        await using (var otherScope = await AttemptScope.BeginAsync(world.Api, otherOrg))
        {
            var outcome = (await otherScope.WriteAsync(plan.RunId, new CaseAttemptInput(plan.Executions[0].ExecutionKey, 1, "passed")))
                .ShouldHaveSingleItem();
            outcome.Status.ShouldBe(CaseAttemptWriteStatus.UnknownExecution);
        }

        // A project-scoped scope for another project of the same org: the run is not visible either.
        await using (var projectScope = await AttemptScope.BeginAsync(world.Api, world.Org, otherProject))
        {
            var outcome = (await projectScope.WriteAsync(plan.RunId, new CaseAttemptInput(plan.Executions[0].ExecutionKey, 1, "passed")))
                .ShouldHaveSingleItem();
            outcome.Status.ShouldBe(CaseAttemptWriteStatus.UnknownExecution);
        }

        // The control: under the run's own org the very same call is accepted.
        await using (var own = await AttemptScope.BeginAsync(world.Api, world.Org))
        {
            (await own.WriteAsync(plan.RunId, new CaseAttemptInput(plan.Executions[0].ExecutionKey, 1, "passed")))
                .ShouldHaveSingleItem().Status.ShouldBe(CaseAttemptWriteStatus.Accepted);
            await own.CommitAsync();
        }

        (await CountRowsAsync(world, plan.RunId)).ShouldBe(1);
        (await CountRowsAsync(world, otherPlan.RunId)).ShouldBe(0);
    }

    // ------------------------------------------------------------------ validation

    [Theory]
    [InlineData("execution_key_invalid")]
    [InlineData("attempt_no_invalid")]
    [InlineData("raw_status_invalid")]
    [InlineData("duration_invalid")]
    [InlineData("timestamps_invalid")]
    public async Task WriteAttempt_InvalidInputs_Refused_ZeroStatements(string caseName)
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world);
        var key = plan.Executions[0].ExecutionKey;

        await using var scope = await AttemptScope.BeginAsync(world.Api, world.Org);
        world.Api.Statements.Clear();
        var outcome = (await scope.WriteAsync(plan.RunId, InvalidInput(caseName, key))).ShouldHaveSingleItem();

        outcome.Status.ShouldBe(CaseAttemptWriteStatus.Invalid);
        outcome.Reason.ShouldBe(caseName);
        outcome.IsRefusal.ShouldBeTrue();
        CaseAttemptStatements(world).ShouldBeEmpty();
        (await CountRowsAsync(world, plan.RunId)).ShouldBe(0);
    }

    private static CaseAttemptInput InvalidInput(string caseName, string key) => caseName switch
    {
        "execution_key_invalid" => new CaseAttemptInput(new string('z', 64), 1, "passed"),
        "attempt_no_invalid" => new CaseAttemptInput(key, 0, "passed"),
        "raw_status_invalid" => new CaseAttemptInput(key, 1, "bad\0status"),
        "duration_invalid" => new CaseAttemptInput(key, 1, "passed", DurationMs: -1),
        _ => new CaseAttemptInput(
            key, 1, "passed",
            new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 1, 1, 11, 0, 0, TimeSpan.Zero)),
    };

    [Fact]
    public async Task WriteAttempt_RawStatus_65CodePoints_And_LoneSurrogate_Refused()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world);
        var key = plan.Executions[0].ExecutionKey;

        await using var scope = await AttemptScope.BeginAsync(world.Api, world.Org);

        var tooLong = (await scope.WriteAsync(plan.RunId, new CaseAttemptInput(key, 1, new string('x', 65))))
            .ShouldHaveSingleItem();
        tooLong.Status.ShouldBe(CaseAttemptWriteStatus.Invalid);
        tooLong.Reason.ShouldBe("raw_status_invalid");

        var loneSurrogate = (await scope.WriteAsync(plan.RunId, new CaseAttemptInput(key, 1, "a\ud800b")))
            .ShouldHaveSingleItem();
        loneSurrogate.Status.ShouldBe(CaseAttemptWriteStatus.Invalid);
        loneSurrogate.Reason.ShouldBe("raw_status_invalid");

        (await CountRowsAsync(world, plan.RunId)).ShouldBe(0);
    }

    [Fact]
    public async Task WriteAttempt_RawStatus_64AstralCodePoints_IsAccepted()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world);
        var planned = plan.Executions[0];
        var raw = string.Concat(Enumerable.Repeat("😀", 64)); // 64 code points, 128 UTF-16 units

        await using var scope = await AttemptScope.BeginAsync(world.Api, world.Org);
        (await scope.WriteAsync(plan.RunId, new CaseAttemptInput(planned.ExecutionKey, 1, raw)))
            .ShouldHaveSingleItem().Status.ShouldBe(CaseAttemptWriteStatus.Accepted);
        await scope.CommitAsync();

        (await _fixture.ScalarAsync<string>(world.Database, $"select raw_status from public.case_attempt where run_id = '{plan.RunId}'"))
            .ShouldBe(raw);
        (await NormalizedAsync(world, plan.RunId, planned.ExecutionKey)).ShouldBe("error");
    }

    [Fact]
    public async Task WriteAttempt_EmptyBatch_And_Over500_Throw()
    {
        await using var world = await StartAsync();

        // No tenant scope is open, so an ArgumentException here proves the size guard runs before scopes.Require().
        await using var services = world.Api.Services.CreateAsyncScope();
        var writer = services.ServiceProvider.GetRequiredService<ICaseAttemptWriter>();
        var attempts = Enumerable.Range(0, 501)
            .Select(i => new CaseAttemptInput(UnplannedKey, i + 1, "passed"))
            .ToArray();

        await Should.ThrowAsync<ArgumentException>(
            () => writer.WriteAttemptsAsync(Guid.CreateVersion7(), [], TestContext.Current.CancellationToken).AsTask());
        await Should.ThrowAsync<ArgumentException>(
            () => writer.WriteAttemptsAsync(Guid.CreateVersion7(), attempts, TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task WriteAttempt_NullElement_Throws_ZeroStatements()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world);

        // No tenant scope is open, so an ArgumentNullException here proves the element guard runs before
        // scopes.Require() like the batch-size guard, and the message names the offending index.
        await using var services = world.Api.Services.CreateAsyncScope();
        var writer = services.ServiceProvider.GetRequiredService<ICaseAttemptWriter>();

        var error = await Should.ThrowAsync<ArgumentNullException>(
            () => writer.WriteAttemptsAsync(
                plan.RunId,
                new CaseAttemptInput[] { null! },
                TestContext.Current.CancellationToken).AsTask());

        error.ParamName.ShouldBe("attempts");
        error.Message.ShouldContain("attempts[0]");
        CaseAttemptStatements(world).ShouldBeEmpty();
    }
}
