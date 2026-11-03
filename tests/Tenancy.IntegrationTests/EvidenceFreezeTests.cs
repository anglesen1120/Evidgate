// IEvidenceFreeze through a real PostgreSQL and the real host — the one cross-module call 05's
// reconciler makes to freeze a run's evidence. Every test opens the caller's tenant transaction itself
// (FreezeScope) and owns the commit boundary, so "runs inside the caller's transaction, never opens or
// commits one" is what the read-back assertions pin. Expectations come from the feature plan's result
// shapes, failure path and test names, not from the current behaviour.
#pragma warning disable CA1861
using System.Globalization;
using Evidgate.BuildingBlocks.Canonicalization;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Evidence.Domain;
using Evidgate.Modules.Orchestrator.Contracts;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The freeze through a real PostgreSQL and the real host: <see cref="IEvidenceFreeze"/> inside the
/// caller's tenant transaction. The artifacts of a run are left-joined to <c>case_attempt</c> by
/// <c>(execution_key, attempt_no)</c>, the pairs that resolve are persisted with one idempotent
/// <c>UPDATE … WHERE attempt_id IS NULL</c>, the caller's requirement set is reported as
/// <see cref="MissingSource.Requirement"/> gaps distinct from the unresolved-pair orphans, and the
/// <see cref="FreezeResult.ArtifactSetHash"/> is the canonical digest of the ready set.
/// </summary>
[Trait("Category", "Docker")]
public sealed class EvidenceFreezeTests
{
    private readonly PostgresTenancyFixture _fixture;

    public EvidenceFreezeTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    // ------------------------------------------------------------------ fixture

