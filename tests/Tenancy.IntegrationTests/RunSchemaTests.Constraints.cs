using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class RunSchemaTests
{
    // ------------------------------------------------------------------ (c) cross-org composite FKs

    [Fact]
    public async Task Run_Manifest_OtherOrg_ViolatesFk()
    {
        await using var world = await StartAsync();
        var mine = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var (otherOrg, otherProject) = await CreateOtherTenantAsync(world, "beta");
        var theirs = await SeedParentsAsync(world.Database, otherOrg, otherProject);

        // The row is filed under the other org but points at this org's manifest: only the composite
        // key can reject it (the RLS with check passes because the tenant setting is the other org).
        var error = await CaptureAppErrorAsync(
            world, otherOrg, InsertRunSql(otherOrg, otherProject, theirs, manifestId: mine.ManifestId));

        error.SqlState.ShouldBe("23503");
        error.ConstraintName.ShouldBe("run_manifest_fk");
    }

    [Fact]
    public async Task PlannedExecution_Run_OtherOrg_ViolatesFk()
    {
        await using var world = await StartAsync();
        var mine = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var myRun = await InsertRunAsync(world, mine);
        var (otherOrg, otherProject) = await CreateOtherTenantAsync(world, "beta");
        var theirs = await SeedParentsAsync(world.Database, otherOrg, otherProject);

        var error = await CaptureAppErrorAsync(
            world, otherOrg, InsertPlannedExecutionSql(otherOrg, myRun, theirs.TestRevisionId));

        error.SqlState.ShouldBe("23503");
        error.ConstraintName.ShouldBe("planned_execution_run_fk");
    }

    [Fact]
    public async Task PlannedExecution_CaseRevision_OtherOrg_ViolatesFk()
    {
        await using var world = await StartAsync();
        var mine = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var (otherOrg, otherProject) = await CreateOtherTenantAsync(world, "beta");
        var theirs = await SeedParentsAsync(world.Database, otherOrg, otherProject);
        var theirRun = await InsertRunAsync(world, otherOrg, otherProject, theirs);

        var error = await CaptureAppErrorAsync(
            world, otherOrg, InsertPlannedExecutionSql(otherOrg, theirRun, mine.TestRevisionId));

        error.SqlState.ShouldBe("23503");
        error.ConstraintName.ShouldBe("planned_execution_case_revision_fk");
    }

    [Fact]
    public async Task RunId_OtherOrg_ViolatesFk()
    {
        // 06.1.1 §9: the artifact's deferred composite run key, added by 0031.
        await using var world = await StartAsync();
        var mine = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var myRun = await InsertRunAsync(world, mine);
        var (otherOrg, otherProject) = await CreateOtherTenantAsync(world, "beta");

        var error = await CaptureAppErrorAsync(world, otherOrg, InsertArtifactSql(otherOrg, otherProject, myRun));

        error.SqlState.ShouldBe("23503");
        error.ConstraintName.ShouldBe("artifact_run_fk");
    }

    [Fact]
    public async Task ProducerSlot_RunId_OtherOrg_ViolatesFk()
    {
        // 04.1.2-T4's deferred composite run key, added by 0031.
        await using var world = await StartAsync();
        var mine = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var myRun = await InsertRunAsync(world, mine);
        var (otherOrg, _) = await CreateOtherTenantAsync(world, "beta");

        var error = await CaptureAppErrorAsync(world, otherOrg, InsertProducerSlotSql(otherOrg, myRun));

        error.SqlState.ShouldBe("23503");
        error.ConstraintName.ShouldBe("producer_slot_run_fk");
    }

    // ------------------------------------------------------------------ (d) run CHECK boundaries

    [Theory]
    [InlineData(-1)]
    [InlineData(525601)]
    public async Task LateWindowMinutes_OutsideWidenedBounds_FailsCheck(int minutes)
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);

        var error = await CaptureAppErrorAsync(
            world, world.Org, InsertRunSql(world.Org, world.Project, parents, lateWindowMinutes: minutes));

        error.SqlState.ShouldBe("23514");
        error.ConstraintName.ShouldBe("run_late_window_minutes_check");
    }

    // 0033 widens the bound to 0..525600 (owner decision Q4): both ends are accepted values, not refusals.
    [Theory]
    [InlineData(0)]
    [InlineData(525600)]
    public async Task LateWindowMinutes_WidenedBounds_AreAccepted(int minutes)
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await scope.ExecuteAsync(InsertRunSql(world.Org, world.Project, parents, lateWindowMinutes: minutes))).ShouldBe(1);
            await scope.CommitAsync();
        }

        (await _fixture.ScalarAsync<int>(
            world.Database,
            $"select late_window_minutes from public.run where org_id = '{world.Org}'")).ShouldBe(minutes);
    }

    [Fact]
    public async Task State_Unknown_FailsCheck()
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);

        var error = await CaptureAppErrorAsync(
            world, world.Org, InsertRunSql(world.Org, world.Project, parents, state: "paused"));

        error.SqlState.ShouldBe("23514");
        error.ConstraintName.ShouldBe("run_state_check");
    }

    [Fact]
    public async Task RerunScope_Unknown_FailsCheck()
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);

        var error = await CaptureAppErrorAsync(
            world, world.Org, InsertRunSql(world.Org, world.Project, parents, rerunScope: "everything"));

        error.SqlState.ShouldBe("23514");
        error.ConstraintName.ShouldBe("run_rerun_scope_check");
    }

    [Fact]
    public async Task Purpose_ReplayWithoutGrant_FailsCheck()
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);

        var error = await CaptureAppErrorAsync(
            world, world.Org, InsertRunSql(world.Org, world.Project, parents, purpose: "replay"));

        error.SqlState.ShouldBe("23514");
        error.ConstraintName.ShouldBe("run_replay_grant_check");
    }

    [Fact]
    public async Task Purpose_Unknown_FailsCheck()
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);

        var error = await CaptureAppErrorAsync(
            world, world.Org, InsertRunSql(world.Org, world.Project, parents, purpose: "adhoc"));

        error.SqlState.ShouldBe("23514");
        error.ConstraintName.ShouldBe("run_purpose_check");
    }

    [Fact]
    public async Task TestCommit_TooLong_FailsCheck()
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);

        var error = await CaptureAppErrorAsync(
            world, world.Org, InsertRunSql(world.Org, world.Project, parents, testCommit: new string('a', 129)));

        error.SqlState.ShouldBe("23514");
        error.ConstraintName.ShouldBe("run_test_commit_check");
    }

    [Theory]
    [InlineData("{\"provider\":\"github_actions\",\"repository_id\":1,\"run_id\":2}")]
    [InlineData("{\"provider\":\"github_actions\",\"repository_id\":1,\"run_id\":2,\"run_attempt\":1,\"extra\":true}")]
    [InlineData("[1,2,3]")]
    public async Task CiRunRef_OutsideTheExactShape_FailsCheck(string ciRunRef)
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);

        var error = await CaptureAppErrorAsync(
            world, world.Org, InsertRunSql(world.Org, world.Project, parents, ciRunRef: ciRunRef));

        error.SqlState.ShouldBe("23514");
        error.ConstraintName.ShouldBe("run_ci_run_ref_check");
    }

    [Fact]
    public async Task CiRunRef_ExactShape_IsAccepted()
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        (await scope.ExecuteAsync(InsertRunSql(
            world.Org,
            world.Project,
            parents,
            ciRunRef: "{\"provider\":\"github_actions\",\"repository_id\":1,\"run_id\":2,\"run_attempt\":1}"))).ShouldBe(1);
        await scope.CommitAsync();
    }

    [Fact]
    public async Task Components_NotAnObject_FailsCheck()
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);

        var error = await CaptureAppErrorAsync(
            world, world.Org, InsertRunSql(world.Org, world.Project, parents, components: "'[]'::jsonb"));

        error.SqlState.ShouldBe("23514");
        error.ConstraintName.ShouldBe("run_components_check");
    }

    // ------------------------------------------------------------------ (e) planned_execution CHECK boundaries

    [Theory]
    [InlineData("PLACEHOLDER_EXECUTION_KEY")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("../../etc/passwd")]
    public async Task ExecutionKey_OutsideTheHexShape_FailsCheck(string executionKey)
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var runId = await InsertRunAsync(world, parents);

        var error = await CaptureAppErrorAsync(
            world, world.Org, InsertPlannedExecutionSql(world.Org, runId, parents.TestRevisionId, executionKey: executionKey));

        error.SqlState.ShouldBe("23514");
        error.ConstraintName.ShouldBe("planned_execution_execution_key_check");
    }

    [Theory]
    [InlineData("sha256:short")]
    [InlineData("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    public async Task VariantHash_OutsideTheContentAddress_FailsCheck(string variantHash)
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var runId = await InsertRunAsync(world, parents);

        var error = await CaptureAppErrorAsync(
            world, world.Org, InsertPlannedExecutionSql(world.Org, runId, parents.TestRevisionId, variantHash: variantHash));

        error.SqlState.ShouldBe("23514");
        error.ConstraintName.ShouldBe("planned_execution_variant_hash_check");
    }

    [Theory]
    [InlineData("shard-0")]
    [InlineData("shard-")]
    [InlineData("1")]
    public async Task ShardId_OutsideTheDerivedShape_FailsCheck(string shardId)
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var runId = await InsertRunAsync(world, parents);

        var error = await CaptureAppErrorAsync(
            world, world.Org, InsertPlannedExecutionSql(world.Org, runId, parents.TestRevisionId, shardId: shardId));

        error.SqlState.ShouldBe("23514");
        error.ConstraintName.ShouldBe("planned_execution_shard_id_check");
    }

    [Fact]
    public async Task TargetEnv_TooLong_FailsCheck()
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var runId = await InsertRunAsync(world, parents);

        var error = await CaptureAppErrorAsync(
            world, world.Org, InsertPlannedExecutionSql(world.Org, runId, parents.TestRevisionId, targetEnv: new string('e', 65)));

        error.SqlState.ShouldBe("23514");
        error.ConstraintName.ShouldBe("planned_execution_target_env_check");
    }

    [Fact]
    public async Task SameRun_ExecutionKey_IsUnique()
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var runId = await InsertRunAsync(world, parents);

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await scope.ExecuteAsync(InsertPlannedExecutionSql(world.Org, runId, parents.TestRevisionId))).ShouldBe(1);
            await scope.CommitAsync();
        }

        var error = await CaptureAppErrorAsync(
            world, world.Org, InsertPlannedExecutionSql(world.Org, runId, parents.TestRevisionId));

        error.SqlState.ShouldBe("23505");
        error.ConstraintName.ShouldBe("planned_execution_run_execution_key");
    }
}
