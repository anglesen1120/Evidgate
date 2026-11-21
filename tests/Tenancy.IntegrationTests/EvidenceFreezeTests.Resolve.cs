#pragma warning disable CA1861
using System.Globalization;
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Evidence.Domain;
using Evidgate.Modules.Orchestrator.Contracts;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class EvidenceFreezeTests
{
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
}
