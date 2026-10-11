using Evidgate.Modules.Orchestrator.Domain;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// QA probes for 05.3.3-T1 (migration 0032) that the schema suite does not reach: the CHECK
/// boundaries of <c>case_attempt</c> (raw-status length, duration, the started/finished order,
/// the execution-key shape), the six accepted normalised values, the un-keyed <c>job_id</c>, the
/// operator role's visibility, and the same-org artifact attempt FK.
/// </summary>
public sealed partial class CaseAttemptSchemaTests
{
    /// <summary>Insert shaped like the writer's, with every bounded column as a bound parameter.</summary>
    private const string AttemptInsertSql =
        "insert into public.case_attempt "
        + "(id, org_id, run_id, execution_key, case_id, attempt_no, raw_status, normalized_status, started_at, finished_at, duration_ms, job_id) "
        + "values (@id, @org_id, @run_id, @execution_key, @case_id, @attempt_no, @raw_status, @normalized_status, @started_at, @finished_at, @duration_ms, @job_id)";

    /// <summary>
    /// Parameters for <see cref="AttemptInsertSql"/>. A null <paramref name="startedAt"/> means "now";
    /// a null <paramref name="finishedAt"/>, <paramref name="durationMs"/> or <paramref name="jobId"/>
    /// means SQL NULL.
    /// </summary>
    private static (string Name, object Value)[] AttemptParams(
        Guid org,
        Guid runId,
        Guid caseId,
        Guid? id = null,
        string raw = "passed",
        string normalized = "passed",
        string executionKey = ExecutionKey,
        int attemptNo = 1,
        object? startedAt = null,
        object? finishedAt = null,
        object? durationMs = null,
        object? jobId = null) =>
        new (string Name, object Value)[]
        {
            ("id", id ?? Guid.CreateVersion7()),
            ("org_id", org),
            ("run_id", runId),
            ("execution_key", executionKey),
            ("case_id", caseId),
            ("attempt_no", attemptNo),
            ("raw_status", raw),
            ("normalized_status", normalized),
            ("started_at", startedAt ?? DateTimeOffset.UtcNow),
            ("finished_at", finishedAt ?? DBNull.Value),
            ("duration_ms", durationMs ?? DBNull.Value),
            ("job_id", jobId ?? DBNull.Value),
        };

    // ------------------------------------------------------------------ (f) QA: CHECK boundaries