    private sealed record World(TenancyApiFactory Api, HttpClient Client, string Database, Guid User, Guid Org, Guid Project)
        : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
        }
    }

    /// <summary>One migrated database with an org and its first project, created through the real registry routes.</summary>
    private async Task<World> StartAsync()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, "acme");
        var project = await CreateProjectAsync(client, user, org, "checkout");

        return new World(api, client, database, user, org, project);
    }

    /// <summary>A second org with a project, through the real routes.</summary>
    private static async Task<(Guid Org, Guid Project)> CreateOtherTenantAsync(World world, string slug)
    {
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(world.Client, user, slug);
        var project = await CreateProjectAsync(world.Client, user, org, slug + "-project");

        return (org, project);
    }

    // ------------------------------------------------------------------ seeding

    /// <summary>One committed run and its plan for <paramref name="world"/>'s org (or another org).</summary>
    private Task<PlannedExecutionSeeder.SeededRun> SeedPlanAsync(World world, int count = 1, Guid? org = null) =>
        PlannedExecutionSeeder.SeedPlanAsync(_fixture, world.Database, org ?? world.Org, count);

    /// <summary>One committed <c>case_attempt</c> batch for one slot of a seeded plan (the production writer path).</summary>
    private static async Task SeedAttemptsAsync(World world, Guid runId, params CaseAttemptInput[] attempts) =>
        await SeedAttemptsAsync(world, world.Org, runId, attempts);

    /// <summary>One committed <c>case_attempt</c> batch for <paramref name="org"/>'s run.</summary>
    private static async Task SeedAttemptsAsync(World world, Guid org, Guid runId, params CaseAttemptInput[] attempts)
    {
        await using var scope = await AttemptScope.BeginAsync(world.Api, org);
        var outcomes = await scope.WriteAsync(runId, attempts);

        outcomes.ShouldAllBe(outcome => outcome.Status == CaseAttemptWriteStatus.Accepted);
        await scope.CommitAsync();
    }

    /// <summary>The persisted <c>case_attempt.id</c> of one slot.</summary>
    private Task<Guid> CaseAttemptIdAsync(World world, Guid runId, string executionKey, int attemptNo) =>
        _fixture.ScalarAsync<Guid>(
            world.Database,
            $"select id from public.case_attempt where org_id = '{world.Org}' and run_id = '{runId}' "
            + $"and execution_key = '{executionKey}' and attempt_no = {attemptNo}");

    /// <summary>One committed artifact row for <paramref name="world"/>'s org and project.</summary>
    private Task InsertArtifactAsync(World world, Guid runId, ArtifactSeed seed) =>
        InsertArtifactsAsync(world, world.Org, world.Project, runId, [seed]);

    /// <summary>One committed batch of artifact rows, inserted in the order given (physical order under test).</summary>
    private Task InsertArtifactsAsync(World world, Guid runId, IReadOnlyList<ArtifactSeed> seeds) =>
        InsertArtifactsAsync(world, world.Org, world.Project, runId, seeds);

    /// <summary>One committed batch of artifact rows for <paramref name="org"/>'s project, in the order given.</summary>
    private async Task InsertArtifactsAsync(
        World world,
        Guid org,
        Guid project,
        Guid runId,
        IReadOnlyList<ArtifactSeed> seeds)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), org);

        foreach (var seed in seeds)
        {
            (await scope.ExecuteAsync(InsertArtifactSql(org, project, runId, seed))).ShouldBe(1);
        }

        await scope.CommitAsync();
    }

    /// <summary>
    /// One artifact row to insert: identity (<paramref name="Id"/>, <paramref name="ExecutionKey"/>,
    /// <paramref name="AttemptNo"/>, <paramref name="Kind"/>) plus the state, content address, already-resolved
    /// <paramref name="AttemptId"/> and <paramref name="Sensitivity"/> the case under test pins.
    /// </summary>
    private sealed record ArtifactSeed(
        Guid Id,
        string ExecutionKey,
        int AttemptNo,
        string Kind,
        string State = ArtifactState.Ready,
        string? Checksum = null,
        Guid? AttemptId = null,
        string Sensitivity = ArtifactSensitivity.Normal);

    /// <summary>
    /// One raw artifact insert as the app role (0028's grants): the object key is the row's own final key, so
    /// the shape CHECK holds for every state, and <paramref name="seed"/> carries the state, checksum,
    /// resolved <c>attempt_id</c> and sensitivity the case under test needs.
    /// </summary>
    private static string InsertArtifactSql(Guid org, Guid project, Guid runId, ArtifactSeed seed) =>
        "insert into public.artifact "
        + "(id, org_id, project_id, run_id, execution_key, attempt_no, attempt_id, kind, object_key, media_type, checksum, sensitivity, scan_state, state) "
        + $"values ('{seed.Id}', '{org}', '{project}', '{runId}', '{seed.ExecutionKey}', {seed.AttemptNo}, "
        + $"{(seed.AttemptId is { } attemptId ? $"'{attemptId}'" : "null")}, '{seed.Kind}', "
        + $"'{FinalKey(org, project, runId, seed.ExecutionKey, seed.AttemptNo, seed.Id)}', 'application/zip', "
        + $"'{seed.Checksum ?? ChecksumOf(seed.Id)}', '{seed.Sensitivity}', 'clean', '{seed.State}')";

    /// <summary>The final object key of one row's own identity (the artifact shape CHECK).</summary>
    private static string FinalKey(Guid org, Guid project, Guid runId, string executionKey, int attemptNo, Guid id) =>
        $"org/{org}/project/{project}/run/{runId}/attempt/{executionKey}/{attemptNo}/{id}";

    /// <summary>A deterministic, well-formed content address for one artifact id, <c>sha256:&lt;64 hex&gt;</c>.</summary>
    private static string ChecksumOf(Guid id) => "sha256:" + id.ToString("N") + id.ToString("N");

    // ------------------------------------------------------------------ the caller's transaction

    /// <summary>
    /// The caller's tenant transaction of 05's reconcile handler, with <see cref="IEvidenceFreeze"/> resolved
    /// from the same container. The service never opens or commits a transaction, so the test owns the commit
    /// boundary: <see cref="CommitAsync"/> makes the resolution durable, disposing without it rolls it back.
    /// <para>
    /// Deliberately not <c>async</c> (tests/README.md, "AsyncLocal rule"): the factory publishes the scope into
    /// its ambient slot synchronously, in the caller's execution context (the <see cref="AttemptScope"/> rule).
    /// </para>
    /// </summary>
    private sealed class FreezeScope : IAsyncDisposable
    {
        private readonly AsyncServiceScope _services;
        private readonly ITenantScope _scope;

        private FreezeScope(AsyncServiceScope services, ITenantScope scope)
        {
            _services = services;
            _scope = scope;
            Freeze = services.ServiceProvider.GetRequiredService<IEvidenceFreeze>();
        }

        /// <summary>The service under test, running in this transaction.</summary>
        internal IEvidenceFreeze Freeze { get; }

        /// <summary>Opens the caller's tenant transaction for <paramref name="org"/> (optionally narrowed to a project).</summary>
        internal static Task<FreezeScope> BeginAsync(TenancyApiFactory api, Guid org, Guid? project = null)
        {
            var services = api.Services.CreateAsyncScope();
            var scopes = services.ServiceProvider.GetRequiredService<ITenantScopeFactory>();

            return scopes
                .BeginAsync(org, project, false, TestContext.Current.CancellationToken)
                .ContinueWith(
                    opened => new FreezeScope(services, opened.GetAwaiter().GetResult()),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
        }

        /// <summary>One freeze of <paramref name="runId"/> with the caller's requirement set.</summary>
        internal Task<FreezeResult> RunAsync(Guid runId, params RequiredArtifactSpec[] required) =>
            Freeze.FreezeAsync(new FreezeRequest(runId, required), TestContext.Current.CancellationToken);

        /// <summary>The row's <c>attempt_id</c> as this transaction sees it (own uncommitted writes included).</summary>
        internal async Task<Guid?> AttemptIdAsync(Guid artifactId)
        {
            var value = await ScalarAsync(
                "select attempt_id from public.artifact where org_id = @org_id and id = @id",
                artifactId);

            return value as Guid?;
        }

        /// <summary>The row's <c>xmin</c>: the transaction that last wrote it (an UPDATE changes it).</summary>
        internal async Task<string> XminAsync(Guid artifactId)
        {
            var value = await ScalarAsync(
                "select xmin::text from public.artifact where org_id = @org_id and id = @id",
                artifactId);

            return value as string
                ?? throw new InvalidOperationException($"artifact {artifactId} is not visible to this scope");
        }

        /// <summary>The C2 commit boundary; the freeze itself never commits.</summary>
        internal Task CommitAsync() => _scope.CommitAsync(TestContext.Current.CancellationToken);

        /// <summary>Releases the transaction scope (a rollback when <see cref="CommitAsync"/> was not called).</summary>
        public async ValueTask DisposeAsync()
        {
            await _scope.DisposeAsync();
            await _services.DisposeAsync();
        }

        private async Task<object?> ScalarAsync(string sql, Guid artifactId)
        {
            await using var command = _scope.Connection.CreateCommand();
            command.Transaction = _scope.Transaction;
            command.CommandText = sql;
            command.Parameters.AddWithValue("org_id", _scope.OrgId);
            command.Parameters.AddWithValue("id", artifactId);

            return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>One freeze of its own on a fresh tenant transaction, committed like 05's reconcile would.</summary>
    private static async Task<FreezeResult> FreezeAsync(World world, Guid runId, params RequiredArtifactSpec[] required)
    {
        await using var scope = await FreezeScope.BeginAsync(world.Api, world.Org);
        var result = await scope.RunAsync(runId, required);

        await scope.CommitAsync();
        return result;
    }

    /// <summary>Every command one freeze issued (the host's app-role data source is logged by <see cref="TenancyApiFactory.Statements"/>).</summary>
    private static IReadOnlyList<string> Statements(World world) => world.Api.Statements.Snapshot();

    /// <summary>The commands that write (the no-per-artifact-write rule): a plain <c>update</c> or a CTE one.</summary>
    private static IEnumerable<string> UpdateStatements(IEnumerable<string> statements) =>
        statements.Where(statement => statement.Contains("update ", StringComparison.OrdinalIgnoreCase));

    /// <summary>The documented order: <c>(execution_key, attempt_no, kind, id)</c>, ordinal, id as lower-case "D".</summary>
    private static List<(string ExecutionKey, int AttemptNo, string Kind, Guid Id)> FreezeOrder(
        IEnumerable<(string ExecutionKey, int AttemptNo, string Kind, Guid Id)> tuples) =>
        tuples
            .OrderBy(tuple => tuple.ExecutionKey, StringComparer.Ordinal)
            .ThenBy(tuple => tuple.AttemptNo)
            .ThenBy(tuple => tuple.Kind, StringComparer.Ordinal)
            .ThenBy(tuple => tuple.Id.ToString("D"), StringComparer.Ordinal)
            .ToList();

    /// <summary>The digest computed independently of the service, over the documented tuple array.</summary>
    private static string ExpectedHash(IEnumerable<(Guid Id, string ObjectKey, string Checksum)> rows) =>
        CanonicalHash.Compute(
            "["
            + string.Join(
                ",",
                rows.Select(row =>
                    $$"""{"artifact_id":"{{row.Id:D}}","object_key":"{{row.ObjectKey}}","checksum":"{{row.Checksum}}"}"""))
            + "]");

    /// <summary>The documented <c>{artifact_id, object_key, checksum}</c> tuple of one inserted seed.</summary>
    private static (Guid Id, string ObjectKey, string Checksum) TupleOf(World world, Guid runId, ArtifactSeed seed) =>
        (seed.Id,
            FinalKey(world.Org, world.Project, runId, seed.ExecutionKey, seed.AttemptNo, seed.Id),
            seed.Checksum ?? ChecksumOf(seed.Id));

    /// <summary>A committed row's <c>xmin</c>, read as the superuser.</summary>
    private Task<string> XminAsync(World world, Guid artifactId) =>
        _fixture.ScalarAsync<string>(world.Database, $"select xmin::text from public.artifact where id = '{artifactId}'");

    // ------------------------------------------------------------------ resolve + persist

    [Fact]
    public async Task Freeze_ResolvesReadyArtifacts_ToAttempts_AndPersists()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world, 2);
        var first = plan.Executions[0];
        var second = plan.Executions[1];

        await SeedAttemptsAsync(
            world,
            plan.RunId,
            new CaseAttemptInput(first.ExecutionKey, 1, "passed"),
            new CaseAttemptInput(first.ExecutionKey, 2, "failed"),
            new CaseAttemptInput(second.ExecutionKey, 1, "timedOut"));

        var firstAttempt1 = await CaseAttemptIdAsync(world, plan.RunId, first.ExecutionKey, 1);
        var firstAttempt2 = await CaseAttemptIdAsync(world, plan.RunId, first.ExecutionKey, 2);
        var secondAttempt1 = await CaseAttemptIdAsync(world, plan.RunId, second.ExecutionKey, 1);

        var trace = Guid.CreateVersion7();
        var screenshot = Guid.CreateVersion7();
        var retriedTrace = Guid.CreateVersion7();
        var report = Guid.CreateVersion7();
        var orphanVideo = Guid.CreateVersion7();
        var pending = Guid.CreateVersion7();
        var uploaded = Guid.CreateVersion7();
        var rejected = Guid.CreateVersion7();
        var pendingDelete = Guid.CreateVersion7();
        var deleted = Guid.CreateVersion7();

        var traceSeed = new ArtifactSeed(trace, first.ExecutionKey, 1, ArtifactKind.Trace);
        var screenshotSeed = new ArtifactSeed(screenshot, first.ExecutionKey, 1, ArtifactKind.Screenshot);
        var retriedTraceSeed = new ArtifactSeed(retriedTrace, first.ExecutionKey, 2, ArtifactKind.Trace);
        var reportSeed = new ArtifactSeed(
            report, second.ExecutionKey, 1, ArtifactKind.RawReport, Sensitivity: ArtifactSensitivity.Sensitive);
        var orphanVideoSeed = new ArtifactSeed(orphanVideo, second.ExecutionKey, 2, ArtifactKind.Video);
        ArtifactSeed[] ready = [traceSeed, screenshotSeed, retriedTraceSeed, reportSeed];

        // Ready rows of the other five states of the artifact machine: never part of a freeze.
        ArtifactSeed[] notReady =
        [
            new(pending, first.ExecutionKey, 1, ArtifactKind.Trace, ArtifactState.PendingUpload),
            new(uploaded, first.ExecutionKey, 1, ArtifactKind.Trace, ArtifactState.Uploaded),
            new(rejected, first.ExecutionKey, 1, ArtifactKind.Trace, ArtifactState.Rejected),
            new(pendingDelete, first.ExecutionKey, 1, ArtifactKind.Trace, ArtifactState.PendingDelete),
            new(deleted, first.ExecutionKey, 1, ArtifactKind.Trace, ArtifactState.Deleted),
        ];

        // Deliberately not the canonical order: the result order is the service's, not the insertion order.
        await InsertArtifactsAsync(
            world,
            plan.RunId,
            [
                notReady[4], notReady[2], reportSeed, notReady[1], retriedTraceSeed, traceSeed,
                notReady[3], orphanVideoSeed, screenshotSeed, notReady[0],
            ]);

        await using (var scope = await FreezeScope.BeginAsync(world.Api, world.Org))
        {
            var result = await scope.RunAsync(plan.RunId);

            result.Ready
                .Select(artifact => (artifact.ExecutionKey, artifact.AttemptNo, artifact.Kind, artifact.ArtifactId))
                .ShouldBe(FreezeOrder(ready.Select(seed => (seed.ExecutionKey, seed.AttemptNo, seed.Kind, seed.Id))));

            result.Ready.Single(artifact => artifact.ArtifactId == trace).AttemptId.ShouldBe(firstAttempt1);
            result.Ready.Single(artifact => artifact.ArtifactId == retriedTrace).AttemptId.ShouldBe(firstAttempt2);
            result.Ready.Single(artifact => artifact.ArtifactId == report).AttemptId.ShouldBe(secondAttempt1);

            var traceRow = result.Ready.Single(artifact => artifact.ArtifactId == trace);
            traceRow.ObjectKey.ShouldBe(FinalKey(world.Org, world.Project, plan.RunId, first.ExecutionKey, 1, trace));
            traceRow.Checksum.ShouldBe(ChecksumOf(trace));
            traceRow.Sensitivity.ShouldBe(ArtifactSensitivity.Normal);
            result.Ready.Single(artifact => artifact.ArtifactId == report).Sensitivity.ShouldBe(ArtifactSensitivity.Sensitive);

            result.Orphans.ShouldHaveSingleItem().ShouldBe(new OrphanArtifact(
                orphanVideo,
                second.ExecutionKey,
                2,
                ArtifactKind.Video,
                FinalKey(world.Org, world.Project, plan.RunId, second.ExecutionKey, 2, orphanVideo),
                ChecksumOf(orphanVideo),
                ArtifactSensitivity.Normal));

            result.Missing.ShouldHaveSingleItem().ShouldBe(new MissingReason(
                FreezeCodes.ArtifactMissing,
                MissingSource.Orphan,
                second.ExecutionKey,
                ArtifactKind.Video,
                2,
                orphanVideo));

            result.Ready.ShouldNotContain(artifact => notReady.Any(seed => seed.Id == artifact.ArtifactId));

            // Persisted inside the caller's transaction (nothing committed yet) and null for the rest.
            (await scope.AttemptIdAsync(trace)).ShouldBe(firstAttempt1);
            (await scope.AttemptIdAsync(report)).ShouldBe(secondAttempt1);
            (await scope.AttemptIdAsync(orphanVideo)).ShouldBeNull();

            foreach (var seed in notReady)
            {
                (await scope.AttemptIdAsync(seed.Id)).ShouldBeNull();
            }

            await scope.CommitAsync();
        }

        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.artifact where run_id = '{plan.RunId}' and state = 'ready' and attempt_id is not null"))
            .ShouldBe(4);
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.artifact where run_id = '{plan.RunId}' and attempt_id is null"))
            .ShouldBe(6);
        (await _fixture.ScalarAsync<string>(
            world.Database,
            $"select attempt_id::text from public.artifact where id = '{retriedTrace}'"))
            .ShouldBe(firstAttempt2.ToString());
    }

    // ------------------------------------------------------------------ the orphan

    [Fact]
    public async Task Freeze_UnresolvedPair_IsOrphan_ArtifactMissing()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world, 2);
        var unresolved = plan.Executions[0];
        var resolved = plan.Executions[1];

        // Only the second execution recorded an attempt at 1: attempt 1 of another execution is never borrowed.
        await SeedAttemptsAsync(world, plan.RunId, new CaseAttemptInput(resolved.ExecutionKey, 1, "passed"));
        var resolvedAttemptId = await CaseAttemptIdAsync(world, plan.RunId, resolved.ExecutionKey, 1);

        var orphan = Guid.CreateVersion7();
        var bound = Guid.CreateVersion7();
        await InsertArtifactsAsync(
            world,
            plan.RunId,
            [
                new ArtifactSeed(orphan, unresolved.ExecutionKey, 1, ArtifactKind.Trace),
                new ArtifactSeed(bound, resolved.ExecutionKey, 1, ArtifactKind.Trace),
            ]);

        await using (var scope = await FreezeScope.BeginAsync(world.Api, world.Org))
        {
            var result = await scope.RunAsync(plan.RunId);

            result.Ready.ShouldHaveSingleItem().ArtifactId.ShouldBe(bound);
            result.Ready[0].AttemptId.ShouldBe(resolvedAttemptId);

            var orphanRow = result.Orphans.ShouldHaveSingleItem();
            orphanRow.ArtifactId.ShouldBe(orphan);
            orphanRow.ExecutionKey.ShouldBe(unresolved.ExecutionKey);
            orphanRow.AttemptNo.ShouldBe(1);
            orphanRow.Kind.ShouldBe(ArtifactKind.Trace);

            result.Missing.ShouldHaveSingleItem().ShouldBe(new MissingReason(
                FreezeCodes.ArtifactMissing,
                MissingSource.Orphan,
                unresolved.ExecutionKey,
                ArtifactKind.Trace,
                1,
                orphan));

            // Never resolved to a wrong attempt: the pair stays null, in this transaction and after the commit.
            (await scope.AttemptIdAsync(orphan)).ShouldBeNull();
            await scope.CommitAsync();
        }

        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.artifact where id = '{orphan}' and attempt_id is null"))
            .ShouldBe(1);
    }

    // ------------------------------------------------------------------ requirement gaps vs orphans

    [Fact]
    public async Task Freeze_MissingRequiredArtifact_DistinctFromOrphan()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world);
        var key = plan.Executions[0].ExecutionKey;

        await SeedAttemptsAsync(world, plan.RunId, new CaseAttemptInput(key, 1, "passed"));

        var trace = Guid.CreateVersion7();
        var unrecordedScreenshot = Guid.CreateVersion7();
        await InsertArtifactsAsync(
            world,
            plan.RunId,
            [
                new ArtifactSeed(trace, key, 1, ArtifactKind.Trace),
                new ArtifactSeed(unrecordedScreenshot, key, 2, ArtifactKind.Screenshot),
            ]);

        var duplicate = new RequiredArtifactSpec(key, ArtifactKind.Video, "video_required");
        var result = await FreezeAsync(
            world,
            plan.RunId,
            // Met: attempt 1 and any attempt of the execution.
            new RequiredArtifactSpec(key, ArtifactKind.Trace, "capture_failed", 1),
            new RequiredArtifactSpec(key, ArtifactKind.Trace, "capture_failed"),
            // Not met: a ready trace exists, but not for attempt 2.
            new RequiredArtifactSpec(key, ArtifactKind.Trace, "capture_failed", 2),
            // Not met at all, declared twice: duplicate specs collapse to one gap.
            duplicate,
            duplicate,
            // The only attempt-2 artifact is an orphan: it never satisfies a requirement, and the
            // caller-declared artifact_missing stays distinguishable from the orphan's by Source and ArtifactId.
            new RequiredArtifactSpec(key, ArtifactKind.Screenshot, FreezeCodes.ArtifactMissing, 2));

        result.Ready.Select(artifact => artifact.ArtifactId).ShouldBe(new[] { trace });
        result.Orphans.Select(artifact => artifact.ArtifactId).ShouldBe(new[] { unrecordedScreenshot });

        // Orphans first, then requirement gaps by (execution_key, kind, attempt_no null-first, reason code).
        result.Missing.ShouldBe(new MissingReason[]
        {
            new(FreezeCodes.ArtifactMissing, MissingSource.Orphan, key, ArtifactKind.Screenshot, 2, unrecordedScreenshot),
            new(FreezeCodes.ArtifactMissing, MissingSource.Requirement, key, ArtifactKind.Screenshot, 2, null),
            new("capture_failed", MissingSource.Requirement, key, ArtifactKind.Trace, 2, null),
            new("video_required", MissingSource.Requirement, key, ArtifactKind.Video, null, null),
        });
    }

    // ------------------------------------------------------------------ idempotent, no rewrite

    [Fact]
    public async Task Freeze_AttemptIdResolution_IsIdempotent_NoRewrite()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world);
        var key = plan.Executions[0].ExecutionKey;

        await SeedAttemptsAsync(
            world,
            plan.RunId,
            new CaseAttemptInput(key, 1, "passed"),
            new CaseAttemptInput(key, 2, "passed"));

        var attempt1 = await CaseAttemptIdAsync(world, plan.RunId, key, 1);
        var attempt2 = await CaseAttemptIdAsync(world, plan.RunId, key, 2);

        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var alreadyResolved = Guid.CreateVersion7();
        await InsertArtifactsAsync(
            world,
            plan.RunId,
            [
                new ArtifactSeed(first, key, 1, ArtifactKind.Trace),
                new ArtifactSeed(second, key, 2, ArtifactKind.Video),
                // Resolved by an earlier freeze: the WHERE attempt_id IS NULL guard must leave it alone.
                new ArtifactSeed(alreadyResolved, key, 1, ArtifactKind.Screenshot, AttemptId: attempt1),
            ]);

        var alreadyResolvedXmin = await XminAsync(world, alreadyResolved);

        await using (var scope = await FreezeScope.BeginAsync(world.Api, world.Org))
        {
            world.Api.Statements.Clear();
            var call = await scope.RunAsync(plan.RunId);

            var statements = Statements(world);
            statements.Count.ShouldBe(2);
            UpdateStatements(statements).Count().ShouldBe(1);

            call.Ready.Count.ShouldBe(3);
            call.Orphans.ShouldBeEmpty();
            call.Missing.ShouldBeEmpty();
            (await scope.AttemptIdAsync(first)).ShouldBe(attempt1);
            (await scope.AttemptIdAsync(second)).ShouldBe(attempt2);

            // The row that was already bound did not take part in the first call's UPDATE either.
            (await scope.XminAsync(alreadyResolved)).ShouldBe(alreadyResolvedXmin);

            var xmins = new Dictionary<Guid, string>();
            foreach (var artifactId in new[] { first, second, alreadyResolved })
            {
                xmins[artifactId] = await scope.XminAsync(artifactId);
            }

            world.Api.Statements.Clear();
            var again = await scope.RunAsync(plan.RunId);

            var secondCall = Statements(world);
            secondCall.Count.ShouldBe(1);
            UpdateStatements(secondCall).ShouldBeEmpty();

            again.Ready.ShouldBe(call.Ready);
            again.Orphans.ShouldBe(call.Orphans);
            again.Missing.ShouldBe(call.Missing);
            again.ArtifactSetHash.ShouldBe(call.ArtifactSetHash);

            // No row was rewritten by the second call: xmin still names the transaction that first resolved it.
            foreach (var (artifactId, xmin) in xmins)
            {
                (await scope.XminAsync(artifactId)).ShouldBe(xmin);
            }

            await scope.CommitAsync();
        }

        (await XminAsync(world, alreadyResolved)).ShouldBe(alreadyResolvedXmin);
        (await _fixture.ScalarAsync<string>(
            world.Database,
            $"select attempt_id::text from public.artifact where id = '{alreadyResolved}'"))
            .ShouldBe(attempt1.ToString());
    }

    // ------------------------------------------------------------------ the canonical hash

    [Fact]
    public async Task Freeze_HashIsCanonical_OrderIndependent()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world);
        var key = plan.Executions[0].ExecutionKey;

        await SeedAttemptsAsync(
            world,
            plan.RunId,
            new CaseAttemptInput(key, 1, "passed"),
            new CaseAttemptInput(key, 2, "passed"));

        ArtifactSeed[] canonical =
        [
            new(Guid.CreateVersion7(), key, 1, ArtifactKind.Trace),
            new(Guid.CreateVersion7(), key, 1, ArtifactKind.Video),
            new(Guid.CreateVersion7(), key, 2, ArtifactKind.Trace),
        ];

        // Inserted in the reverse of the canonical order: the digest must follow the documented order, not the heap.
        ArtifactSeed[] insertionOrder = [canonical[2], canonical[1], canonical[0]];
        await InsertArtifactsAsync(world, plan.RunId, insertionOrder);

        var expected = canonical.Select(seed => TupleOf(world, plan.RunId, seed)).ToList();
        var expectedUnordered = insertionOrder.Select(seed => TupleOf(world, plan.RunId, seed)).ToList();

        var first = await FreezeAsync(world, plan.RunId);
        first.Orphans.ShouldBeEmpty();
        first.ArtifactSetHash.ShouldBe(ExpectedHash(expected));
        first.ArtifactSetHash.ShouldNotBe(ExpectedHash(expectedUnordered));

        // The digest is a function of the claimed fields: a different checksum or object key changes it.
        first.ArtifactSetHash.ShouldNotBe(ExpectedHash(expected.Select(row =>
            (row.Id, row.ObjectKey, Checksum: "sha256:" + new string('d', 64)))));
        first.ArtifactSetHash.ShouldNotBe(ExpectedHash(expected.Select(row =>
            (row.Id, ObjectKey: row.ObjectKey + "-moved", row.Checksum))));

        // An orphan is part of the set, and resolving it later leaves the digest unchanged (attempt_id excluded).
        var lateOrphan = new ArtifactSeed(Guid.CreateVersion7(), key, 3, ArtifactKind.Screenshot);
        await InsertArtifactAsync(world, plan.RunId, lateOrphan);

        var withOrphan = await FreezeAsync(world, plan.RunId);
        withOrphan.Orphans.ShouldHaveSingleItem().ArtifactId.ShouldBe(lateOrphan.Id);
        withOrphan.ArtifactSetHash.ShouldNotBe(first.ArtifactSetHash);
        withOrphan.ArtifactSetHash.ShouldBe(ExpectedHash([.. expected, TupleOf(world, plan.RunId, lateOrphan)]));

        await SeedAttemptsAsync(world, plan.RunId, new CaseAttemptInput(key, 3, "passed"));

        var resolved = await FreezeAsync(world, plan.RunId);
        resolved.Orphans.ShouldBeEmpty();
        resolved.Ready.Count.ShouldBe(4);
        resolved.Ready.Single(artifact => artifact.ArtifactId == lateOrphan.Id).AttemptId.ShouldNotBe(Guid.Empty);
        resolved.ArtifactSetHash.ShouldBe(withOrphan.ArtifactSetHash);
    }

    // ------------------------------------------------------------------ bounded statements

    [Fact]
    public async Task Freeze_BoundedQueries_NoNPlus1()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world, 2);

        // 50 recorded attempts over 2 planned executions: 25 attempt numbers each.
        await SeedAttemptsAsync(
            world,
            plan.RunId,
            [.. plan.Executions.SelectMany(execution => Enumerable
                .Range(1, 25)
                .Select(attemptNo => new CaseAttemptInput(execution.ExecutionKey, attemptNo, "passed")))]);

        string[] kinds =
            [ArtifactKind.Trace, ArtifactKind.Screenshot, ArtifactKind.RawReport, ArtifactKind.Video];
        var seeds = plan.Executions
            .SelectMany(execution => Enumerable
                .Range(1, 25)
                .SelectMany(attemptNo => kinds.Select(kind =>
                    new ArtifactSeed(Guid.CreateVersion7(), execution.ExecutionKey, attemptNo, kind))))
            .ToList();
        seeds.Count.ShouldBe(200);
        await InsertArtifactsAsync(world, plan.RunId, seeds);

        await using var scope = await FreezeScope.BeginAsync(world.Api, world.Org);

        world.Api.Statements.Clear();
        var first = await scope.RunAsync(plan.RunId);
        var firstStatements = Statements(world);

        // One read plus the one conditional UPDATE, whatever the row count.
        firstStatements.Count.ShouldBe(2);
        UpdateStatements(firstStatements).Count().ShouldBe(1);

        world.Api.Statements.Clear();
        var second = await scope.RunAsync(plan.RunId);
        var secondStatements = Statements(world);

        // Everything already resolved: the read only.
        secondStatements.Count.ShouldBe(1);
        UpdateStatements(secondStatements).ShouldBeEmpty();

        first.Ready.Count.ShouldBe(200);
        first.Orphans.ShouldBeEmpty();
        first.Missing.ShouldBeEmpty();
        second.Ready.Count.ShouldBe(200);
        second.ArtifactSetHash.ShouldBe(first.ArtifactSetHash);

        var attemptIds = new Dictionary<(string ExecutionKey, int AttemptNo), Guid>();
        foreach (var row in await _fixture.ColumnAsync<string>(
            world.Database,
            $"select execution_key || '|' || attempt_no || '|' || id from public.case_attempt where org_id = '{world.Org}' and run_id = '{plan.RunId}'"))
        {
            var parts = row.Split('|');
            attemptIds[(parts[0], int.Parse(parts[1], CultureInfo.InvariantCulture))] = Guid.Parse(parts[2]);
        }

        attemptIds.Count.ShouldBe(50);
        foreach (var artifact in first.Ready)
        {
            artifact.AttemptId.ShouldBe(attemptIds[(artifact.ExecutionKey, artifact.AttemptNo)]);
        }

        await scope.CommitAsync();
    }

    // ------------------------------------------------------------------ tenant isolation + scope guard

    [Fact]
    public async Task Freeze_OrgIsolation()
    {
        await using var world = await StartAsync();
        var (otherOrg, otherProject) = await CreateOtherTenantAsync(world, "rival");

        var ownPlan = await SeedPlanAsync(world);
        var otherPlan = await SeedPlanAsync(world, 2, otherOrg);
        var ownKey = ownPlan.Executions[0].ExecutionKey;
        var otherKey = otherPlan.Executions[0].ExecutionKey;

        await SeedAttemptsAsync(world, ownPlan.RunId, new CaseAttemptInput(ownKey, 1, "passed"));
        await SeedAttemptsAsync(world, otherOrg, otherPlan.RunId, new CaseAttemptInput(otherKey, 1, "passed"));

        var ownArtifact = Guid.CreateVersion7();
        var otherBound = Guid.CreateVersion7();
        var otherOrphan = Guid.CreateVersion7();
        await InsertArtifactAsync(world, ownPlan.RunId, new ArtifactSeed(ownArtifact, ownKey, 1, ArtifactKind.Trace));
        await InsertArtifactsAsync(
            world,
            otherOrg,
            otherProject,
            otherPlan.RunId,
            [
                new ArtifactSeed(otherBound, otherKey, 1, ArtifactKind.Trace),
                new ArtifactSeed(otherOrphan, otherKey, 2, ArtifactKind.Trace),
            ]);

        // Another org's run: nothing is visible, so every declared requirement is missing — never complete.
        await using (var scope = await FreezeScope.BeginAsync(world.Api, world.Org))
        {
            var foreign = await scope.RunAsync(
                otherPlan.RunId,
                new RequiredArtifactSpec(otherKey, ArtifactKind.Trace, FreezeCodes.ArtifactMissing));

            foreign.Ready.ShouldBeEmpty();
            foreign.Orphans.ShouldBeEmpty();
            foreign.Missing.ShouldHaveSingleItem().ShouldBe(new MissingReason(
                FreezeCodes.ArtifactMissing,
                MissingSource.Requirement,
                otherKey,
                ArtifactKind.Trace,
                null,
                null));
            foreign.ArtifactSetHash.ShouldBe(CanonicalHash.Compute("[]"));

            await scope.CommitAsync();
        }

        // The org's own run keeps its own artifacts only.
        await using (var scope = await FreezeScope.BeginAsync(world.Api, world.Org))
        {
            var own = await scope.RunAsync(ownPlan.RunId);

            own.Ready.ShouldHaveSingleItem().ArtifactId.ShouldBe(ownArtifact);
            own.Orphans.ShouldBeEmpty();
            own.Missing.ShouldBeEmpty();

            await scope.CommitAsync();
        }

        // The other org's rows were never touched (their own freeze has not run yet).
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.artifact where org_id = '{otherOrg}' and attempt_id is not null"))
            .ShouldBe(0);
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.artifact where org_id = '{otherOrg}'"))
            .ShouldBe(2);

        // No ambient tenant scope: refused before any statement.
        await using var services = world.Api.Services.CreateAsyncScope();
        var unscoped = services.ServiceProvider.GetRequiredService<IEvidenceFreeze>();
        world.Api.Statements.Clear();

        await Should.ThrowAsync<InvalidOperationException>(() => unscoped.FreezeAsync(
            new FreezeRequest(ownPlan.RunId, []),
            TestContext.Current.CancellationToken));

        Statements(world).ShouldBeEmpty();
    }

    // ------------------------------------------------------------------ project visibility inside one org

    [Fact]
    public async Task Freeze_OtherProjectOfSameOrg_YieldsNoEvidence()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world);
        var key = plan.Executions[0].ExecutionKey;

        // A second project of the SAME org: row level security is org-only (0028), so the `a.project_id` predicate
        // of both statements is the only thing that keeps another project's evidence out of a project-scoped freeze.
        var otherProject = await CreateProjectAsync(world.Client, world.User, world.Org, "second");
        await SeedAttemptsAsync(world, plan.RunId, new CaseAttemptInput(key, 1, "passed"));

        var artifact = Guid.CreateVersion7();
        await InsertArtifactAsync(world, plan.RunId, new ArtifactSeed(artifact, key, 1, ArtifactKind.Trace));

        await using (var scope = await FreezeScope.BeginAsync(world.Api, world.Org, otherProject))
        {
            world.Api.Statements.Clear();
            var result = await scope.RunAsync(
                plan.RunId,
                new RequiredArtifactSpec(key, ArtifactKind.Trace, FreezeCodes.ArtifactMissing));

            result.Ready.ShouldBeEmpty();
            result.Orphans.ShouldBeEmpty();
            result.ArtifactSetHash.ShouldBe(CanonicalHash.Compute("[]"));
            result.Missing.ShouldHaveSingleItem().ShouldBe(new MissingReason(
                FreezeCodes.ArtifactMissing,
                MissingSource.Requirement,
                key,
                ArtifactKind.Trace,
                null,
                null));

            // No row was even a candidate, so the call issued the read only and never resolved the attempt.
            Statements(world).Count.ShouldBe(1);
            UpdateStatements(Statements(world)).ShouldBeEmpty();
            (await scope.AttemptIdAsync(artifact)).ShouldBeNull();

            await scope.CommitAsync();
        }

        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.artifact where id = '{artifact}' and attempt_id is null"))
            .ShouldBe(1);
    }

    // --------------------------------------------- a report counts only once its parse has decided

    [Fact]
    public async Task Freeze_UnparsedReport_IsNeverCounted_EvenAfterItsRejection()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world);
        var key = plan.Executions[0].ExecutionKey;

        await SeedAttemptsAsync(world, plan.RunId, new CaseAttemptInput(key, 1, "passed"));

        var report = Guid.CreateVersion7();
        await InsertArtifactAsync(world, plan.RunId, new ArtifactSeed(report, key, 1, ArtifactKind.RawReport));

        // The shape the out-of-process parse leaves behind while it runs: the report is `ready` and its
        // `artifact_parse` row has not reached `done`, so the parser may still reject the bytes.
        await InsertParseTaskAsync(world, report, "queued");

        var spec = new RequiredArtifactSpec(key, ArtifactKind.RawReport, FreezeCodes.ArtifactMissing, 1);
        var expectedGap = new MissingReason(
            FreezeCodes.ArtifactMissing,
            MissingSource.Requirement,
            key,
            ArtifactKind.RawReport,
            1,
            null);

        await using (var scope = await FreezeScope.BeginAsync(world.Api, world.Org))
        {
            world.Api.Statements.Clear();
            var result = await scope.RunAsync(plan.RunId, spec);

            // Not evidence while its verdict is open: the caller learns it as the missing requirement instead,
            // and nothing is written for it.
            result.Ready.ShouldBeEmpty();
            result.Orphans.ShouldBeEmpty();
            result.ArtifactSetHash.ShouldBe(CanonicalHash.Compute("[]"));
            result.Missing.ShouldHaveSingleItem().ShouldBe(expectedGap);
            UpdateStatements(Statements(world)).ShouldBeEmpty();
            (await scope.AttemptIdAsync(report)).ShouldBeNull();

            await scope.CommitAsync();
        }

        // The verdict that would have contradicted a revision frozen a moment earlier.
        await RejectReportAsync(world, report);

        var after = await FreezeAsync(world, plan.RunId, spec);
        after.Ready.ShouldBeEmpty();
        after.Orphans.ShouldBeEmpty();
        after.Missing.ShouldHaveSingleItem().ShouldBe(expectedGap);
    }

    [Theory]
    [InlineData("queued")]
    [InlineData("active")]
    [InlineData("retry")]
    [InlineData("dead_letter")]
    public async Task Freeze_UnfinishedParseTask_IsArtifactMissing(string parseState)
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world);
        var key = plan.Executions[0].ExecutionKey;

        await SeedAttemptsAsync(world, plan.RunId, new CaseAttemptInput(key, 1, "passed"));

        var report = Guid.CreateVersion7();
        await InsertArtifactAsync(world, plan.RunId, new ArtifactSeed(report, key, 1, ArtifactKind.RawReport));
        await InsertParseTaskAsync(world, report, parseState);

        var result = await FreezeAsync(
            world,
            plan.RunId,
            new RequiredArtifactSpec(key, ArtifactKind.RawReport, FreezeCodes.ArtifactMissing));

        result.Ready.ShouldBeEmpty();
        result.Orphans.ShouldBeEmpty();
        result.Missing.ShouldHaveSingleItem().ShouldBe(new MissingReason(
            FreezeCodes.ArtifactMissing,
            MissingSource.Requirement,
            key,
            ArtifactKind.RawReport,
            null,
            null));

        // The one variable that decides it is the row's own state: the same report, with its parse finished, is
        // evidence. This half guards against over-gating — the assertions above are the half a build without the gate
        // fails, because that build freezes the report while its verdict is still open.
        await SetParseTaskStateAsync(world, report, "done");

        var parsed = await FreezeAsync(world, plan.RunId);

        parsed.Ready.Select(artifact => artifact.ArtifactId).ShouldBe(new[] { report });
        parsed.Missing.ShouldBeEmpty();
    }

    [Fact]
    public async Task Freeze_OpenParse_IsNeverDecisionInput_AndItsRejectionChangesNothing()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world);
        var key = plan.Executions[0].ExecutionKey;

        await SeedAttemptsAsync(
            world,
            plan.RunId,
            new CaseAttemptInput(key, 1, "passed"),
            new CaseAttemptInput(key, 2, "passed"));

        var parsed = Guid.CreateVersion7();
        var stillParsing = Guid.CreateVersion7();
        var parsedSeed = new ArtifactSeed(parsed, key, 1, ArtifactKind.RawReport);
        await InsertArtifactsAsync(
            world,
            plan.RunId,
            [parsedSeed, new ArtifactSeed(stillParsing, key, 2, ArtifactKind.RawReport)]);
        await InsertParseTaskAsync(world, parsed, "done");
        await InsertParseTaskAsync(world, stillParsing, "active");

        var spec = new RequiredArtifactSpec(key, ArtifactKind.RawReport, FreezeCodes.ArtifactMissing, 2);
        var first = await FreezeAsync(world, plan.RunId, spec);

        // The decided report is evidence and the open one is the named gap: the set and digest are the decision
        // input, so they must never hold a report the parser has not cleared.
        first.Ready.Select(artifact => artifact.ArtifactId).ShouldBe(new[] { parsed });
        first.ArtifactSetHash.ShouldBe(ExpectedHash([TupleOf(world, plan.RunId, parsedSeed)]));
        first.Missing.ShouldHaveSingleItem().ShouldBe(new MissingReason(
            FreezeCodes.ArtifactMissing,
            MissingSource.Requirement,
            key,
            ArtifactKind.RawReport,
            2,
            null));

        // Its rejection changes the same input by nothing: the frozen set and digest stay, and the report stays
        // the gap, so no revision ever counts it.
        await RejectReportAsync(world, stillParsing);

        var second = await FreezeAsync(world, plan.RunId, spec);
        second.Ready.Select(artifact => artifact.ArtifactId).ShouldBe(new[] { parsed });
        second.ArtifactSetHash.ShouldBe(first.ArtifactSetHash);
        second.Ready.ShouldNotContain(artifact => artifact.ArtifactId == stillParsing);
    }

    [Fact]
    public async Task Freeze_UndispatchedParseOutbox_IsArtifactMissing()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world);
        var key = plan.Executions[0].ExecutionKey;

        await SeedAttemptsAsync(world, plan.RunId, new CaseAttemptInput(key, 1, "passed"));

        var report = Guid.CreateVersion7();
        await InsertArtifactAsync(world, plan.RunId, new ArtifactSeed(report, key, 1, ArtifactKind.RawReport));

        // The completion committed `ready` and its parse request; the dispatcher has not turned that outbox row
        // into a task row yet, so the task anti-join alone reads nothing and would admit an unparsed report.
        await InsertParseOutboxAsync(world, report);

        var spec = new RequiredArtifactSpec(key, ArtifactKind.RawReport, FreezeCodes.ArtifactMissing, 1);
        var expectedGap = new MissingReason(
            FreezeCodes.ArtifactMissing,
            MissingSource.Requirement,
            key,
            ArtifactKind.RawReport,
            1,
            null);

        var pending = await FreezeAsync(world, plan.RunId, spec);
        pending.Ready.ShouldBeEmpty();
        pending.Orphans.ShouldBeEmpty();
        pending.Missing.ShouldHaveSingleItem().ShouldBe(expectedGap);

        // The dispatcher's own hand-over: the task row exists and the outbox row is stamped. Still no verdict.
        await InsertParseTaskAsync(world, report, "queued");
        await DispatchParseOutboxAsync(world, report);

        var dispatched = await FreezeAsync(world, plan.RunId, spec);
        dispatched.Ready.ShouldBeEmpty();
        dispatched.Missing.ShouldHaveSingleItem().ShouldBe(expectedGap);

        // Only the parse's own verdict admits the report, so neither half of the gate over-blocks.
        await SetParseTaskStateAsync(world, report, "done");

        var parsed = await FreezeAsync(world, plan.RunId, spec);
        parsed.Ready.Select(artifact => artifact.ArtifactId).ShouldBe(new[] { report });
        parsed.Missing.ShouldBeEmpty();
    }

    [Fact]
    public async Task Freeze_ForeignOrgParseRows_DoNotBlock()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world);
        var key = plan.Executions[0].ExecutionKey;

        await SeedAttemptsAsync(world, plan.RunId, new CaseAttemptInput(key, 1, "passed"));

        var report = Guid.CreateVersion7();
        await InsertArtifactAsync(world, plan.RunId, new ArtifactSeed(report, key, 1, ArtifactKind.RawReport));

        // `public.task` and `public.outbox` carry no row-level security, so only the explicit `org_id` predicate
        // keeps another tenant's row naming this artifact id out of this tenant's decision.
        var (otherOrg, _) = await CreateOtherTenantAsync(world, "other");
        await InsertParseTaskAsync(world, report, "active", otherOrg);
        await InsertParseOutboxAsync(world, report, otherOrg);

        var result = await FreezeAsync(world, plan.RunId);

        result.Ready.Select(artifact => artifact.ArtifactId).ShouldBe(new[] { report });
        result.Missing.ShouldBeEmpty();
    }

    /// <summary>
    /// One <c>artifact_parse</c> row for <paramref name="artifactId"/>, in the task state under test: the
    /// dispatcher's row shape (<c>payload.artifact_id</c>), so the freeze sees exactly what production writes.
    /// </summary>
    private async Task InsertParseTaskAsync(World world, Guid artifactId, string state, Guid? org = null)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), org ?? world.Org);
        var taskId = Guid.CreateVersion7();

        (await scope.ExecuteAsync(
            "insert into public.task (id, org_id, queue, task_type, payload, idempotency_key, state) "
            + "values (@id, @org_id, 'default', 'artifact_parse', "
            + "jsonb_build_object('artifact_id', @artifact_id::text), @idempotency_key, @state)",
            ("id", taskId),
            ("org_id", org ?? world.Org),
            ("artifact_id", artifactId.ToString("D", CultureInfo.InvariantCulture)),
            ("idempotency_key", "outbox:" + taskId.ToString("D", CultureInfo.InvariantCulture)),
            ("state", state)))
            .ShouldBe(1);

        await scope.CommitAsync();
    }

    /// <summary>
    /// The completion's parse request before the dispatcher runs: one undispatched <c>outbox</c> row, no task row,
    /// with the key and payload shape <c>CompleteArtifactUpload</c> writes.
    /// </summary>
    private async Task InsertParseOutboxAsync(World world, Guid artifactId, Guid? org = null)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), org ?? world.Org);
        var outboxId = Guid.CreateVersion7();
        var artifactText = artifactId.ToString("D", CultureInfo.InvariantCulture);

        (await scope.ExecuteAsync(
            "insert into public.outbox (id, org_id, aggregate_type, aggregate_id, task_type, payload, idempotency_key) "
            + "values (@id, @org_id, 'artifact', @aggregate_id, 'artifact_parse', "
            + "jsonb_build_object('artifact_id', @artifact_id::text), @idempotency_key)",
            ("id", outboxId),
            ("org_id", org ?? world.Org),
            ("aggregate_id", artifactId),
            ("artifact_id", artifactText),
            ("idempotency_key", "evidence:artifact:" + artifactText + ":parse")))
            .ShouldBe(1);

        await scope.CommitAsync();
    }

    /// <summary>
    /// The dispatcher's stamp on the parse outbox row. The app role may only insert outbox rows, so the stamp runs as
    /// the owner, exactly the role the dispatcher holds.
    /// </summary>
    private Task DispatchParseOutboxAsync(World world, Guid artifactId) =>
        _fixture.ExecuteAsync(
            world.Database,
            "update public.outbox set dispatched_at = now() "
            + $"where org_id = '{world.Org}' and task_type = 'artifact_parse' "
            + $"and payload ->> 'artifact_id' = '{artifactId.ToString("D", CultureInfo.InvariantCulture)}'");

    /// <summary>
    /// The consumer's own transition of <paramref name="artifactId"/>'s parse row, keyed the way the dispatcher writes
    /// it: the handler's verdict is durable in its transaction, so the row moves only after that commit.
    /// </summary>
    private async Task SetParseTaskStateAsync(World world, Guid artifactId, string state)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);

        (await scope.ExecuteAsync(
            "update public.task set state = @state "
            + "where org_id = @org_id and task_type = 'artifact_parse' "
            + "and payload ->> 'artifact_id' = @artifact_id",
            ("state", state),
            ("org_id", world.Org),
            ("artifact_id", artifactId.ToString("D", CultureInfo.InvariantCulture))))
            .ShouldBe(1);

        await scope.CommitAsync();
    }

    /// <summary>
    /// The parse consumer's rejection, in the order that makes the verdict durable first: the guarded
    /// <c>ready → rejected</c> write, then the task's <c>done</c>.
    /// </summary>
    private async Task RejectReportAsync(World world, Guid artifactId)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);

        (await scope.ExecuteAsync(
            "update public.artifact set state = @state, failure_code = @failure_code "
            + "where org_id = @org_id and id = @id and state = 'ready'",
            ("state", ArtifactState.Rejected),
            ("failure_code", ArtifactFailureCode.ParseCrashed),
            ("org_id", world.Org),
            ("id", artifactId)))
            .ShouldBe(1);

        (await scope.ExecuteAsync(
            "update public.task set state = 'done' "
            + "where org_id = @org_id and task_type = 'artifact_parse' "
            + "and payload ->> 'artifact_id' = @artifact_id",
            ("org_id", world.Org),
            ("artifact_id", artifactId.ToString("D", CultureInfo.InvariantCulture))))
            .ShouldBe(1);

        await scope.CommitAsync();
    }

    // ------------------------------------------------------------------ authz: no HTTP surface

    [Fact]
    public async Task Freeze_AddsNoHttpRoute()
    {
        await using var world = await StartAsync();

        // The freeze is an internal cross-module service call that 05's
        // reconcile handler makes inside its own tenant transaction — it exposes no /v1 route, so no permission
        // gates it and no caller can freeze evidence over HTTP.
        var routes = world.Api.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText ?? string.Empty)
            .ToList();

        routes.ShouldNotBeEmpty();
        routes.ShouldNotContain(
            route => route.Contains("freeze", StringComparison.OrdinalIgnoreCase),
            "IEvidenceFreeze is a service call, never a route");
    }

    // ------------------------------------------------------------------ failure path: request shape

    [Theory]
    [InlineData("request_null")]
    [InlineData("element_null")]
    [InlineData("run_id_empty")]
    [InlineData("too_many_specs")]
    [InlineData("execution_key_short")]
    [InlineData("execution_key_upper")]
    [InlineData("execution_key_non_hex")]
    [InlineData("kind_unknown")]
    [InlineData("reason_code_upper")]
    [InlineData("reason_code_empty")]
    [InlineData("reason_code_too_long")]
    [InlineData("attempt_no_zero")]
    [InlineData("attempt_no_negative")]
    public async Task Freeze_InvalidRequest_Refused_ZeroStatements(string caseName)
    {
        await using var world = await StartAsync();
        var runId = Guid.CreateVersion7();
        var key = new string('a', 64);

        await using var services = world.Api.Services.CreateAsyncScope();
        var freeze = services.ServiceProvider.GetRequiredService<IEvidenceFreeze>();
        world.Api.Statements.Clear();

        // No tenant scope is open on purpose: validation runs before the scope guard and before any statement,
        // so a malformed request from a caller bug never reaches the guard.
        FreezeRequest? request = caseName switch
        {
            "request_null" => null,
            "element_null" => new FreezeRequest(runId, [null!]),
            _ => InvalidRequest(caseName, runId, key),
        };
        var expected = caseName is "request_null" or "element_null"
            ? typeof(ArgumentNullException)
            : typeof(ArgumentException);

        var thrown = await Should.ThrowAsync<ArgumentException>(() => freeze.FreezeAsync(
            request!,
            TestContext.Current.CancellationToken));

        thrown.GetType().ShouldBe(expected);
        Statements(world).ShouldBeEmpty();
    }

    /// <summary>One malformed request of the shape list; a requirement set is otherwise well formed.</summary>
    private static FreezeRequest InvalidRequest(string caseName, Guid runId, string key) => caseName switch
    {
        "run_id_empty" => new FreezeRequest(Guid.Empty, []),
        "too_many_specs" => new FreezeRequest(
            runId,
            Enumerable
                .Range(0, FreezeRequest.MaxRequired + 1)
                .Select(_ => new RequiredArtifactSpec(key, ArtifactKind.Trace, FreezeCodes.ArtifactMissing))
                .ToList()),
        "execution_key_short" => Requirement(runId, new string('a', 63), ArtifactKind.Trace),
        "execution_key_upper" => Requirement(runId, new string('A', 64), ArtifactKind.Trace),
        "execution_key_non_hex" => Requirement(runId, new string('g', 64), ArtifactKind.Trace),
        "kind_unknown" => Requirement(runId, key, "executable"),
        "reason_code_upper" => Requirement(runId, key, ArtifactKind.Trace, "Artifact_missing"),
        "reason_code_empty" => Requirement(runId, key, ArtifactKind.Trace, string.Empty),
        "reason_code_too_long" => Requirement(runId, key, ArtifactKind.Trace, "a" + new string('b', 64)),
        "attempt_no_zero" => Requirement(runId, key, ArtifactKind.Trace, attemptNo: 0),
        "attempt_no_negative" => Requirement(runId, key, ArtifactKind.Trace, attemptNo: -1),
        _ => throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "unknown validation case"),
    };

    /// <summary>One well-formed request carrying a single (here malformed) requirement.</summary>
    private static FreezeRequest Requirement(
        Guid runId,
        string key,
        string kind,
        string reasonCode = FreezeCodes.ArtifactMissing,
        int? attemptNo = null) =>
        new(runId, [new RequiredArtifactSpec(key, kind, reasonCode, attemptNo)]);

    // ------------------------------------------------------------------ integrity: a disagreeing attempt_id

    [Fact]
    public async Task Freeze_StoredAttemptIdDisagreeing_ThrowsAndRewritesNothing()
    {
        await using var world = await StartAsync();
        var ownPlan = await SeedPlanAsync(world);
        var otherPlan = await SeedPlanAsync(world);
        var key = ownPlan.Executions[0].ExecutionKey;

        await SeedAttemptsAsync(
            world,
            ownPlan.RunId,
            new CaseAttemptInput(key, 1, "passed"),
            new CaseAttemptInput(key, 2, "passed"));
        await SeedAttemptsAsync(
            world,
            otherPlan.RunId,
            new CaseAttemptInput(otherPlan.Executions[0].ExecutionKey, 1, "passed"));

        var foreignAttemptId = await _fixture.ScalarAsync<Guid>(
            world.Database,
            $"select id from public.case_attempt where org_id = '{world.Org}' and run_id = '{otherPlan.RunId}'");

        var conflicted = Guid.CreateVersion7();
        var resolvable = Guid.CreateVersion7();
        await InsertArtifactsAsync(
            world,
            ownPlan.RunId,
            [
                // 0032's artifact_attempt_fk is (org_id, attempt_id): an attempt of ANOTHER run passes the database.
                new ArtifactSeed(conflicted, key, 1, ArtifactKind.Trace, AttemptId: foreignAttemptId),
                // Would resolve on its own: nothing is rewritten when the batch is refused.
                new ArtifactSeed(resolvable, key, 2, ArtifactKind.Trace),
            ]);

        await using (var scope = await FreezeScope.BeginAsync(world.Api, world.Org))
        {
            world.Api.Statements.Clear();

            await Should.ThrowAsync<InvalidOperationException>(() => scope.RunAsync(ownPlan.RunId));

            UpdateStatements(Statements(world)).ShouldBeEmpty();
            (await scope.AttemptIdAsync(conflicted)).ShouldBe(foreignAttemptId);
            (await scope.AttemptIdAsync(resolvable)).ShouldBeNull();

            // The scope is left to roll back, like 05's reconcile on a refusal.
        }

        (await _fixture.ScalarAsync<string>(
            world.Database,
            $"select attempt_id::text from public.artifact where id = '{conflicted}'"))
            .ShouldBe(foreignAttemptId.ToString());
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.artifact where id = '{resolvable}' and attempt_id is null"))
            .ShouldBe(1);

        // The other fail-closed shape of the same clause: a stored attempt_id whose pair resolves to no
        // case_attempt at all (attempt 7 of a planned execution that recorded no such attempt) is a disagreement
        // too — the row is never silently reinterpreted as an orphan.
        var stranded = Guid.CreateVersion7();
        await InsertArtifactAsync(
            world,
            otherPlan.RunId,
            new ArtifactSeed(
                stranded,
                otherPlan.Executions[0].ExecutionKey,
                7,
                ArtifactKind.Trace,
                AttemptId: foreignAttemptId));

        await using (var scope = await FreezeScope.BeginAsync(world.Api, world.Org))
        {
            world.Api.Statements.Clear();

            await Should.ThrowAsync<InvalidOperationException>(() => scope.RunAsync(otherPlan.RunId));

            UpdateStatements(Statements(world)).ShouldBeEmpty();
            (await scope.AttemptIdAsync(stranded)).ShouldBe(foreignAttemptId);
        }

        (await _fixture.ScalarAsync<string>(
            world.Database,
            $"select attempt_id::text from public.artifact where id = '{stranded}'"))
            .ShouldBe(foreignAttemptId.ToString());
    }
}
