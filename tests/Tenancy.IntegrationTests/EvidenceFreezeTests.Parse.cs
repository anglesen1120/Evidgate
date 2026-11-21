#pragma warning disable CA1861
using System.Globalization;
using Evidgate.BuildingBlocks.Canonicalization;
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Evidence.Domain;
using Evidgate.Modules.Orchestrator.Contracts;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class EvidenceFreezeTests
{
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
        var (otherOrg, _) = await CreateOtherTenantAsync(world, Unique.Slug("other"));
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
    /// The dispatcher's stamp on the parse outbox row. The app role may only insert outbox rows, so the stamp is
    /// applied out of band through the fixture's superuser connection.
    /// </summary>
    private Task DispatchParseOutboxAsync(World world, Guid artifactId) =>
        _fixture.ExecuteAsync(
            world.Database,
            "update public.outbox set dispatched_at = now() "
            + $"where org_id = '{world.Org}' and task_type = 'artifact_parse' "
            + $"and payload ->> 'artifact_id' = '{artifactId.ToString("D", CultureInfo.InvariantCulture)}'");

    /// <summary>
    /// The consumer's own transition of <paramref name="artifactId"/>'s parse row, keyed the way the dispatcher writes
    /// it and run as the queue role that owns the transition: the handler's verdict is durable in its transaction, so
    /// the row moves only after that commit.
    /// </summary>
    private async Task SetParseTaskStateAsync(World world, Guid artifactId, string state)
    {
        await using var scope = await TenantScope.BeginAsync(
            _fixture.WorkerConnectionString(world.Database), world.Org);

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
    /// The parse consumer's rejection, verdict first: the guarded <c>ready → rejected</c> write as the app role
    /// that owns the artifact, then the task's <c>done</c> through the queue role's own transition.
    /// </summary>
    private async Task RejectReportAsync(World world, Guid artifactId)
    {
        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await scope.ExecuteAsync(
                "update public.artifact set state = @state, failure_code = @failure_code "
                + "where org_id = @org_id and id = @id and state = 'ready'",
                ("state", ArtifactState.Rejected),
                ("failure_code", ArtifactFailureCode.ParseCrashed),
                ("org_id", world.Org),
                ("id", artifactId)))
                .ShouldBe(1);
            await scope.CommitAsync();
        }

        await SetParseTaskStateAsync(world, artifactId, "done");
    }
}
