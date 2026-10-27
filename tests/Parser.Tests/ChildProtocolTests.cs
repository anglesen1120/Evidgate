using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Parser.Tests;

/// <summary>
/// 06.3.6-T1.2 (06.3.6 §7, §9): the <c>Evidgate.Parser</c> child run as a real process, asserting what crosses its
/// pipes. The subject is the protocol, not the profile: the profile itself is proven in-process by 06.3.1/06.3.2, so
/// these tests take the committed corpus and assert the framing (one JSON object per line, one final marker whose
/// count matches), determinism (the same bytes produce the same bytes), the injection boundary (an artifact string
/// can never end a line or forge the marker), the two rejection families and the exit-code contract.
/// </summary>
public sealed class ChildProtocolTests
{
    private const string JunitFixture = "reports/junit/sample/input.xml";

    /// <summary>A generous cap: the corpus files are kilobytes, and the cap is what the test varies, not the size.</summary>
    private const string OneMebibyte = "1048576";

    /// <summary>
    /// The largest cap a 64-bit <see cref="long"/> can name. The child clamps it to the profile's own ceiling, so
    /// the run stays bounded instead of buffering toward the heap limit.
    /// </summary>
    private const string LongMax = "9223372036854775807";

    /// <summary>Every argument vector the contract does not accept.</summary>
    public static TheoryData<string[]> UsageErrors
    {
        get
        {
            var data = new TheoryData<string[]>();

            foreach (var arguments in RefusedArguments)
            {
                data.Add(arguments);
            }

            return data;
        }
    }

    /// <summary>
    /// The refused vectors, held once: a usage error is any missing, duplicated, unknown or unparsable argument,
    /// and the assertion is the same for all of them (exit 2, nothing on stdout, the fixed usage line on stderr).
    /// </summary>
    private static readonly string[][] RefusedArguments =
    [
        [],
        ["--profile", "xml"],
        ["--max-bytes", "1024"],
        ["--profile", "yaml", "--max-bytes", "1024"],
        ["--profile", "xml", "--max-bytes", "zero"],
        ["--profile", "xml", "--max-bytes", "0"],
        ["--profile", "xml", "--max-bytes", "-1"],
        ["--profile", "xml", "--profile", "json", "--max-bytes", "1024"],
        ["--profile", "xml", "--max-bytes", "1024", "--media-type", "text/plain", "--media-type", "text/xml"],
        ["--profile", "xml", "--max-bytes", "1024", "--media-type", "--media-type", "text/xml"],
        ["--profile", "xml", "--max-bytes", "1024", "--media-type", "--unknown"],
        ["--max-bytes", "1024", "--profile", "xml", "--unknown", "value"],
        ["--profile", "xml", "--max-bytes"],
        ["xml", "--profile", "xml", "--max-bytes", "1024"],
    ];

    [Fact]
    public async Task Xml_Report_Streams_The_Document_And_The_Final_Marker()
    {
        var content = await ReadFixtureAsync(JunitFixture);

        var run = await ChildRunner.RunAsync(content, "--profile", "xml", "--max-bytes", OneMebibyte);

        run.ExitCode.ShouldBe(0);
        run.Stderr.ShouldBeEmpty();

        var records = AssertFramed(run);

        // The declaration and the whitespace between nodes travel as text records, so the root is the first start
        // record rather than the first record.
        var root = records.First(record => record.Event == "start");
        root.Kind.ShouldBe("xml");
        root.Name.ShouldBe("testsuites");

        // Every start has its end, empty elements included: the stream rebuilds the document.
        records.Count(record => record.Event == "start")
            .ShouldBe(records.Count(record => record.Event == "end"));

        records.ShouldContain(record => record.Event == "start" && record.Name == "testcase");
        records.ShouldContain(
            record => record.Event == "text" && record.Value == "Sample.Tests.Case_Fails()");

        // The attribute of a start record survives as a name/value pair, in document order.
        root.Attributes.ShouldContain(
            attribute => attribute.Key == "name" && attribute.Value == "sample");
    }