    [Fact]
    public async Task CaseAttempt_RawStatus_Exactly64Characters_IsAccepted()
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var runId = await SeedRunAndPlanAsync(world, parents);

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);

        // The upper bound of case_attempt_raw_status_check (char_length between 0 and 64).
        var raw = new string('x', 64);
        (await scope.ExecuteAsync(AttemptInsertSql, AttemptParams(world.Org, runId, parents.CaseId, raw: raw, normalized: "error")))
            .ShouldBe(1);
    }

    [Fact]
    public async Task CaseAttempt_RawStatus_EmptyString_IsStoredVerbatim_WithTheNormalisedError()
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var runId = await SeedRunAndPlanAsync(world, parents);
        var id = Guid.CreateVersion7();
        var normalized = CaseAttemptStatus.Normalize(string.Empty);

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        (await scope.ExecuteAsync(
            AttemptInsertSql,
            AttemptParams(world.Org, runId, parents.CaseId, id: id, raw: string.Empty, normalized: normalized)))
            .ShouldBe(1);

        // Round-trip: the raw value is stored as sent (empty), the normalised value is the server's error.
        (await scope.ScalarAsync<string>("select raw_status from public.case_attempt where id = @id", ("id", id)))
            .ShouldBe(string.Empty);
        (await scope.ScalarAsync<string>("select normalized_status from public.case_attempt where id = @id", ("id", id)))
            .ShouldBe("error");
    }

    [Fact]
    public async Task CaseAttempt_RawStatus_IsStoredCaseSensitively_AndNormalisesToError()
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var runId = await SeedRunAndPlanAsync(world, parents);
        var id = Guid.CreateVersion7();
        var normalized = CaseAttemptStatus.Normalize("PASSED");

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        (await scope.ExecuteAsync(
            AttemptInsertSql,
            AttemptParams(world.Org, runId, parents.CaseId, id: id, raw: "PASSED", normalized: normalized)))
            .ShouldBe(1);

        (await scope.ScalarAsync<string>("select raw_status from public.case_attempt where id = @id", ("id", id)))
            .ShouldBe("PASSED");
        (await scope.ScalarAsync<string>("select normalized_status from public.case_attempt where id = @id", ("id", id)))
            .ShouldBe("error");
    }

    [Fact]
    public async Task CaseAttempt_FinishedBeforeStarted_FailsCheck()
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var runId = await SeedRunAndPlanAsync(world, parents);

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        var started = DateTimeOffset.UtcNow;
        var error = await scope.CaptureErrorAsync(
            AttemptInsertSql,
            AttemptParams(world.Org, runId, parents.CaseId, startedAt: started, finishedAt: started.AddHours(-1)));

        error.SqlState.ShouldBe("23514");
        error.ConstraintName.ShouldBe("case_attempt_finished_after_started_check");
    }

    [Fact]
    public async Task CaseAttempt_NegativeDuration_FailsCheck()
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var runId = await SeedRunAndPlanAsync(world, parents);

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        var error = await scope.CaptureErrorAsync(
            AttemptInsertSql,
            AttemptParams(world.Org, runId, parents.CaseId, durationMs: -1));

        error.SqlState.ShouldBe("23514");
        error.ConstraintName.ShouldBe("case_attempt_duration_ms_check");
    }

    [Fact]
    public async Task CaseAttempt_ZeroDuration_IsAccepted()
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var runId = await SeedRunAndPlanAsync(world, parents);

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);

        // duration_ms >= 0 is inclusive: a zero-length attempt is valid.
        (await scope.ExecuteAsync(AttemptInsertSql, AttemptParams(world.Org, runId, parents.CaseId, durationMs: 0)))
            .ShouldBe(1);
    }

    [Fact]
    public async Task CaseAttempt_NullStartedAt_WithFinishedAt_IsAccepted()
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var runId = await SeedRunAndPlanAsync(world, parents);

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);

        // The order check is "finished_at is null or started_at is null or finished_at >= started_at":
        // a missing started_at must not trip it.
        (await scope.ExecuteAsync(
            AttemptInsertSql,
            AttemptParams(world.Org, runId, parents.CaseId, startedAt: DBNull.Value, finishedAt: DateTimeOffset.UtcNow)))
            .ShouldBe(1);
    }

    [Fact]
    public async Task CaseAttempt_ExecutionKey_Uppercase_FailsCheck()
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var runId = await SeedRunAndPlanAsync(world, parents);

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);

        // The 00 §7 key is lowercase hex only (^[0-9a-f]{64}$).
        var error = await scope.CaptureErrorAsync(
            AttemptInsertSql,
            AttemptParams(world.Org, runId, parents.CaseId, executionKey: new string('A', 64)));

        error.SqlState.ShouldBe("23514");
        error.ConstraintName.ShouldBe("case_attempt_execution_key_check");
    }

    [Fact]
    public async Task CaseAttempt_ExecutionKey_63Characters_FailsCheck()
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var runId = await SeedRunAndPlanAsync(world, parents);

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);

        var error = await scope.CaptureErrorAsync(
            AttemptInsertSql,
            AttemptParams(world.Org, runId, parents.CaseId, executionKey: new string('a', 63)));

        error.SqlState.ShouldBe("23514");
        error.ConstraintName.ShouldBe("case_attempt_execution_key_check");
    }

    [Fact]
    public async Task CaseAttempt_JobId_HasNoForeignKey_AnyUuidIsAccepted()
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var runId = await SeedRunAndPlanAsync(world, parents);

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);

        // job_id is deferred: no job table exists yet (05.7.2-T1), so a uuid that names no job must be accepted.
        (await scope.ExecuteAsync(
            AttemptInsertSql,
            AttemptParams(world.Org, runId, parents.CaseId, jobId: Guid.CreateVersion7())))
            .ShouldBe(1);
    }

    [Fact]
    public async Task CaseAttempt_EachOfTheSixNormalizedStatuses_IsAccepted()
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var runId = await SeedRunAndPlanAsync(world, parents);

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);

        // 00 §8.3: {passed, failed, skipped, timed_out, error, blocked}. Each value gets its own attempt_no.
        string[] statuses = ["passed", "failed", "skipped", "timed_out", "error", "blocked"];
        var attemptNo = 1;
        foreach (var status in statuses)
        {
            (await scope.ExecuteAsync(
                AttemptInsertSql,
                AttemptParams(world.Org, runId, parents.CaseId, normalized: status, attemptNo: attemptNo++)))
                .ShouldBe(1, status);
        }
    }

    // ------------------------------------------------------------------ (g) QA: visibility and FK positive path

    [Fact]
    public async Task CaseAttempt_OperatorRole_SeesNoRows_WhileTheAppRoleSeesTheOrgsRow()
    {
        // Pins current behaviour: evidgate_operator has SELECT but no RLS policy, so FORCE RLS
        // yields zero rows. The app-role count is the control, so the zero is not vacuous.
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var runId = await SeedRunAndPlanAsync(world, parents);

        await using (var writer = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await writer.ExecuteAsync(AttemptInsertSql, AttemptParams(world.Org, runId, parents.CaseId))).ShouldBe(1);
            await writer.CommitAsync();
        }

        await using (var app = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await app.ScalarAsync<long>("select count(*) from public.case_attempt")).ShouldBe(1);
        }

        await using var operatorScope = await TenantScope.BeginAsync(_fixture.OperatorConnectionString(world.Database), world.Org);
        (await operatorScope.ScalarAsync<long>("select count(*) from public.case_attempt")).ShouldBe(0);
    }

    [Fact]
    public async Task Artifact_AttemptId_SameOrg_IsAccepted()
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var runId = await SeedRunAndPlanAsync(world, parents);
        var attemptId = Guid.CreateVersion7();

        await using (var writer = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await writer.ExecuteAsync(AttemptInsertSql, AttemptParams(world.Org, runId, parents.CaseId, id: attemptId))).ShouldBe(1);
            await writer.CommitAsync();
        }

        // The positive path of artifact_attempt_fk: a same-org attempt id on an artifact of the same run.
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        (await scope.ExecuteAsync(InsertArtifactSql(world.Org, world.Project, runId, attemptId))).ShouldBe(1);
    }
}
