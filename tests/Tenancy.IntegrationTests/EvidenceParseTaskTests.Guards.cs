// The row guards and no-op paths of one parse attempt: a row whose key is not its own, a lost race, a spawn failure,
// a cancelled attempt, a replay, and a row another tenant or an operator already decided.
using System.Text;
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Evidence.Domain;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Runner;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class EvidenceParseTaskTests
{
    /// <summary>
    /// Defence in depth over the 0028 shape CHECK: a <c>ready</c> row whose <c>object_key</c> is not the final key its
    /// own coordinates mint (here the staging key <c>pending/&lt;org&gt;/&lt;id&gt;</c>, which the CHECK's other arm
    /// admits, built from the row's own id so the row itself is legal) is never read. The attempt throws the fixed first
    /// line, so the queue retries and an operator sees the corrupt row.
    /// </summary>
    [Fact]
    public async Task Parse_Row_Whose_Key_Is_Not_Its_Minted_Final_Key_Is_Not_Read()
    {
        await using var world = await StartAsync();
        var seeded = await SeedAsync(
            world,
            ArtifactState.Ready,
            failureCode: null,
            objectKey: id => $"pending/{world.Org:D}/{id:D}");
        var runner = new StubParseRunner
        {
            Result = new ParseRunFailed(ParseRunFailureKind.ExitedAbnormally, 152, 0, 0),
        };

        var versionBefore = await VersionAsync(world, seeded.ArtifactId);

        var exception = await Should.ThrowAsync<InvalidOperationException>(
            () => InvokeAsync(world, seeded.ArtifactId, runner));

        exception.Message.ShouldStartWith(
            "the artifact's stored object key is not the key its row mints",
            Case.Sensitive,
            "the message is fixed, so an operator greps one line");
        exception.Message.ShouldNotContain(seeded.ObjectKey, Case.Sensitive, "no key reaches a task's last_error");
        runner.Calls.ShouldBe(0, "nothing was handed to a child");
        (await ColumnAsync(world, seeded.ArtifactId, "state")).ShouldBe(ArtifactState.Ready, "no verdict was invented");
        (await ColumnAsync(world, seeded.ArtifactId, "failure_code")).ShouldBeNull();
        (await VersionAsync(world, seeded.ArtifactId)).ShouldBe(versionBefore, "nothing was written");
    }

    /// <summary>
    /// The guarded <c>UPDATE</c> losing a race is not a verdict. When another writer changes the row while the parse
    /// runs, the handler's statement matches no row; the handler re-reads, finds the artifact still <c>ready</c>, and
    /// throws so the queue parses it again — the redelivered attempt then writes the rejection. Treating the lost race
    /// as "already decided" would leave the artifact <c>ready</c> and unparsed forever.
    /// </summary>
    [Fact]
    public async Task Parse_Lost_Race_Is_Retried_Not_Treated_As_A_Verdict()
    {
        await using var world = await StartAsync();
        var seeded = await SeedReadyAsync(world);
        var versionBefore = await VersionAsync(world, seeded.ArtifactId);

        // A writer that is not this attempt touches the row between the handler's read and its verdict, which bumps
        // `version` under the tracked copy. `expires_at` is one of the columns the app role may update and carries no
        // foreign key (0032 gave `attempt_id` one, so it cannot take a made-up uuid), and the 0028 trigger increments
        // `version` for any write, which is exactly the race being modelled.
        var racing = new StubParseRunner
        {
            DuringRun = () => _fixture.ExecuteAsync(
                world.Database,
                $"update public.artifact set expires_at = now() + interval '1 day' where id = '{seeded.ArtifactId:D}'"),
            Result = new ParseRunFailed(ParseRunFailureKind.ExitedAbnormally, 152, 0, 0),
        };

        var exception = await Should.ThrowAsync<InvalidOperationException>(
            () => InvokeAsync(world, seeded.ArtifactId, racing));

        exception.Message.ShouldContain("retried", Case.Sensitive, "the attempt is thrown out for redelivery");
        (await ColumnAsync(world, seeded.ArtifactId, "state")).ShouldBe(ArtifactState.Ready, "the row is still undecided");
        (await ColumnAsync(world, seeded.ArtifactId, "failure_code")).ShouldBeNull();
        (await VersionAsync(world, seeded.ArtifactId)).ShouldBe(
            versionBefore + 1,
            "only the other writer's statement reached the row");

        // The redelivery: a fresh attempt reads the row again and writes the verdict it derives.
        var retry = new StubParseRunner
        {
            Result = new ParseRunFailed(ParseRunFailureKind.ExitedAbnormally, 152, 0, 0),
        };

        await InvokeAsync(world, seeded.ArtifactId, retry);

        (await ColumnAsync(world, seeded.ArtifactId, "state")).ShouldBe(ArtifactState.Rejected);
        (await ColumnAsync(world, seeded.ArtifactId, "failure_code")).ShouldBe(ParseRunRejection.Timeout);
        retry.Calls.ShouldBe(1);

        // And a concurrent writer that already decided the artifact is the one case a lost race means the work is done:
        // the handler returns quietly and the stored verdict stands.
        var decided = await SeedReadyAsync(world);

        var settled = new StubParseRunner
        {
            DuringRun = () => _fixture.ExecuteAsync(
                world.Database,
                $"update public.artifact set state = '{ArtifactState.Rejected}', failure_code = '{ParseRunRejection.Crashed}' "
                + $"where id = '{decided.ArtifactId:D}'"),
            Result = new ParseRunFailed(ParseRunFailureKind.ExitedAbnormally, 4, 0, 0),
        };

        await InvokeAsync(world, decided.ArtifactId, settled);

        (await ColumnAsync(world, decided.ArtifactId, "state")).ShouldBe(ArtifactState.Rejected);
        (await ColumnAsync(world, decided.ArtifactId, "failure_code")).ShouldBe(
            ParseRunRejection.Crashed,
            "the verdict that landed first stands");

        // And an artifact a delete took out of the parseable set has no evidence left to judge: the attempt is the same
        // no-op the top of the handler makes, not a retry that would end in a dead letter for bytes nobody keeps.
        var deleting = await SeedReadyAsync(world);

        var deletedMidParse = new StubParseRunner
        {
            DuringRun = () => _fixture.ExecuteAsync(
                world.Database,
                $"update public.artifact set state = '{ArtifactState.PendingDelete}' where id = '{deleting.ArtifactId:D}'"),
            Result = new ParseRunFailed(ParseRunFailureKind.ExitedAbnormally, 152, 0, 0),
        };

        await InvokeAsync(world, deleting.ArtifactId, deletedMidParse);

        (await ColumnAsync(world, deleting.ArtifactId, "state")).ShouldBe(
            ArtifactState.PendingDelete,
            "the row keeps the state the delete gave it");
        (await ColumnAsync(world, deleting.ArtifactId, "failure_code")).ShouldBeNull("no verdict is written for it");
    }

    /// <summary>
    /// 00 §5.7 S3 at the handler's own boundary: the one Warning line a rejection writes carries the excerpt the runner
    /// already bounded and redacted, and nothing else. The tenant-visible code is one of the fixed codes, the excerpt is
    /// never stored on the row, and the path that throws carries no stderr in its message.
    /// </summary>
    [Fact]
    public async Task Parse_Stderr_Excerpt_Reaches_One_Redacted_Warning_Line_Only()
    {
        // The runner redacts before handing the excerpt over (ParseRunFailed.StderrExcerpt); this is what the handler
        // receives, and it may only ever log it.
        const string Excerpt = "parser: unexpected token at offset 12; token=[redacted]";

        await using var world = await StartAsync();
        var seeded = await SeedReadyAsync(world);
        var recorder = new RecordingLogger();
        var runner = new StubParseRunner
        {
            Result = new ParseRunFailed(ParseRunFailureKind.MalformedOutput, 0, 0, 0, Excerpt),
        };

        await InvokeAsync(world, seeded.ArtifactId, runner, logger: recorder);

        var line = recorder.Entries.ShouldHaveSingleItem();
        line.Level.ShouldBe(LogLevel.Warning);
        line.Message.ShouldContain(Excerpt, Case.Sensitive, "the redacted excerpt is what an operator reads");
        line.Message.ShouldContain(ArtifactFailureCode.ParseCrashed, Case.Sensitive);

        var failureCode = await ColumnAsync(world, seeded.ArtifactId, "failure_code") ?? string.Empty;
        new[]
        {
            ArtifactFailureCode.ParseOom,
            ArtifactFailureCode.ParseTimeout,
            ArtifactFailureCode.ParseCrashed,
        }.ShouldContain(failureCode, "the tenant-visible value is a fixed code");
        failureCode.ShouldNotContain("redacted", Case.Sensitive, "no excerpt text is stored");
        (await ColumnAsync(world, seeded.ArtifactId, "state")).ShouldBe(ArtifactState.Rejected);
    }

    /// <summary>One NDJSON record exactly as the child writes it: the JSON object's UTF-8 bytes.</summary>
    /// <param name="json">The record's JSON text, without a terminating newline.</param>
    private static ParseRecord Record(string json) => new(Encoding.UTF8.GetBytes(json));

    /// <summary>
    /// A spawn failure means no child existed to judge the artifact: the handler throws so the queue retries with
    /// backoff (06.3.6 §6), and the row keeps its state — a limit verdict is never invented for a host that never ran.
    /// The thrown message is the task's <c>last_error</c>, so it carries neither the object key nor any stderr text.
    /// </summary>
    [Fact]
    public async Task Parse_SpawnFailure_Throws_And_Leaves_The_Artifact_Parseable()
    {
        await using var world = await StartAsync();
        var seeded = await SeedReadyAsync(world);
        var runner = new StubParseRunner
        {
            // A spawn failure has no child, so a real run has no excerpt; the stub states one anyway, because a message
            // that echoed stderr would be a leak whatever produced it.
            Result = new ParseRunFailed(
                ParseRunFailureKind.SpawnFailed,
                ChildExitCode: null,
                0,
                0,
                "boom: internal detail [redacted]"),
        };

        var exception = await Should.ThrowAsync<InvalidOperationException>(
            () => InvokeAsync(world, seeded.ArtifactId, runner));

        exception.Message.ShouldNotContain(seeded.ObjectKey, Case.Sensitive, "no key reaches a task's last_error");
        exception.Message.ShouldNotContain("boom", Case.Sensitive, "no stderr reaches a task's last_error");
        (await ColumnAsync(world, seeded.ArtifactId, "state")).ShouldBe(ArtifactState.Ready);
        (await ColumnAsync(world, seeded.ArtifactId, "failure_code")).ShouldBeNull();
    }

    /// <summary>
    /// AC8's "parsed once": an artifact that already carries a verdict is not parsed again, so a replayed row costs no
    /// child process and no statement.
    /// </summary>
    [Fact]
    public async Task Parse_Already_Rejected_Is_A_NoOp_Without_Starting_The_Child()
    {
        await using var world = await StartAsync();
        var seeded = await SeedAsync(world, state: ArtifactState.Rejected, failureCode: ArtifactFailureCode.ParseOom);
        var runner = new StubParseRunner();

        await InvokeAsync(world, seeded.ArtifactId, runner);

        runner.Calls.ShouldBe(0, "a decided artifact is never parsed again");
        (await ColumnAsync(world, seeded.ArtifactId, "state")).ShouldBe(ArtifactState.Rejected);
        (await ColumnAsync(world, seeded.ArtifactId, "failure_code")).ShouldBe(ArtifactFailureCode.ParseOom);
    }

    /// <summary>
    /// A cancellation is the caller's (lease loss, shutdown), never a parse verdict: the runner's own cancellation
    /// propagates, and the artifact keeps its state for the redelivered attempt.
    /// </summary>
    [Fact]
    public async Task Parse_Cancellation_Is_Not_A_Verdict()
    {
        await using var world = await StartAsync();
        var seeded = await SeedReadyAsync(world);
        var runner = new StubParseRunner { Throws = new OperationCanceledException("lease lost") };

        await Should.ThrowAsync<OperationCanceledException>(() => InvokeAsync(world, seeded.ArtifactId, runner));

        runner.Calls.ShouldBe(1, "the runner was started and then cancelled");
        (await ColumnAsync(world, seeded.ArtifactId, "state")).ShouldBe(ArtifactState.Ready);
        (await ColumnAsync(world, seeded.ArtifactId, "failure_code")).ShouldBeNull();
    }

    /// <summary>
    /// A payload naming another tenant's artifact reaches nothing: RLS makes the row invisible, so the attempt changes
    /// nothing and the other tenant's artifact stays exactly as it was (06.3.6 §8 "one artifact's bytes only").
    /// </summary>
    [Fact]
    public async Task Parse_Payload_Of_Another_Tenant_Is_Unreachable()
    {
        await using var world = await StartAsync();
        await using var other = await CreateOtherWorldAsync("globex");
        var foreign = await SeedReadyAsync(other);
        var runner = new StubParseRunner
        {
            Result = new ParseRunFailed(ParseRunFailureKind.ExitedAbnormally, 4, 0, 0),
        };

        await InvokeAsync(world, foreign.ArtifactId, runner);

        runner.Calls.ShouldBe(0, "another tenant's artifact is not a row in this scope");
        (await ColumnAsync(other, foreign.ArtifactId, "state")).ShouldBe(ArtifactState.Ready);
        (await ColumnAsync(other, foreign.ArtifactId, "failure_code")).ShouldBeNull();
    }

    /// <summary>
    /// AC8: two attempts over the same rejected artifact write once. The second attempt sees the stored verdict and
    /// starts nothing (and the guarded <c>UPDATE</c> would match no row even if it did), so the version increments a
    /// single time and the code is the first attempt's.
    /// </summary>
    [Fact]
    public async Task Parse_Replayed_Attempt_Writes_Once()
    {
        await using var world = await StartAsync();
        var seeded = await SeedReadyAsync(world);
        var runner = new StubParseRunner
        {
            Result = new ParseRunFailed(ParseRunFailureKind.ExitedAbnormally, 152, 0, 0),
        };

        var versionBefore = await VersionAsync(world, seeded.ArtifactId);

        await InvokeAsync(world, seeded.ArtifactId, runner);
        var afterFirst = await VersionAsync(world, seeded.ArtifactId);

        await InvokeAsync(world, seeded.ArtifactId, runner);

        afterFirst.ShouldBe(versionBefore + 1);
        (await VersionAsync(world, seeded.ArtifactId)).ShouldBe(afterFirst, "the replay changed nothing");
        runner.Calls.ShouldBe(1, "the replay never started a second child");
        (await ColumnAsync(world, seeded.ArtifactId, "failure_code")).ShouldBe(ParseRunRejection.Timeout);
    }

    /// <summary>
    /// The coordinator's freeze check: a freeze run after the rejection does not count the artifact. Before the verdict
    /// the ready artifact is reported (as an orphan: this fixture has no <c>case_attempt</c> for its pair), and after it
    /// the same call reports nothing at all — missing evidence never reads as complete, and rejected bytes are never
    /// evidence (00 §8.4, 06.5.1).
    /// </summary>
    [Fact]
    public async Task Parse_Rejected_Artifact_Is_Not_Counted_By_A_Later_Freeze()
    {
        await using var world = await StartAsync();
        var seeded = await SeedReadyAsync(world);

        var before = await FreezeAsync(world);
        before.Ready.Count.ShouldBe(0);
        before.Orphans.Count.ShouldBe(1, "before the verdict the artifact is still ready evidence");

        await InvokeAsync(
            world,
            seeded.ArtifactId,
            new StubParseRunner { Result = new ParseRunFailed(ParseRunFailureKind.MalformedOutput, 0, 0, 0) });

        var after = await FreezeAsync(world);
        after.Ready.ShouldBeEmpty();
        after.Orphans.ShouldBeEmpty("a rejected artifact is not evidence any more, so it is not in the frozen set");
    }
}
