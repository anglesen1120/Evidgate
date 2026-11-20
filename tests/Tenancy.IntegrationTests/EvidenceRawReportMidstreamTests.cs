using System.Text;
using System.Text.Json;
using Evidgate.Modules.Evidence.Application;
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Evidence.Domain;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Runner;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

// The `raw_report_parse` handler over a child that maps one case and then fails: the sink has already accepted a
// testcase when the verdict arrives, so every case here proves the refusal is still the only thing the attempt
// writes. The cases are what a partial run ends with — a cap, a broken frame, a broken pipe, an exit code, the wall
// clock — plus the child's own rejection record, which outranks the mapper fault the damaged stream left behind.
public sealed partial class EvidenceRawReportParseTests
{
    [Fact]
    public async Task RecordCapHitAfterAMappedCase_WritesOnlyTheRefusal() =>
        await AssertRefusedAfterMappedCaseAsync(MidstreamOutcome.RecordCapHit, ParseRunRejection.Crashed);

    [Fact]
    public async Task ByteCapHitAfterAMappedCase_WritesOnlyTheRefusal() =>
        await AssertRefusedAfterMappedCaseAsync(MidstreamOutcome.ByteCapHit, ParseRunRejection.Crashed);

    [Fact]
    public async Task MalformedFrameAfterAMappedCase_WritesOnlyTheRefusal() =>
        await AssertRefusedAfterMappedCaseAsync(MidstreamOutcome.MalformedFrame, ParseRunRejection.Crashed);

    [Fact]
    public async Task BrokenPipeAfterAMappedCase_WritesOnlyTheRefusal() =>
        await AssertRefusedAfterMappedCaseAsync(MidstreamOutcome.BrokenPipe, ParseRunRejection.Crashed);

    [Fact]
    public async Task KilledChildAfterAMappedCase_WritesOnlyTheRefusal() =>
        await AssertRefusedAfterMappedCaseAsync(MidstreamOutcome.Killed, ParseRunRejection.Crashed);

    [Fact]
    public async Task WallClockAfterAMappedCase_WritesOnlyTheTimeoutRefusal() =>
        await AssertRefusedAfterMappedCaseAsync(MidstreamOutcome.Stalled, ParseRunRejection.Timeout);

    [Fact]
    public async Task HeapLimitExitAfterAMappedCase_WritesOnlyTheOomRefusal() =>
        await AssertRefusedAfterMappedCaseAsync(MidstreamOutcome.MemoryLimit, ParseRunRejection.Oom);

    [Fact]
    public async Task ChildRejectionAfterAMismatchedEnd_WinsOverTheMapperFault() =>
        await AssertRefusedAfterMappedCaseAsync(
            MidstreamOutcome.RejectedAfterMismatchedEnd,
            ParsingFailureReason.DtdProhibited);

    [Fact]
    public async Task ChildRejectionOfTheEntryBudget_IsTheCapExceededRefusal() =>
        await AssertRefusedAfterMappedCaseAsync(MidstreamOutcome.RejectedCapExceeded, ArtifactFailureCode.CapExceeded);

    [Fact]
    public async Task Control_ACompleteStreamAfterTheMappedCase_WritesItsEvent()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedOneCaseAsync(world);
        var artifactId = await SeedArtifactAsync(world, MidstreamScripts.OneCaseBody);

        var result = await ParseAsync(
            world, artifactId, ProducerKind, runner: MidstreamChildRunner.For(MidstreamOutcome.Complete));

        result.Status.ShouldBe(ParseRawReportOutcome.Parsed);
        result.Events.ShouldBe(1, "the control proves the prefix really maps the planned case");
        (await CountAsync(world, "event", artifactId)).ShouldBe(1);
        (await CountAsync(world, "case_attempt")).ShouldBe(1);
    }

    private async Task AssertRefusedAfterMappedCaseAsync(MidstreamOutcome midstream, string reason)
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedOneCaseAsync(world);
        var artifactId = await SeedArtifactAsync(world, MidstreamScripts.OneCaseBody);

        var result = await ParseAsync(
            world, artifactId, ProducerKind, runner: MidstreamChildRunner.For(midstream));

        result.Status.ShouldBe(ParseRawReportOutcome.Malformed);
        result.Reason.ShouldBe(reason);
        result.Events.ShouldBe(0);

        // One refusal row for this report, its own trigger and nothing else: the mapped case left no event, attempt,
        // receipt or slot behind, not even the run transition a successful parse writes.
        await AssertNothingWrittenAsync(world, artifactId, refusals: 1);
    }

    /// <summary>The report's case name resolves to a planned execution, so the prefix maps a real case and not a ghost.</summary>
    private Task SeedOneCaseAsync(World world) =>
        MidstreamScripts.SeedOneCaseAsync(_fixture, world.Database, world.Org, world.Project, world.RunId);
}

/// <summary>
/// The child scripts these cases run: every one begins with the three records that map the report's single testcase,
/// so the mapped case is in the sink before the remaining lines decide the verdict.
/// </summary>
internal static class MidstreamScripts
{
    /// <summary>The report the prefix records were read from: one testcase, no attributes on either element.</summary>
    internal static readonly byte[] OneCaseBody = "<testsuite><testcase name=\"one\"/></testsuite>"u8.ToArray();