    [Fact]
    public async Task Xml_Output_Is_Byte_Identical_Across_Two_Runs()
    {
        var content = await ReadFixtureAsync(JunitFixture);

        var first = await ChildRunner.RunAsync(content, "--profile", "xml", "--max-bytes", OneMebibyte);
        var second = await ChildRunner.RunAsync(content, "--profile", "xml", "--max-bytes", OneMebibyte);

        first.ExitCode.ShouldBe(0);
        second.ExitCode.ShouldBe(0);

        // No timestamp, no run id, no hash-order dependency: the protocol is a function of the bytes and the caps.
        second.StdoutBytes.ShouldBe(first.StdoutBytes);
    }

    [Fact]
    public async Task Xml_Text_And_Attribute_Content_Cannot_End_A_Line_Or_Forge_The_Marker()
    {
        // The text node carries the marker's own JSON, a real newline, the U+2028 line separator and a tab; the
        // attribute carries it too. If any of them reached the wire unescaped, the line count and the marker would
        // stop agreeing — which is exactly what a consumer treats as a crash.
        var content = Encoding.UTF8.GetBytes(
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <root marker="x{&quot;done&quot;:true,&quot;count&quot;:0}">
              <t>{"done":true,"count":0}&#10;next&#8232;line&#9;end</t>
            </root>
            """);

        var run = await ChildRunner.RunAsync(content, "--profile", "xml", "--max-bytes", OneMebibyte);

        run.ExitCode.ShouldBe(0);
        run.Stderr.ShouldBeEmpty();

        var records = AssertFramed(run);

        records.ShouldContain(
            record => record.Event == "text"
                && record.Value == "{\"done\":true,\"count\":0}\nnext\u2028line\tend");

        var root = records.First(record => record.Event == "start" && record.Name == "root");
        root.Attributes.ShouldContain(
            attribute => attribute.Key == "marker" && attribute.Value == "x{\"done\":true,\"count\":0}");

        // The separator left as a JSON escape, never as a character a splitter could honour.
        run.Stdout.ShouldContain("\\u2028");
        run.Stdout.ShouldNotContain("\u2028");
        run.Stdout.ShouldNotContain("\r");
    }

    [Fact]
    public async Task Json_Payload_Is_One_Record_With_The_Document_Root()
    {
        var content = Encoding.UTF8.GetBytes(
            "{\"note\":\"{\\\"done\\\":true,\\\"count\\\":0}\",\"separator\":\"\u2028\",\"items\":[1,2,3]}");

        var run = await ChildRunner.RunAsync(content, "--profile", "json", "--max-bytes", OneMebibyte);

        run.ExitCode.ShouldBe(0);
        run.Stderr.ShouldBeEmpty();

        var records = AssertFramed(run);

        records.Count.ShouldBe(1);
        records[0].Kind.ShouldBe("json");

        using var document = JsonDocument.Parse(run.Lines[0]);
        var root = document.RootElement.GetProperty("v");
        root.GetProperty("note").GetString().ShouldBe("{\"done\":true,\"count\":0}");
        root.GetProperty("separator").GetString().ShouldBe("\u2028");
        root.GetProperty("items").GetArrayLength().ShouldBe(3);

        run.Stdout.ShouldNotContain("\u2028");
    }

    [Theory]
    [InlineData("xxe.xml", "dtd_prohibited")]
    [InlineData("billion_laughs.xml", "dtd_prohibited")]
    [InlineData("utf16_dtd.xml", "malformed")]
    public async Task Hostile_Xml_Is_Rejected_With_The_Manifest_Reason(string file, string reason)
    {
        var content = await ReadFixtureAsync($"hostile/{file}");

        var run = await ChildRunner.RunAsync(content, "--profile", "xml", "--max-bytes", OneMebibyte);

        run.ExitCode.ShouldBe(0);
        run.Stderr.ShouldBeEmpty();

        var records = AssertFramed(run);

        records.Count.ShouldBe(1);
        records[0].Kind.ShouldBe("rejected");

        using var document = JsonDocument.Parse(run.Lines[0]);
        document.RootElement.GetProperty("code").GetString().ShouldBe("artifact_invalid");
        document.RootElement.GetProperty("reason").GetString().ShouldBe(reason);

        // A rejection never echoes the artifact: no prologue, no entity, no file name.
        run.Stdout.ShouldNotContain("DOCTYPE");
        run.Stdout.ShouldNotContain("ENTITY");
        run.Stdout.ShouldNotContain(file);
    }

    [Theory]
    [InlineData("traversal.zip", "unsafe_entry_name")]
    [InlineData("truncated_trace.zip", "malformed")]
    public async Task Hostile_Archive_Is_Rejected_With_The_Manifest_Reason(string file, string reason)
    {
        var content = await ReadFixtureAsync($"hostile/{file}");

        var run = await ChildRunner.RunAsync(content, "--profile", "archive", "--max-bytes", OneMebibyte);

        run.ExitCode.ShouldBe(0);
        run.Stderr.ShouldBeEmpty();

        var records = AssertFramed(run);

        records.Count.ShouldBe(1);
        records[0].Kind.ShouldBe("rejected");

        using var document = JsonDocument.Parse(run.Lines[0]);
        document.RootElement.GetProperty("code").GetString().ShouldBe("artifact_invalid");
        document.RootElement.GetProperty("reason").GetString().ShouldBe(reason);

        // The refused entry name is never reported (00 §5.7 S3).
        run.Stdout.ShouldNotContain("etc/passwd");
    }

    [Fact]
    public async Task Well_Formed_Archive_Emits_One_Record_Per_Entry()
    {
        var content = await ReadFixtureAsync("hostile/zip_long_comment.zip");

        var run = await ChildRunner.RunAsync(content, "--profile", "archive", "--max-bytes", OneMebibyte);

        run.ExitCode.ShouldBe(0);
        run.Stderr.ShouldBeEmpty();

        var records = AssertFramed(run);

        records.ShouldNotBeEmpty();

        foreach (var line in run.Lines.Take(run.Lines.Count - 1))
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            root.GetProperty("t").GetString().ShouldBe("entry");
            root.GetProperty("n").GetString().ShouldNotBeNullOrEmpty();
            root.GetProperty("c").GetInt64().ShouldBeGreaterThanOrEqualTo(0);
            root.GetProperty("x").GetInt64().ShouldBeGreaterThanOrEqualTo(0);
            root.GetProperty("d").ValueKind.ShouldBeOneOf(JsonValueKind.True, JsonValueKind.False);
        }
    }

    [Fact]
    public async Task Input_Over_The_Cap_Is_A_Size_Rejection_And_The_Cap_Itself_Is_Accepted()
    {
        var content = await ReadFixtureAsync(JunitFixture);

        // One byte under the object's size: the child reads the cap and one byte past it, so this is an overflow
        // rather than a truncated parse.
        var overCap = await ChildRunner.RunAsync(
            content,
            "--profile",
            "xml",
            "--max-bytes",
            (content.Length - 1).ToString(CultureInfo.InvariantCulture));

        overCap.ExitCode.ShouldBe(0);
        overCap.Stderr.ShouldBeEmpty();

        var records = AssertFramed(overCap);
        records.Count.ShouldBe(1);
        records[0].Kind.ShouldBe("rejected");

        using (var document = JsonDocument.Parse(overCap.Lines[0]))
        {
            document.RootElement.GetProperty("code").GetString().ShouldBe("payload_too_large");
            document.RootElement.GetProperty("reason").GetString().ShouldBe("size_exceeded");
        }

        // The boundary is exact: the same object at exactly the cap is parsed.
        var atCap = await ChildRunner.RunAsync(
            content,
            "--profile",
            "xml",
            "--max-bytes",
            content.Length.ToString(CultureInfo.InvariantCulture));

        atCap.ExitCode.ShouldBe(0);
        AssertFramed(atCap).ShouldContain(record => record.Kind == "xml");
    }

    [Fact]
    public async Task Declared_Media_Type_Mismatch_Is_Rejected_Before_The_Parse()
    {
        var content = await ReadFixtureAsync(JunitFixture);

        var mismatch = await ChildRunner.RunAsync(
            content,
            "--profile",
            "xml",
            "--max-bytes",
            OneMebibyte,
            "--media-type",
            "text/plain");

        mismatch.ExitCode.ShouldBe(0);
        mismatch.Stderr.ShouldBeEmpty();

        var records = AssertFramed(mismatch);
        records.Count.ShouldBe(1);
        records[0].Kind.ShouldBe("rejected");

        using (var document = JsonDocument.Parse(mismatch.Lines[0]))
        {
            document.RootElement.GetProperty("code").GetString().ShouldBe("artifact_invalid");
            document.RootElement.GetProperty("reason").GetString().ShouldBe("media_type_mismatch");
        }

        // The same object with the type it really is parses: the sniff is the only difference between the runs.
        var match = await ChildRunner.RunAsync(
            content,
            "--profile",
            "xml",
            "--max-bytes",
            OneMebibyte,
            "--media-type",
            "application/xml");

        match.ExitCode.ShouldBe(0);
        AssertFramed(match).ShouldContain(record => record.Event == "start" && record.Name == "testsuites");
    }

    [Fact]
    public async Task Empty_Input_Is_Malformed_Not_A_Crash()
    {
        var run = await ChildRunner.RunAsync([], "--profile", "xml", "--max-bytes", OneMebibyte);

        run.ExitCode.ShouldBe(0);
        run.Stderr.ShouldBeEmpty();

        var records = AssertFramed(run);
        records.Count.ShouldBe(1);
        records[0].Kind.ShouldBe("rejected");

        using var document = JsonDocument.Parse(run.Lines[0]);
        document.RootElement.GetProperty("code").GetString().ShouldBe("artifact_invalid");
        document.RootElement.GetProperty("reason").GetString().ShouldBe("malformed");

        // The marker counts the rejection: a `rejected` record is a record, so an empty object still yields
        // `{"done":true,"count":1}` and the consumer's line count agrees with it.
        using var marker = JsonDocument.Parse(run.Lines[^1]);
        marker.RootElement.GetProperty("count").GetInt32().ShouldBe(1);
        run.Lines.Count.ShouldBe(2);
    }

    [Theory]
    [MemberData(nameof(UsageErrors))]
    public async Task Usage_Errors_Exit_2_With_Nothing_On_Stdout(string[] arguments)
    {
        var run = await ChildRunner.RunAsync(Encoding.UTF8.GetBytes("<root/>"), arguments);

        // Stable facts only: the child refused the vector, said so on stderr and streamed no record. The exact
        // sentence is not pinned here (one test holds it steady), so a wording fix does not fail twelve vectors.
        run.ExitCode.ShouldBe(2);
        run.Stdout.ShouldBeEmpty();
        run.Stderr.ShouldNotBeNullOrWhiteSpace();
        run.Stderr.ShouldStartWith("evidgate-parser: ");
        run.Stderr.TrimEnd('\n').ShouldNotContain("\n");
    }

    [Fact]
    public async Task Every_Refused_Vector_Prints_The_Same_One_Line()
    {
        var lines = new List<string>();

        foreach (var arguments in RefusedArguments)
        {
            var run = await ChildRunner.RunAsync([], arguments);

            run.ExitCode.ShouldBe(2);
            run.Stdout.ShouldBeEmpty();
            lines.Add(run.Stderr);
        }

        // One fixed sentence whatever was refused, naming the contract rather than the value: the child never says
        // which argument it disliked, so a hostile vector cannot make it echo a value back.
        lines.Distinct(StringComparer.Ordinal).Count().ShouldBe(1);
        lines[0].ShouldContain("--profile");
        lines[0].ShouldContain("--max-bytes");
        lines[0].ShouldNotContain("yaml");
        lines[0].ShouldNotContain("zero");
    }

    /// <summary>
    /// A JSON string holding a lone UTF-16 surrogate is valid JSON (the escape is legal) and survives the
    /// profile's strict UTF-8 check, but a .NET string cannot hold it: the writer refuses it. The child must turn
    /// that into a rejection decided before any byte of the record reaches stdout, not into an unhandled
    /// exception (exit 3 with no marker, which the parent can only read as a crash).
    /// </summary>
    /// <param name="payload">The JSON text whose string carries the unpaired surrogate.</param>
    [Theory]
    [InlineData("\"\\ud800\"")]
    [InlineData("[\"\\udc00\"]")]
    public async Task Lone_Utf16_Surrogate_Is_A_Malformed_Rejection(string payload)
    {
        var run = await ChildRunner.RunAsync(
            Encoding.UTF8.GetBytes(payload),
            "--profile",
            "json",
            "--max-bytes",
            OneMebibyte);

        run.ExitCode.ShouldBe(0);
        run.Stderr.ShouldBeEmpty();

        var records = AssertFramed(run);
        records.Count.ShouldBe(1);
        records[0].Kind.ShouldBe("rejected");

        using var document = JsonDocument.Parse(run.Lines[0]);
        document.RootElement.GetProperty("code").GetString().ShouldBe("artifact_invalid");
        document.RootElement.GetProperty("reason").GetString().ShouldBe("malformed");

        // The abandoned record left nothing behind: no payload fragment precedes the rejection line.
        run.Stdout.ShouldNotContain("ud800");
        run.Stdout.ShouldNotContain("udc00");
    }

    /// <summary>
    /// The strings a JSON payload can carry and the writer can represent: a valid surrogate pair stays one
    /// character, and U+0000 stays the JSON escape rather than a raw NUL byte on the wire.
    /// </summary>
    /// <param name="payload">The JSON text.</param>
    /// <param name="expected">The value the record must carry.</param>
    /// <param name="escape">The escape the writer must have used.</param>
    [Theory]
    [InlineData("\"\\ud83d\\ude00\"", "\U0001F600", "\\uD83D\\uDE00")]
    [InlineData("\"\\u0000\"", "\0", "\\u0000")]
    public async Task Representable_Json_Strings_Are_Emitted(string payload, string expected, string escape)
    {
        var run = await ChildRunner.RunAsync(
            Encoding.UTF8.GetBytes(payload),
            "--profile",
            "json",
            "--max-bytes",
            OneMebibyte);

        run.ExitCode.ShouldBe(0);
        run.Stderr.ShouldBeEmpty();

        var records = AssertFramed(run);
        records.Count.ShouldBe(1);
        records[0].Kind.ShouldBe("json");

        using var document = JsonDocument.Parse(run.Lines[0]);
        document.RootElement.GetProperty("v").GetString().ShouldBe(expected);

        run.Stdout.ShouldContain(escape);

        // A raw NUL never travels: one byte of it would end the line for a C consumer.
        run.StdoutBytes.ShouldNotContain((byte)0);
    }

    /// <summary>
    /// The parent's write shape must not change the child's answer: a pipe that delivers seven bytes per flush is
    /// the same artifact as one that delivers the file in a single write.
    /// </summary>
    [Fact]
    public async Task Stdin_Written_In_Tiny_Blocks_Produces_Identical_Output()
    {
        var content = await ReadFixtureAsync(JunitFixture);

        var oneBlock = await ChildRunner.RunAsync(content, "--profile", "xml", "--max-bytes", OneMebibyte);
        var manyBlocks = await ChildRunner.RunFragmentedAsync(
            7,
            content,
            "--profile",
            "xml",
            "--max-bytes",
            OneMebibyte);

        oneBlock.ExitCode.ShouldBe(0);
        manyBlocks.ExitCode.ShouldBe(0);
        manyBlocks.Stderr.ShouldBeEmpty();

        manyBlocks.StdoutBytes.ShouldBe(oneBlock.StdoutBytes);
    }

    /// <summary>
    /// The record's <c>n</c> is the qualified name as the document spells it, exactly like the attributes, so a
    /// prefixed element keeps its prefix instead of being reported as a prefix-less local name.
    /// </summary>
    [Fact]
    public async Task Prefixed_Element_Keeps_Its_Prefix_In_The_Record()
    {
        var content = Encoding.UTF8.GetBytes(
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <ns:root xmlns:ns="urn:evg:report" ns:kind="sample">
              <ns:child/>
            </ns:root>
            """);

        var run = await ChildRunner.RunAsync(content, "--profile", "xml", "--max-bytes", OneMebibyte);

        run.ExitCode.ShouldBe(0);
        run.Stderr.ShouldBeEmpty();

        var records = AssertFramed(run);

        var root = records.First(record => record.Event == "start" && record.Name == "ns:root");
        root.Attributes.ShouldContain(attribute => attribute.Key == "ns:kind" && attribute.Value == "sample");
        root.Attributes.ShouldContain(
            attribute => attribute.Key == "xmlns:ns" && attribute.Value == "urn:evg:report");

        records.ShouldContain(record => record.Event == "start" && record.Name == "ns:child");

        // No record carries the prefix-less form: a consumer never has to guess which field dropped it.
        records.ShouldNotContain(record => record.Event == "start" && record.Name == "root");
        records.ShouldNotContain(record => record.Event == "start" && record.Name == "child");
    }

