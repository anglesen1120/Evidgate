using System.Diagnostics;
using System.Text;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Runner;
using Shouldly;
using Xunit;

namespace Parser.Tests;

/// <summary>
/// 06.3.6-T1.3 behaviours 3, 4 and 11(b): the parent's stdout protocol over scripted children. Each case states one
/// framing fact the child contract makes — the marker is last, counted, single and <c>true</c>; every line is one
/// JSON object with a final newline; a blank line, a trailing fragment, a second value, a record after the marker
/// and a second marker are all malformed — plus the two caps and the three pipe-pressure shapes (stdin ignored,
/// stderr flooded, oversized line) that must not deadlock or truncate into a success.
/// </summary>
public sealed class ParseRunnerProtocolTests
{
    /// <summary>Scripts whose stdout is not the protocol; each must end as <see cref="ParseRunFailureKind.MalformedOutput"/>.</summary>
    public static TheoryData<string> MalformedStreams => new()
    {
        // No output at all: no marker.
        string.Empty,

        // A record with no marker.
        """printf '{"a":1}\n'""",

        // A blank line is not a record.
        """printf '{"a":1}\n\n{"done":true,"count":1}\n'""",

        // The last line must be the marker: a missing final newline means the stream was cut.
        """printf '{"done":true,"count":0}'""",

        // A marker is not the last line.
        """printf '{"done":true,"count":0}\n{"a":1}\n'""",

        // Exactly one marker.
        """printf '{"done":true,"count":0}\n{"done":true,"count":0}\n'""",

        // The count must equal the records before the marker.
        """printf '{"a":1}\n{"b":2}\n{"done":true,"count":3}\n'""",

        // A marker needs its count.
        """printf '{"done":true}\n'""",

        // done is the boolean true, never a lookalike.
        """printf '{"done":false,"count":0}\n'""",
        """printf '{"done":"true","count":0}\n'""",
        """printf '{"done":1,"count":0}\n'""",

        // One JSON value per line.
        """printf '[1,2]\n{"done":true,"count":0}\n'""",
        """printf 'not json\n{"done":true,"count":0}\n'""",
        """printf '{"a":1}{"b":2}\n{"done":true,"count":0}\n'""",
        """printf '{"done":true,"count":0} {"a":1}\n'""",

        // A duplicate key inside one object is not a record the writer could produce.
        """printf '{"done":true,"done":true,"count":0}\n'""",

        // A whitespace-only line is not a record either.
        """printf '   \n{"done":true,"count":0}\n'""",

        // Nothing may follow the marker, not even whitespace, and the marker line itself must end with a newline.
        """printf '{"done":true,"count":0}\n \n'""",
        """printf '{"done":true,"count":0}\n   '""",
    };

    [Theory]
    [MemberData(nameof(MalformedStreams))]
    public async Task Malformed_Stdout_Ends_As_A_Malformed_Failure(string script)
    {
        ParseRunnerHarness.RequirePosixShell("a scripted child that writes a fixed stream");
        using var harness = new ParseRunnerHarness();
        using var runner = harness.Script(script);

        var result = await ParseRunnerHarness.RunAsync(runner, ParseRunnerProfile.Xml, 1024 * 1024, [1, 2, 3], cancellationToken: TestContext.Current.CancellationToken);

        // Never a truncated success: the failure kind is the only shape this stream can take.
        result.ShouldBeOfType<ParseRunFailed>().Failure.ShouldBe(ParseRunFailureKind.MalformedOutput);
    }

    [Fact]
    public async Task Record_Counting_A_Nested_Done_And_A_Top_Level_Count_Is_A_Valid_Stream()
    {
        ParseRunnerHarness.RequirePosixShell("a scripted child");
        using var harness = new ParseRunnerHarness();
        using var runner = harness.Script(
            """printf '{"t":"start","n":"a","a":{"done":true}}\n{"count":5}\n{"done":true,"count":2}\n'""");

        var result = await ParseRunnerHarness.RunAsync(runner, ParseRunnerProfile.Xml, 1024 * 1024, [1], cancellationToken: TestContext.Current.CancellationToken);

        var success = result.ShouldBeOfType<ParseRunSucceeded>();

        // A nested done is record content (records are opaque); a top-level count without done is a plain record.
        success.Records.Count.ShouldBe(2);
        Encoding.UTF8.GetString(success.Records[0].Utf8Json.Span).ShouldContain("\"a\":{\"done\":true}");
        Encoding.UTF8.GetString(success.Records[1].Utf8Json.Span).ShouldBe("""{"count":5}""");
    }