    /// <summary>The execution key the seeded <c>one</c> case plans, so the prefix resolves to it.</summary>
    internal static readonly string OneKey = new('e', 64);

    private static readonly string[] Prefix =
    [
        """{"t":"xml","e":"start","n":"testsuite"}""",
        """{"t":"xml","e":"start","n":"testcase","a":[["name","one"]]}""",
        """{"t":"xml","e":"end"}""",
    ];

    /// <summary>The prefix's own byte count: the byte cap that stops the run right after the mapped case.</summary>
    internal static int PrefixBytes => Encoding.UTF8.GetByteCount(string.Join('\n', Prefix) + "\n");

    internal static byte[] Ndjson(bool marker, params string[] records)
    {
        var lines = new List<string>(records);
        if (marker)
        {
            lines.Add(Marker(records.Length));
        }

        return Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n");
    }

    internal static string[] WithPrefix(params string[] tail) => [.. Prefix, .. tail];

    /// <summary>
    /// One planned execution of the world's run whose case is named <c>one</c>: the bare name resolves to it, because
    /// the report carries no classname.
    /// </summary>
    internal static async Task SeedOneCaseAsync(
        PostgresTenancyFixture fixture,
        string database,
        Guid org,
        Guid project,
        Guid runId)
    {
        var caseId = Guid.CreateVersion7();
        var revisionId = Guid.CreateVersion7();
        var hash = "sha256:" + new string('f', 64);

        await fixture.ExecuteAsync(
            database,
            "insert into public.test_case (id, org_id, project_id, annotation_id, name, framework, first_seen_commit) "
            + $"values ('{caseId:D}', '{org:D}', '{project:D}', '{Guid.CreateVersion7():D}', 'one', 'pytest', 'abcdef0')");
        await fixture.ExecuteAsync(
            database,
            "insert into public.test_revision (id, org_id, case_id, revision_hash, commit_sha, file_path, framework, "
            + $"content_sha256, assertion_hash) values ('{revisionId:D}', '{org:D}', '{caseId:D}', '{hash}', 'abcdef0', "
            + $"'b', 'pytest', '{hash}', '{hash}')");
        await fixture.ExecuteAsync(
            database,
            "insert into public.planned_execution "
            + "(id, org_id, run_id, execution_key, case_revision_id, variant_hash, target_env, shard_id) values "
            + $"('{Guid.CreateVersion7():D}', '{org:D}', '{runId:D}', '{OneKey}', '{revisionId:D}', "
            + $"'sha256:{new string('b', 64)}', 'staging', 'shard-1')");
    }

    private static string Marker(int count) => JsonSerializer.Serialize(new { done = true, count });
}

/// <summary>The outcome of one child run once the mapped case has been fed to the sink.</summary>
internal enum MidstreamOutcome
{
    /// <summary>Every line arrived, the marker closed the stream and the child exited 0.</summary>
    Complete,

    /// <summary>The record cap refused the closing element after the case was mapped.</summary>
    RecordCapHit,

    /// <summary>The byte cap refused the bytes after the mapped case's own records.</summary>
    ByteCapHit,

    /// <summary>A line that is not one JSON object followed the mapped case.</summary>
    MalformedFrame,

    /// <summary>The stdout pipe died after the mapped case.</summary>
    BrokenPipe,

    /// <summary>The child was killed (128 + SIGKILL) with a complete stream.</summary>
    Killed,

    /// <summary>The wall clock expired with the stream still open.</summary>
    Stalled,

    /// <summary>The child caught its own heap limit and exited 4.</summary>
    MemoryLimit,

    /// <summary>A mismatched end record, then the child's own rejection of a prohibited document type.</summary>
    RejectedAfterMismatchedEnd,

    /// <summary>The child's own rejection of the report's entry budget.</summary>
    RejectedCapExceeded,
}

/// <summary>
/// The parse child's stand-in at the state a partial run leaves: it frames real NDJSON into the request's sink with
/// the production protocol, so the mapped case reaches the mapper exactly as the child writes it, and it then reports
/// the verdict the production runner would report for that stream and exit code.
/// </summary>
internal sealed class MidstreamChildRunner : IParseRunner
{
    private const string CloseSuite = """{"t":"xml","e":"end"}""";
    private const string MismatchedEnd = """{"t":"xml","e":"end","n":"testcase"}""";
    private const string RejectedDtd = """{"t":"rejected","code":"artifact_invalid","reason":"dtd_prohibited"}""";
    private const string RejectedCap = """{"t":"rejected","code":"artifact_invalid","reason":"cap_exceeded"}""";
    private const string NotJson = "not-json";

    private const int MaxRecordBytes = 64 * 1024;
    private const int GenerousRecords = 32;
    private const long GenerousBytes = 1L << 20;