    /// <summary>Every shape of JSON rejection the profile decides, at a 1 KiB cap.</summary>
    public static TheoryData<string, string, string> JsonRejections =>
        new()
        {
            { "artifact_invalid", "malformed", "{\"a\":" },
            { "artifact_invalid", "depth_exceeded", NestedArrays(70) },
            { "payload_too_large", "size_exceeded", "\"" + new string('a', 4096) + "\"" },
        };

    /// <summary>A payload of <paramref name="depth"/> nested empty arrays, which is one over the profile's cap.</summary>
    /// <param name="depth">The nesting depth.</param>
    /// <returns>The JSON text.</returns>
    private static string NestedArrays(int depth) => new string('[', depth) + new string(']', depth);

    [Theory]
    [MemberData(nameof(JsonRejections))]
    public async Task Json_Rejections_Carry_The_Profile_Reason(string code, string reason, string payload)
    {
        var run = await ChildRunner.RunAsync(
            Encoding.UTF8.GetBytes(payload),
            "--profile",
            "json",
            "--max-bytes",
            "1024");

        run.ExitCode.ShouldBe(0);
        run.Stderr.ShouldBeEmpty();

        var records = AssertFramed(run);
        records.Count.ShouldBe(1);
        records[0].Kind.ShouldBe("rejected");

        using var document = JsonDocument.Parse(run.Lines[0]);
        document.RootElement.GetProperty("code").GetString().ShouldBe(code);
        document.RootElement.GetProperty("reason").GetString().ShouldBe(reason);

        // A rejection never echoes the payload.
        run.Stdout.ShouldNotContain("aaaa");
    }