    [Fact]
    public async Task Output_Byte_Cap_Ends_As_A_Breach_And_Kills_The_Child_At_Once()
    {
        ParseRunnerHarness.RequirePosixShell("a scripted child that keeps writing");
        using var harness = new ParseRunnerHarness();
        using var runner = harness.Script(
            """
            i=1
            while [ $i -le 200 ]; do
              printf '{"v":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}\n'
              i=$((i+1))
            done
            printf '{"done":true,"count":200}\n'
            """,
            options => options.MaxOutputBytes = 1024);

        var stopwatch = Stopwatch.StartNew();
        var result = await ParseRunnerHarness.RunAsync(runner, ParseRunnerProfile.Xml, 1024 * 1024, [1], cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldBeOfType<ParseRunFailed>().Failure.ShouldBe(ParseRunFailureKind.OutputBytesExceeded);

        // The breach kills the child itself; waiting for the wall clock instead would show the 30 s default here.
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task Record_Cap_Ends_As_A_Breach()
    {
        ParseRunnerHarness.RequirePosixShell("a scripted child");
        using var harness = new ParseRunnerHarness();
        using var runner = harness.Script(
            """
            i=1
            while [ $i -le 10 ]; do
              printf '{"v":%d}\n' "$i"
              i=$((i+1))
            done
            printf '{"done":true,"count":10}\n'
            """,
            options => options.MaxOutputRecords = 5);

        var result = await ParseRunnerHarness.RunAsync(runner, ParseRunnerProfile.Xml, 1024 * 1024, [1], cancellationToken: TestContext.Current.CancellationToken);

        var failure = result.ShouldBeOfType<ParseRunFailed>();
        failure.Failure.ShouldBe(ParseRunFailureKind.OutputRecordsExceeded);
        failure.OutputRecords.ShouldBe(5);
    }

    /// <summary>
    /// Behaviour 3: a single line longer than the per-line cap is a breach, even when the whole stream would fit the
    /// total byte cap — a record is validated whole or not at all.
    /// </summary>
    [Fact]
    public async Task One_Oversized_Record_Line_Ends_As_A_Breach_Not_A_Malformed_Failure()
    {
        ParseRunnerHarness.RequirePosixShell("a scripted child that writes one huge line");
        using var harness = new ParseRunnerHarness();
        using var runner = harness.Script(
            """
            printf '{"v":"'
            /usr/bin/head -c 2097152 /dev/zero | /usr/bin/tr '\0' 'a'
            printf '"}\n{"done":true,"count":1}\n'
            """);

        // A 4 KiB input cap derives a 1 MiB per-line cap, well above 4 x 4096: the 2 MiB line is the line cap's.
        var result = await ParseRunnerHarness.RunAsync(runner, ParseRunnerProfile.Xml, maxBytes: 4096, [1], cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldBeOfType<ParseRunFailed>().Failure.ShouldBe(ParseRunFailureKind.OutputBytesExceeded);
    }

    /// <summary>
    /// Behaviour 2: a child that writes its answer and exits without reading stdin must not deadlock the parent's
    /// 64 MiB write — the write ends in EPIPE and the marker decides.
    /// </summary>
    [Fact]
    public async Task Child_Ignoring_A_SixtyFour_Mebibyte_Stdin_Does_Not_Deadlock()
    {
        ParseRunnerHarness.RequirePosixShell("a scripted child that ignores stdin");
        using var harness = new ParseRunnerHarness();
        using var runner = harness.Script("""printf '{"done":true,"count":0}\n'""");

        var input = new byte[64 * 1024 * 1024];
        var stopwatch = Stopwatch.StartNew();
        var result = await ParseRunnerHarness.RunAsync(runner, ParseRunnerProfile.Xml, 64L * 1024 * 1024, input, cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldBeOfType<ParseRunSucceeded>().Records.ShouldBeEmpty();
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// Behaviour 2: stderr is drained beyond its bounded capture, so a child that floods it never blocks on a full
    /// pipe; the bytes never reach the result (06.3.6-T1.4 owns stderr).
    /// </summary>
    [Fact]
    public async Task Child_Flooding_Stderr_Does_Not_Block_And_Leaves_Nothing_In_The_Result()
    {
        ParseRunnerHarness.RequirePosixShell("a scripted child that floods stderr");
        using var harness = new ParseRunnerHarness();
        using var runner = harness.Script(
            """
            i=1
            while [ $i -le 2000 ]; do
              printf '%01000d' 0 >&2
              i=$((i+1))
            done
            printf '{"done":true,"count":0}\n'
            """);

        var result = await ParseRunnerHarness.RunAsync(runner, ParseRunnerProfile.Xml, 1024 * 1024, [1], cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldBeOfType<ParseRunSucceeded>();
    }
}