    private readonly byte[] stdout;
    private readonly int maxRecords;
    private readonly long maxBytes;
    private readonly int readChunk;
    private readonly int exitCode;
    private readonly bool brokenPipe;
    private readonly bool stalled;

    private MidstreamChildRunner(
        byte[] stdout,
        int maxRecords = GenerousRecords,
        long maxBytes = GenerousBytes,
        int readChunk = 0,
        int exitCode = 0,
        bool brokenPipe = false,
        bool stalled = false)
    {
        this.stdout = stdout;
        this.maxRecords = maxRecords;
        this.maxBytes = maxBytes;
        this.readChunk = readChunk;
        this.exitCode = exitCode;
        this.brokenPipe = brokenPipe;
        this.stalled = stalled;
    }

    /// <summary>The runner one midstream outcome needs, with the caps a runner would have been configured with.</summary>
    public static MidstreamChildRunner For(MidstreamOutcome outcome) => outcome switch
    {
        MidstreamOutcome.Complete => new(MidstreamScripts.Ndjson(true, MidstreamScripts.WithPrefix(CloseSuite))),
        MidstreamOutcome.RecordCapHit => new(
            MidstreamScripts.Ndjson(true, MidstreamScripts.WithPrefix(CloseSuite)),
            maxRecords: 3),
        MidstreamOutcome.ByteCapHit => new(
            MidstreamScripts.Ndjson(true, MidstreamScripts.WithPrefix(CloseSuite)),
            maxBytes: MidstreamScripts.PrefixBytes,
            readChunk: MidstreamScripts.PrefixBytes),
        MidstreamOutcome.MalformedFrame => new(
            MidstreamScripts.Ndjson(false, MidstreamScripts.WithPrefix(NotJson))),
        MidstreamOutcome.BrokenPipe => new(
            MidstreamScripts.Ndjson(false, MidstreamScripts.WithPrefix()),
            brokenPipe: true),
        MidstreamOutcome.Killed => new(
            MidstreamScripts.Ndjson(true, MidstreamScripts.WithPrefix(CloseSuite)),
            exitCode: 137),
        MidstreamOutcome.Stalled => new(
            MidstreamScripts.Ndjson(false, MidstreamScripts.WithPrefix()),
            exitCode: 137,
            stalled: true),
        MidstreamOutcome.MemoryLimit => new(
            MidstreamScripts.Ndjson(true, MidstreamScripts.WithPrefix(CloseSuite)),
            exitCode: 4),
        MidstreamOutcome.RejectedAfterMismatchedEnd => new(
            MidstreamScripts.Ndjson(true, MidstreamScripts.WithPrefix(MismatchedEnd, RejectedDtd))),
        MidstreamOutcome.RejectedCapExceeded => new(
            MidstreamScripts.Ndjson(true, MidstreamScripts.WithPrefix(RejectedCap))),
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };

    /// <inheritdoc />
    public async Task<ParseRunResult> RunAsync(ParseRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sink = request.Sink
            ?? throw new InvalidOperationException("the handler must name the sink the child's records stream into");

        using var input = new MemoryStream();
        await request.Input.CopyToAsync(input, cancellationToken).ConfigureAwait(false);

        using Stream stream = brokenPipe ? new BrokenPipeStream(stdout) : new ChunkedStream(stdout, readChunk);
        var protocol = new ChildOutputProtocol(maxBytes, maxRecords, MaxRecordBytes, sink);
        var capture = await protocol.ReadAsync(stream, static () => { }).ConfigureAwait(false);

        // The wall clock is the runner's own timer, so that verdict is stated; the prefix above was still framed for
        // real, so the mapper holds the mapped case the timeout has to discard. Everything else follows the
        // production order: a cap or a frame the parent refused is the parent's decision and outranks the exit code
        // its own kill produced, and only then does the child's exit code decide.
        if (stalled)
        {
            return Failed(ParseRunFailureKind.WallClockExceeded);
        }

        if (capture.Failure is { } failure)
        {
            return Failed(failure);
        }

        if (exitCode != 0)
        {
            return Failed(ParseRunFailureKind.ExitedAbnormally);
        }

        return new ParseRunSucceeded(Array.Empty<ParseRecord>(), exitCode, capture.OutputBytes, capture.RecordCount);

        ParseRunFailed Failed(ParseRunFailureKind kind) =>
            new(kind, exitCode, capture.OutputBytes, capture.RecordCount);
    }

    /// <summary>A pipe that answers with a bounded slice per read, so the byte cap can be refused after the prefix.</summary>
    private sealed class ChunkedStream(byte[] content, int chunk) : MemoryStream(content, writable: false)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(
                chunk > 0 && buffer.Length > chunk ? buffer.Slice(0, chunk) : buffer,
                cancellationToken);
    }

    /// <summary>A pipe whose write end is gone: the first block arrives, every read after it throws.</summary>
    private sealed class BrokenPipeStream(byte[] content) : MemoryStream(content, writable: false)
    {
        private bool read;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (read)
            {
                throw new IOException("the child's stdout pipe broke after the mapped case");
            }

            read = true;

            return base.ReadAsync(buffer, cancellationToken);
        }
    }
}
