using System.Globalization;
using Evidgate.GoldenHarness;
using Shouldly;
using Xunit;

namespace GoldenHarness.UnitTests;

/// <summary>
/// The strict outcomes of the console runner: a declared malformed case, the one exception that
/// counts as a refusal, an unexpected parser exception, and output that must stay one row per
/// verdict whatever the fixture or parser writes into it.
/// </summary>
public sealed partial class RunnerTests
{
    [Fact]
    public void Run_Declared_Malformed_Case_Passes_When_The_Parser_Refuses_The_Report()
    {
        CopySeededCase("junit-pytest", "truncated");

        var registry = new ParserRegistry().Register(
            "junit-pytest",
            _ => throw new ParseRefusedException("the document ends inside an attribute"));

        var (exitCode, output) = Run(registry, "--fixtures", _root);

        exitCode.ShouldBe(0);
        output.ShouldContain("junit-pytest/truncated");
        output.ShouldContain("PASS");
        output.ShouldContain("1 passed");
        output.ShouldContain("0 failed");
    }

    [Fact]
    public void Run_Malformed_Expectation_With_Events_Is_Refused_At_Load()
    {
        CreateCase(
            "claims-events",
            ExpectedJson("malformed", 1, Event(1, "case::one", "failed", "failed")));

        var (exitCode, output) = Run("--fixtures", _root);

        exitCode.ShouldBe(1);
        output.ShouldContain("temp-producer/claims-events");
        output.ShouldContain("ERROR");
        output.ShouldContain("raw_report_status");
        output.ShouldContain("1 error");
    }

    [Fact]
    public void Run_Returned_Malformed_Doc_With_Events_Still_Diffs()
    {
        CreateCase("returns-malformed", ExpectedJson("parsed", 1, Event(1, "case::one", "passed", "passed")));

        var registry = new ParserRegistry().Register(
            ParserId,
            _ => ProducedDoc("malformed", ("case::one", 1, "passed", "passed")));

        // A parser that returns a malformed document instead of refusing is not a refusal at
        // all, so the mismatch is still compared field by field.
        var (exitCode, output) = Run(registry, "--fixtures", _root);

        exitCode.ShouldBe(1);
        output.ShouldContain("temp-producer/returns-malformed");
        output.ShouldContain("FAIL");
        output.ShouldContain("raw_report_status");
        output.ShouldContain("1 failed");
    }

    [Fact]
    public void Run_Parser_Refusal_On_A_Parsed_Case_Fails_With_Malformed()
    {
        CopySeededCase("junit-pytest", "happy");

        var registry = new ParserRegistry().Register(
            "junit-pytest",
            _ => throw new ParseRefusedException("the document ends inside an attribute"));

        var (exitCode, output) = Run(registry, "--fixtures", _root);

        exitCode.ShouldBe(1);
        output.ShouldContain("junit-pytest/happy");
        output.ShouldContain("FAIL");
        output.ShouldContain("malformed");
        output.ShouldContain("raw_report_status");
        output.ShouldContain("1 failed");
    }

    [Fact]
    public void Run_Unexpected_Parser_Exception_On_A_Declared_Malformed_Case_Is_An_Error()
    {
        CopySeededCase("junit-pytest", "truncated");

        var registry = new ParserRegistry().Register(
            "junit-pytest",
            // A mapper bug, modelled as reading a slot it never filled: the runtime raises the
            // null reference, so the test does not construct a reserved exception type itself.
            _ => new ExpectedDoc(new string[1].First().Length.ToString(CultureInfo.InvariantCulture), 0, []));

        // Only a declared refusal is the malformed outcome; a mapper bug must not pass simply
        // because the expectation already expected a broken report.
        var (exitCode, output) = Run(registry, "--fixtures", _root);

        exitCode.ShouldBe(1);
        output.ShouldContain("junit-pytest/truncated");
        output.ShouldContain("ERROR");
        output.ShouldContain("NullReferenceException");
        output.ShouldContain("1 error");
        output.ShouldContain("0 passed");
    }

    [Fact]
    public void Run_Control_Characters_In_A_Parser_Id_Cannot_Add_Output_Rows()
    {
        CreateCase(
            "escape-id",
            ExpectedJson("parsed", 0),
            parserId: "ghost\nparser\u001b[31m",
            parserStatus: null);

        var (exitCode, output) = Run("--fixtures", _root);

        exitCode.ShouldBe(1);
        output.ShouldContain("\\u000A");
        output.ShouldContain("\\u001B");
        output.ShouldNotContain("\u001b");
        // Header, one verdict row and the summary: the embedded newline adds no row.
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length.ShouldBe(3);
    }

    [Fact]
    public void Run_Control_Characters_In_Diff_Values_Cannot_Add_Output_Rows()
    {
        CreateCase(
            "case\nwith\u001bcontrols",
            ExpectedJson("parsed", 1, Event(1, "case::one", "passed", "passed")));

        var registry = Registry(ProducedDoc(("case::one\nINJECTED\u001b", 1, "passed", "passed")));

        var (exitCode, output) = Run(registry, "--fixtures", _root);

        exitCode.ShouldBe(1);
        output.ShouldContain("\\u000A");
        output.ShouldContain("\\u001B");
        output.ShouldNotContain("\u001b");
        // Header, the verdict row, the diff row and the summary: neither embedded newline
        // adds a row the reader would take for a verdict.
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length.ShouldBe(4);
    }

    [Fact]
    public void Run_Unregistered_Parser_Without_Pending_Is_Refused_By_Name()
    {
        CreateCase("orphan", ExpectedJson("parsed", 0), parserId: "ghost-parser", parserStatus: null);

        var (exitCode, output) = Run("--fixtures", _root);

        exitCode.ShouldBe(1);
        output.ShouldContain("temp-producer/orphan");
        output.ShouldContain("ghost-parser");
        output.ShouldContain("ERROR");
        output.ShouldContain("1 error");
    }

    [Fact]
    public void Run_Missing_Expected_File_Is_A_Named_Error()
    {
        CreateCase("broken", expectedJson: null);

        var (exitCode, output) = Run("--fixtures", _root);

        exitCode.ShouldBe(1);
        output.ShouldContain("temp-producer/broken");
        output.ShouldContain("ERROR");
    }

    [Fact]
    public void Run_Without_Fixtures_Is_A_Usage_Error()
    {
        var (exitCode, output) = Run();

        exitCode.ShouldBe(2);
        output.ShouldContain("--fixtures");
        output.ShouldContain("usage:");
    }

    [Fact]
    public void Run_Empty_Corpus_Exits_Zero()
    {
        var (exitCode, output) = Run("--fixtures", _root);

        exitCode.ShouldBe(0);
        output.ShouldContain("0 passed");
        output.ShouldContain("0 pending");
        output.ShouldContain("0 error");
    }
}
