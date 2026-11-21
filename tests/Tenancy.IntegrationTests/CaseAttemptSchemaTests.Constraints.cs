using Evidgate.Modules.Orchestrator.Contracts;
using Evidgate.Modules.Orchestrator.Domain;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class CaseAttemptSchemaTests
{
    // ------------------------------------------------------------------ (c) cross-org composite FKs

    [Fact]
    public async Task CaseAttempt_CrossTenant_404()
    {
        await using var world = await StartAsync();
        var mine = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var myRun = await SeedRunAndPlanAsync(world, mine);

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await scope.ExecuteAsync(InsertCaseAttemptSql(world.Org, myRun, mine.CaseId))).ShouldBe(1);
            await scope.CommitAsync();
        }

        // Under org B's tenant scope a SELECT of org A's attempt returns zero rows (RLS).
        var (otherOrg, otherProject) = await CreateOtherTenantAsync(world, "beta");
        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), otherOrg))
        {
            (await scope.ScalarAsync<long>("select count(*) from public.case_attempt")).ShouldBe(0);
        }

        // Inserting an attempt for org A's run under org B's scope is refused — the RLS with check
        // passes because the tenant setting is org B, so the rejection is the composite run FK.
        var error = await CaptureAppErrorAsync(
            world, otherOrg, InsertCaseAttemptSql(otherOrg, myRun, mine.CaseId));

        error.SqlState.ShouldBe("23503");
        error.ConstraintName.ShouldBe("case_attempt_run_fk");
    }

    [Fact]
    public async Task CaseAttempt_PlannedExecution_OtherOrg_ViolatesFk()
    {
        await using var world = await StartAsync();
        var mine = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var myRun = await SeedRunAndPlanAsync(world, mine);
        var (otherOrg, otherProject) = await CreateOtherTenantAsync(world, "beta");
        var theirs = await SeedParentsAsync(world.Database, otherOrg, otherProject);
        // Their run plans a DIFFERENT execution key, so the only key that can reject the row below is
        // the composite planned-execution key: (otherOrg, theirRun, ExecutionKey) has no planned row.
        var theirRun = await SeedRunAndPlanAsync(world, otherOrg, otherProject, theirs, OtherExecutionKey);

        // The row is filed under the other org but names this org's execution key on their run.
        var error = await CaptureAppErrorAsync(
            world, otherOrg, InsertCaseAttemptSql(otherOrg, theirRun, theirs.CaseId, executionKey: ExecutionKey));

        error.SqlState.ShouldBe("23503");
        error.ConstraintName.ShouldBe("case_attempt_planned_execution_fk");

        // Positive control: the same row with the key their run did plan is accepted, so the refusal
        // above is the cross-org mismatch and not a bad seed.
        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), otherOrg))
        {
            (await scope.ExecuteAsync(InsertCaseAttemptSql(
                otherOrg, theirRun, theirs.CaseId, executionKey: OtherExecutionKey))).ShouldBe(1);
            await scope.CommitAsync();
        }
    }

    [Fact]
    public async Task CaseAttempt_CaseId_OtherOrg_ViolatesFk()
    {
        await using var world = await StartAsync();
        var mine = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var (otherOrg, otherProject) = await CreateOtherTenantAsync(world, "beta");
        var theirs = await SeedParentsAsync(world.Database, otherOrg, otherProject);
        var theirRun = await SeedRunAndPlanAsync(world, otherOrg, otherProject, theirs);

        // The row is filed under the other org and names their run and their planned key, but points
        // at this org's case: only the composite case key can reject it.
        var error = await CaptureAppErrorAsync(
            world, otherOrg, InsertCaseAttemptSql(otherOrg, theirRun, mine.CaseId));

        error.SqlState.ShouldBe("23503");
        error.ConstraintName.ShouldBe("case_attempt_case_fk");

        // Positive control: the same row naming their own case is accepted, so the refusal above is
        // the cross-org mismatch and not a bad seed.
        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), otherOrg))
        {
            (await scope.ExecuteAsync(InsertCaseAttemptSql(otherOrg, theirRun, theirs.CaseId))).ShouldBe(1);
            await scope.CommitAsync();
        }
    }

    [Fact]
    public async Task Artifact_AttemptId_OtherOrg_ViolatesFk()
    {
        // 06.1.1-T1's deferred composite attempt key, added by 0032.
        await using var world = await StartAsync();
        var mine = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var myRun = await SeedRunAndPlanAsync(world, mine);
        var attemptId = Guid.CreateVersion7();

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await scope.ExecuteAsync(InsertCaseAttemptSql(world.Org, myRun, mine.CaseId, id: attemptId))).ShouldBe(1);
            await scope.CommitAsync();
        }

        var (otherOrg, otherProject) = await CreateOtherTenantAsync(world, "beta");
        var theirs = await SeedParentsAsync(world.Database, otherOrg, otherProject);
        var theirRun = await SeedRunAndPlanAsync(world, otherOrg, otherProject, theirs);

        // The artifact is filed under the other org and names their run, but points at this org's
        // attempt: only the composite attempt key can reject it.
        var error = await CaptureAppErrorAsync(
            world, otherOrg, InsertArtifactSql(otherOrg, otherProject, theirRun, attemptId));

        error.SqlState.ShouldBe("23503");
        error.ConstraintName.ShouldBe("artifact_attempt_fk");

        // Positive control: an artifact under the other org naming their own attempt is accepted, so
        // the refusal above is the cross-org mismatch and not a bad seed.
        var theirAttemptId = Guid.CreateVersion7();
        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), otherOrg))
        {
            (await scope.ExecuteAsync(InsertCaseAttemptSql(otherOrg, theirRun, theirs.CaseId, id: theirAttemptId))).ShouldBe(1);
            (await scope.ExecuteAsync(InsertArtifactSql(otherOrg, otherProject, theirRun, theirAttemptId))).ShouldBe(1);
            await scope.CommitAsync();
        }
    }

    [Fact]
    public async Task Artifact_AttemptId_Null_IsAccepted()
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var runId = await SeedRunAndPlanAsync(world, parents);

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        (await scope.ExecuteAsync(InsertArtifactSql(world.Org, world.Project, runId))).ShouldBe(1);
        await scope.CommitAsync();
    }

    // ------------------------------------------------------------------ (d) CHECK boundaries

    [Fact]
    public async Task NormalizedStatus_OutsideTheSix_FailsCheck()
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var runId = await SeedRunAndPlanAsync(world, parents);

        var error = await CaptureAppErrorAsync(
            world, world.Org, InsertCaseAttemptSql(world.Org, runId, parents.CaseId, normalizedStatus: "flaky"));

        error.SqlState.ShouldBe("23514");
        error.ConstraintName.ShouldBe("case_attempt_normalized_status_check");
    }

    [Fact]
    public async Task AttemptNo_Zero_FailsCheck()
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var runId = await SeedRunAndPlanAsync(world, parents);

        var error = await CaptureAppErrorAsync(
            world, world.Org, InsertCaseAttemptSql(world.Org, runId, parents.CaseId, attemptNo: 0));

        error.SqlState.ShouldBe("23514");
        error.ConstraintName.ShouldBe("case_attempt_attempt_no_check");
    }

    [Fact]
    public async Task RawStatus_TooLong_FailsCheck()
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var runId = await SeedRunAndPlanAsync(world, parents);

        var error = await CaptureAppErrorAsync(
            world, world.Org, InsertCaseAttemptSql(world.Org, runId, parents.CaseId, rawStatus: new string('x', 65)));

        error.SqlState.ShouldBe("23514");
        error.ConstraintName.ShouldBe("case_attempt_raw_status_check");
    }

    [Fact]
    public async Task Duplicate_RunExecutionAttempt_FailsUnique()
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var runId = await SeedRunAndPlanAsync(world, parents);

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await scope.ExecuteAsync(InsertCaseAttemptSql(world.Org, runId, parents.CaseId))).ShouldBe(1);
            await scope.CommitAsync();
        }

        var error = await CaptureAppErrorAsync(
            world, world.Org, InsertCaseAttemptSql(world.Org, runId, parents.CaseId));

        error.SqlState.ShouldBe("23505");
        error.ConstraintName.ShouldBe("case_attempt_run_execution_attempt_key");
    }

    [Fact]
    public async Task UnplannedExecutionKey_FailsFk()
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var runId = await SeedRunAndPlanAsync(world, parents);

        // A key with no planned_execution row for this run: 04 refuses it with 422
        // unknown_execution_key, so the database key is the second line of defence.
        var error = await CaptureAppErrorAsync(
            world, world.Org, InsertCaseAttemptSql(world.Org, runId, parents.CaseId, executionKey: new string('b', 64)));

        error.SqlState.ShouldBe("23503");
        error.ConstraintName.ShouldBe("case_attempt_planned_execution_fk");
    }

    // ------------------------------------------------------------------ (e) raw + normalised status

    [Fact]
    public async Task WriteAttempt_EmptyRawStatus_IsAcceptedAndNormalizedError()
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var runId = await SeedRunAndPlanAsync(world, parents);

        // The envelope allows an empty status (maxLength 64, no minLength), so the writer accepts it
        // and it normalises to 'error'.
        CaseAttemptStatus.Normalize(string.Empty).ShouldBe("error");

        await using var scope = await AttemptScope.BeginAsync(world.Api, world.Org);
        (await scope.WriteAsync(runId, new CaseAttemptInput(ExecutionKey, 1, string.Empty)))
            .ShouldHaveSingleItem().Status.ShouldBe(CaseAttemptWriteStatus.Accepted);
        await scope.CommitAsync();

        await using var read = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        (await read.ScalarAsync<string>(
            $"select raw_status from public.case_attempt where org_id = '{world.Org}'")).ShouldBe(string.Empty);
        (await read.ScalarAsync<string>(
            $"select normalized_status from public.case_attempt where org_id = '{world.Org}'")).ShouldBe("error");
    }
}