    [Fact]
    public async Task Declared_Media_Type_Mismatch_Is_Rejected_For_Json_And_Archive()
    {
        var json = Encoding.UTF8.GetBytes("{\"a\":1}");

        var jsonMismatch = await ChildRunner.RunAsync(
            json,
            "--profile",
            "json",
            "--max-bytes",
            OneMebibyte,
            "--media-type",
            "text/html");

        AssertMismatch(jsonMismatch);

        // The same bytes with the type they really are: the sniff is the only difference between the two runs.
        var jsonMatch = await ChildRunner.RunAsync(
            json,
            "--profile",
            "json",
            "--max-bytes",
            OneMebibyte,
            "--media-type",
            "application/json");

        jsonMatch.ExitCode.ShouldBe(0);
        AssertFramed(jsonMatch).ShouldContain(record => record.Kind == "json");

        var archive = await ReadFixtureAsync("hostile/zip_long_comment.zip");

        var archiveMismatch = await ChildRunner.RunAsync(
            archive,
            "--profile",
            "archive",
            "--max-bytes",
            OneMebibyte,
            "--media-type",
            "application/json");

        AssertMismatch(archiveMismatch);

        var archiveMatch = await ChildRunner.RunAsync(
            archive,
            "--profile",
            "archive",
            "--max-bytes",
            OneMebibyte,
            "--media-type",
            "application/zip");

        archiveMatch.ExitCode.ShouldBe(0);
        AssertFramed(archiveMatch).ShouldContain(record => record.Kind == "entry");
    }

