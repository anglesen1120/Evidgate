// The verdicts one parse attempt can reach: the mapper's reason codes, the child's own rejection record, and the two
// published caps the read and the child are bounded by.
using System.Globalization;
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Evidence.Domain;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Runner;
using Evidgate.Modules.Ingestion.Contracts;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class EvidenceParseTaskTests
{
    /// <summary>
    /// The three tenant-visible reasons are the domain vocabulary and the mapper's constants, and they are the same
    /// three strings (06.3.6 AC3): a rename in one place cannot silently change the wire value.
    /// </summary>
    [Fact]
    public void ParseReasons_Are_The_Domain_Failure_Codes()
    {
        ParseRunRejection.Oom.ShouldBe(ArtifactFailureCode.ParseOom);
        ParseRunRejection.Timeout.ShouldBe(ArtifactFailureCode.ParseTimeout);
        ParseRunRejection.Crashed.ShouldBe(ArtifactFailureCode.ParseCrashed);
        ArtifactFailureCode.ParseOom.ShouldBe("parse_oom");
        ArtifactFailureCode.ParseTimeout.ShouldBe("parse_timeout");
        ArtifactFailureCode.ParseCrashed.ShouldBe("parse_crashed");
    }

    /// <summary>
    /// AC3: every failed shape ends as <c>rejected</c> with the mapped reason, exactly one version increment per write,
    /// and the row's bytes/scan state are preserved (the artifact keeps what it declared; only the state and the code
    /// change).
    /// </summary>
    /// <param name="failure">The name of the <see cref="ParseRunFailureKind"/> the runner reports.</param>
    /// <param name="exitCode">The child exit code the runner reports.</param>
    /// <param name="reason">The reason the row must carry.</param>
    [Theory]
    [MemberData(nameof(FailureMapping))]
    public async Task Parse_Failed_Run_Is_Rejected_With_The_Mapped_Reason(
        string failure,
        int? exitCode,
        string reason)
    {
        await using var world = await StartAsync();
        var seeded = await SeedReadyAsync(world);
        var runner = new StubParseRunner
        {
            Result = new ParseRunFailed(Enum.Parse<ParseRunFailureKind>(failure), exitCode, 1024, 3, "boom"),
        };

        var versionBefore = await VersionAsync(world, seeded.ArtifactId);

        await InvokeAsync(world, seeded.ArtifactId, runner);

        (await ColumnAsync(world, seeded.ArtifactId, "state")).ShouldBe(ArtifactState.Rejected);
        (await ColumnAsync(world, seeded.ArtifactId, "failure_code")).ShouldBe(reason);
        (await ColumnAsync(world, seeded.ArtifactId, "bytes")).ShouldBe(
            ReportBody.LongLength.ToString(CultureInfo.InvariantCulture), "the verified length is kept");
        (await VersionAsync(world, seeded.ArtifactId)).ShouldBe(
            versionBefore + 1, "one guarded write, one version increment");
        runner.Inputs.ShouldHaveSingleItem().ShouldBe(ReportBody, "the child was handed the stored bytes");
    }

    /// <summary>
    /// A successful run persists nothing — the parity the coordinator asked for: no in-process consumer stores parsed
    /// artifact output today (the <c>IParsing</c> callers return the profile's value to their own caller), so a
    /// successful out-of-process parse must not invent a representation either. The stream here is a real one (element
    /// and end records, no rejection), so the record scan runs and still changes nothing.
    /// </summary>
    [Fact]
    public async Task Parse_Successful_Run_Writes_Nothing()
    {
        await using var world = await StartAsync();
        var seeded = await SeedReadyAsync(world);
        var runner = new StubParseRunner
        {
            Result = new ParseRunSucceeded(
                [
                    Record("""{"t":"xml","e":"start","n":"report","a":[]}"""),
                    Record("""{"t":"xml","e":"start","n":"testcase","a":[["name","checkout"],["outcome","passed"]]}"""),
                    Record("""{"t":"xml","e":"end"}"""),
                ],
                ChildExitCode: 0,
                OutputBytes: 256,
                OutputRecords: 3),
        };

        var versionBefore = await VersionAsync(world, seeded.ArtifactId);
        var updatedBefore = await ColumnAsync(world, seeded.ArtifactId, "updated_at");

        await InvokeAsync(world, seeded.ArtifactId, runner);

        (await ColumnAsync(world, seeded.ArtifactId, "state")).ShouldBe(ArtifactState.Ready);
        (await ColumnAsync(world, seeded.ArtifactId, "failure_code")).ShouldBeNull();
        (await VersionAsync(world, seeded.ArtifactId)).ShouldBe(versionBefore, "nothing was written");
        (await ColumnAsync(world, seeded.ArtifactId, "updated_at")).ShouldBe(updatedBefore);
        runner.Inputs.ShouldHaveSingleItem().ShouldBe(ReportBody);
    }

    /// <summary>
    /// D-S125-Q4: the child's own <c>rejected</c> record is this run's verdict, even though the run exited 0 with its
    /// marker. The reason word the profile uses (<c>dtd_prohibited</c> here) becomes the tenant-visible code, and the
    /// write is the same single guarded <c>UPDATE</c> a failed run makes.
    /// </summary>
    [Fact]
    public async Task Parse_Child_Reported_Rejection_Is_The_Verdict()
    {
        await using var world = await StartAsync();
        var seeded = await SeedReadyAsync(world);
        var runner = new StubParseRunner
        {
            Result = new ParseRunSucceeded(
                [
                    Record("""{"t":"xml","e":"start","n":"report","a":[]}"""),
                    Record(
                        $$"""{"t":"rejected","code":"{{ParsingErrorCode.ArtifactInvalid}}","reason":"{{ParsingFailureReason.DtdProhibited}}"}"""),
                ],
                ChildExitCode: 0,
                OutputBytes: 192,
                OutputRecords: 2),
        };

        var versionBefore = await VersionAsync(world, seeded.ArtifactId);

        await InvokeAsync(world, seeded.ArtifactId, runner);

        (await ColumnAsync(world, seeded.ArtifactId, "state")).ShouldBe(ArtifactState.Rejected);
        (await ColumnAsync(world, seeded.ArtifactId, "failure_code")).ShouldBe(ParsingFailureReason.DtdProhibited);
        (await VersionAsync(world, seeded.ArtifactId)).ShouldBe(versionBefore + 1, "one guarded write");
        (await ColumnAsync(world, seeded.ArtifactId, "bytes")).ShouldBe(
            ReportBody.LongLength.ToString(CultureInfo.InvariantCulture),
            "the verified length is kept");
    }

    /// <summary>
    /// 04.1.3-T1 (AC3): the child's <c>cap_exceeded</c> record — the one cap that arm can hit is the report entry
    /// budget — is the artifact's verdict, the handler hands its configured cap to every run, and the refusal is
    /// counted as <c>report_cases</c> so the operator sees which cap fired.
    /// </summary>
    [Fact]
    public async Task Parse_Report_Over_The_Case_Cap_Is_Rejected_And_Counted()
    {
        await using var world = await StartAsync();
        var seeded = await SeedReadyAsync(world);
        var runner = new StubParseRunner
        {
            Result = new ParseRunSucceeded(
                [
                    Record(
                        $$"""{"t":"rejected","code":"{{ParsingErrorCode.ArtifactInvalid}}","reason":"{{ArtifactFailureCode.CapExceeded}}"}"""),
                ],
                ChildExitCode: 0,
                OutputBytes: 96,
                OutputRecords: 1),
        };

        using var listener = IngestTelemetryCapture.Caps(out var caps);

        await InvokeAsync(
            world,
            seeded.ArtifactId,
            runner,
            limits: Options.Create(new IngestionLimits { MaxReportCases = 7 }));

        (await ColumnAsync(world, seeded.ArtifactId, "state")).ShouldBe(ArtifactState.Rejected);
        (await ColumnAsync(world, seeded.ArtifactId, "failure_code")).ShouldBe(ArtifactFailureCode.CapExceeded);
        runner.MaxCases.ShouldBe([7L], "the child runs with the host's configured entry cap");
        caps.ShouldContain("report_cases");
    }

    /// <summary>
    /// The kind's byte cap is the other published cap behind this handler: its <c>too_large</c> refusal is counted as
    /// <c>report_bytes</c>, so a report refused for size is distinguishable from one refused for its entry count.
    /// </summary>
    [Fact]
    public async Task Parse_Object_Over_The_Kind_Cap_Counts_Report_Bytes()
    {
        await using var world = await StartAsync();
        await SeedKindPolicyAsync(world, maxBytes: 8);
        var seeded = await SeedReadyAsync(world);

        using var listener = IngestTelemetryCapture.Caps(out var caps);

        await InvokeAsync(world, seeded.ArtifactId, new StubParseRunner());

        (await ColumnAsync(world, seeded.ArtifactId, "failure_code")).ShouldBe(ArtifactFailureCode.TooLarge);
        caps.ShouldContain("report_bytes");
    }

    /// <summary>
    /// The rejection record's reason is untrusted text: only a value shaped like a code word (<c>^[a-z0-9_]{1,64}$</c>)
    /// may become the tenant-visible code, and every other shape collapses to <c>parse_crashed</c> — uppercase, spaces,
    /// punctuation, an empty string, an over-long value, a missing member and a non-string member all included, so no
    /// text the parsed document influenced reaches the column (00 §5.7 S3).
    /// </summary>
    [Fact]
    public async Task Parse_Child_Rejection_Outside_The_Code_Shape_Is_Parse_Crashed()
    {
        // The shapes a hostile or buggy child could put in `reason`. Each is one record; every one must land on the
        // fixed code, never on the value.
        string[] ways =
        [
            """{"t":"rejected","code":"artifact_invalid","reason":"DTD_PROHIBITED"}""",
            """{"t":"rejected","code":"artifact_invalid","reason":"dtd prohibited"}""",
            """{"t":"rejected","code":"artifact_invalid","reason":"dtd-prohibited"}""",
            """{"t":"rejected","code":"artifact_invalid","reason":""}""",
            $$"""{"t":"rejected","code":"artifact_invalid","reason":"{{new string('a', 65)}}"}""",
            """{"t":"rejected","code":"artifact_invalid"}""",
            """{"t":"rejected","code":"artifact_invalid","reason":42}""",
        ];

        await using var world = await StartAsync();

        foreach (var json in ways)
        {
            var seeded = await SeedReadyAsync(world);
            var runner = new StubParseRunner
            {
                Result = new ParseRunSucceeded([Record(json)], ChildExitCode: 0, OutputBytes: 64, OutputRecords: 1),
            };

            await InvokeAsync(world, seeded.ArtifactId, runner);

            (await ColumnAsync(world, seeded.ArtifactId, "state")).ShouldBe(ArtifactState.Rejected, json);
            (await ColumnAsync(world, seeded.ArtifactId, "failure_code")).ShouldBe(
                ArtifactFailureCode.ParseCrashed,
                $"the reason in {json} must never become the failure code");
        }
    }

    /// <summary>
    /// An object larger than the kind's cap is a verdict, not an infrastructure fault: the capped read refuses to
    /// truncate (a truncated report is a different document), so the artifact is rejected <c>too_large</c> with no
    /// child started and no retry towards a dead letter.
    /// </summary>
    [Fact]
    public async Task Parse_Object_Over_The_Kind_Cap_Is_Rejected_Too_Large()
    {
        await using var world = await StartAsync();

        // The report body is longer than this row's cap, and the resolution takes the lower of the row and the default,
        // so the handler's own read is the one that refuses it.
        await SeedKindPolicyAsync(world, maxBytes: 8);

        var seeded = await SeedReadyAsync(world);
        var runner = new StubParseRunner();
        var versionBefore = await VersionAsync(world, seeded.ArtifactId);

        await InvokeAsync(world, seeded.ArtifactId, runner);

        (await ColumnAsync(world, seeded.ArtifactId, "state")).ShouldBe(ArtifactState.Rejected);
        (await ColumnAsync(world, seeded.ArtifactId, "failure_code")).ShouldBe(ArtifactFailureCode.TooLarge);
        (await VersionAsync(world, seeded.ArtifactId)).ShouldBe(versionBefore + 1, "one guarded write");
        runner.Calls.ShouldBe(0, "an object the cap refuses is never handed to a child");
    }
}