    /// <summary>
    /// A cap no allocation could satisfy is clamped to the profile's ceiling instead of being honoured: a small
    /// object still parses, and the run never starts buffering toward the heap limit.
    /// </summary>
    [Fact]
    public async Task An_Enormous_Byte_Cap_Is_Clamped_And_A_Small_Object_Is_Accepted()
    {
        var run = await ChildRunner.RunAsync(
            Encoding.UTF8.GetBytes("{\"a\":1}"),
            "--profile",
            "json",
            "--max-bytes",
            LongMax);

        run.ExitCode.ShouldBe(0);
        run.Stderr.ShouldBeEmpty();
        AssertFramed(run).ShouldContain(record => record.Kind == "json");
    }

    /// <summary>
    /// A parent that stops reading closes the pipe, and the child must never report success for bytes nobody
    /// received: the write fails, no marker is written and the run ends as exit 3 — the only answer the parent
    /// maps to <c>parse_crashed</c> rather than to a truncated success.
    /// </summary>
    [Fact]
    public async Task A_Closed_Stdout_Ends_The_Run_With_Exit_3_And_No_Marker()
    {
        // Far more records than a pipe buffer holds, so the close lands while the child is still writing.
        var content = Encoding.UTF8.GetBytes($"<r>{string.Concat(Enumerable.Repeat("<i/>", 60_000))}</r>");
        var startInfo = ChildRunner.CreateStartInfo("--profile", "xml", "--max-bytes", OneMebibyte);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("could not start the parser child process");

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));

            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);

            await process.StandardInput.BaseStream.WriteAsync(content, timeout.Token);
            await process.StandardInput.BaseStream.FlushAsync(timeout.Token);
            process.StandardInput.Close();

            var firstLine = await process.StandardOutput.ReadLineAsync(timeout.Token);
            firstLine.ShouldNotBeNull();

            // The read end goes away while the child still has records to write: the next flush finds the pipe.
            process.StandardOutput.Close();

            await process.WaitForExitAsync(timeout.Token);
            var stderr = await stderrTask;

            process.ExitCode.ShouldBe(3);
            stderr.ShouldNotBeNullOrWhiteSpace();

            // Nothing that was actually received claims the stream completed.
            firstLine.ShouldNotContain("\"done\"");
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // The child exited between the check and the kill.
            }
        }
    }

    /// <summary>Reads one corpus file from the repository; the test's own cancellation token is passed along.</summary>
    /// <param name="relativePath">A path under <c>fixtures/</c>.</param>
    /// <returns>The file's bytes.</returns>
    private static Task<byte[]> ReadFixtureAsync(string relativePath) =>
        File.ReadAllBytesAsync(RepoPaths.Fixtures(relativePath), TestContext.Current.CancellationToken);

    /// <summary>
    /// Asserts the framing the protocol promises and returns the decoded records (the marker excluded): stdout ends
    /// with a newline, the marker is the last line exactly once, its <c>count</c> equals the number of records
    /// before it, and no other record carries a <c>done</c> key.
    /// </summary>
    /// <param name="run">The finished child run.</param>
    /// <returns>The records before the marker, decoded.</returns>
    private static List<ProtocolRecord> AssertFramed(ChildRun run)
    {
        run.Stdout.ShouldEndWith("\n");

        // A blank line between records would be a framing violation the line list cannot show (it drops empty
        // lines), so it is asserted on the raw text.
        run.Stdout.TrimEnd('\n').ShouldNotContain("\n\n");

        var lines = run.Lines;
        lines.ShouldNotBeEmpty();

        using (var marker = JsonDocument.Parse(lines[^1]))
        {
            marker.RootElement.GetProperty("done").GetBoolean().ShouldBeTrue();
            marker.RootElement.GetProperty("count").GetInt32().ShouldBe(lines.Count - 1);
        }

        var records = new List<ProtocolRecord>(lines.Count - 1);

        foreach (var line in lines.Take(lines.Count - 1))
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            root.TryGetProperty("done", out _).ShouldBeFalse();
            root.EnumerateObject().Count().ShouldBeGreaterThanOrEqualTo(2);

            records.Add(new ProtocolRecord(
                root.GetProperty("t").GetString()!,
                root.TryGetProperty("e", out var kind) ? kind.GetString() : null,
                root.TryGetProperty("n", out var name) ? name.GetString() : null,
                root.TryGetProperty("v", out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null,
                Attributes(root)));
        }

        return records;
    }

    /// <summary>Asserts the one rejection a declared-media-type mismatch produces, and nothing else.</summary>
    /// <param name="run">The finished child run.</param>
    private static void AssertMismatch(ChildRun run)
    {
        run.ExitCode.ShouldBe(0);
        run.Stderr.ShouldBeEmpty();

        var records = AssertFramed(run);
        records.Count.ShouldBe(1);
        records[0].Kind.ShouldBe("rejected");

        using var document = JsonDocument.Parse(run.Lines[0]);
        document.RootElement.GetProperty("code").GetString().ShouldBe("artifact_invalid");
        document.RootElement.GetProperty("reason").GetString().ShouldBe("media_type_mismatch");
    }

    private static List<KeyValuePair<string, string>> Attributes(JsonElement root)
    {
        var attributes = new List<KeyValuePair<string, string>>();

        if (!root.TryGetProperty("a", out var array))
        {
            return attributes;
        }

        foreach (var attribute in array.EnumerateArray())
        {
            attributes.Add(new KeyValuePair<string, string>(
                attribute[0].GetString()!,
                attribute[1].GetString()!));
        }

        return attributes;
    }

    /// <summary>One decoded record, reduced to the fields these tests assert on.</summary>
    private sealed record ProtocolRecord(
        string Kind,
        string? Event,
        string? Name,
        string? Value,
        IReadOnlyList<KeyValuePair<string, string>> Attributes);
}
