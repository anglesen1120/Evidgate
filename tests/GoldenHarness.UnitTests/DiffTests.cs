using Evidgate.GoldenHarness;
using Shouldly;
using Xunit;

namespace GoldenHarness.UnitTests;

/// <summary>
/// The comparison core: events pair by <c>(producer_id, sequence)</c>, so a reordered file is
/// still equal, and every unexplained difference names the event index and the member the
/// fixture owner has to change.
/// </summary>
public sealed class DiffTests
{
    [Fact]
    public void Diff_Equal_ReturnsEmpty()
    {
        var expected = Doc(2, Event(0), Event(1));
        var observed = Doc(2, Event(0), Event(1));

        GoldenDiff.Compare(expected, observed).ShouldBeEmpty();
    }

    [Fact]
    public void Diff_ReorderedEvents_ReturnsEmpty()
    {
        var expected = Doc(2, Event(0), Event(1, rawStatus: "failed", normalisedStatus: "failed"));
        var observed = Doc(2, Event(1, rawStatus: "failed", normalisedStatus: "failed"), Event(0));

        // Arrival order is not part of the contract: the two sequences pair by identity.
        GoldenDiff.Compare(expected, observed).ShouldBeEmpty();
    }

    [Fact]
    public void Diff_CountMismatch_NamesDeclaredCount()
    {
        var expected = Doc(2, Event(0));
        var observed = Doc(1, Event(0));

        var diff = GoldenDiff.Compare(expected, observed).ShouldHaveSingleItem();

        diff.EventIndex.ShouldBeNull();
        diff.Field.ShouldBe("declared_count");
        diff.Reason.ShouldBe(DiffReasons.CountMismatch);
        diff.Expected.ShouldBe("2");
        diff.Observed.ShouldBe("1");
    }

    [Fact]
    public void Diff_ReportStatusMismatch_NamesRawReportStatus()
    {
        var expected = new ExpectedDoc("malformed", 0, []);
        var observed = new ExpectedDoc("parsed", 0, []);

        var diff = GoldenDiff.Compare(expected, observed).ShouldHaveSingleItem();

        diff.EventIndex.ShouldBeNull();
        diff.Field.ShouldBe("raw_report_status");
        diff.Reason.ShouldBe(DiffReasons.StatusMismatch);
        diff.Expected.ShouldBe("malformed");
        diff.Observed.ShouldBe("parsed");
    }

    [Fact]
    public void Diff_UnmappedCase_NamesCaseKey()
    {
        var expected = Doc(1, Event(0, caseKey: "tests/test_a.py::test_a"));
        var observed = Doc(1, Event(0, caseKey: "tests/test_b.py::test_b"));

        var diff = GoldenDiff.Compare(expected, observed).ShouldHaveSingleItem();

        diff.EventIndex.ShouldBe(0);
        diff.Field.ShouldBe("case_key");
        diff.Reason.ShouldBe(DiffReasons.UnmappedCase);
        diff.Expected.ShouldBe("tests/test_a.py::test_a");
        diff.Observed.ShouldBe("tests/test_b.py::test_b");
    }

    [Fact]
    public void Diff_StatusMismatch_NamesBothStatusMembers()
    {
        var expected = Doc(1, Event(0));
        var observed = Doc(1, Event(0, rawStatus: "error", normalisedStatus: "failed"));

        var diffs = GoldenDiff.Compare(expected, observed);

        diffs.Select(diff => diff.Field).ShouldBe(["normalised_status", "raw_status"]);
        diffs.Select(diff => diff.Reason).ShouldBe([DiffReasons.StatusMismatch, DiffReasons.StatusMismatch]);
        diffs.Select(diff => diff.EventIndex).ShouldBe([0, 0]);
    }

    [Fact]
    public void Diff_ExtraEvent_IsReported()
    {
        var expected = Doc(2, Event(0));
        var observed = Doc(2, Event(0), Event(1));

        var diff = GoldenDiff.Compare(expected, observed).ShouldHaveSingleItem();

        diff.EventIndex.ShouldBe(1);
        diff.Field.ShouldBe("event");
        diff.Reason.ShouldBe(DiffReasons.ExtraEvent);
        diff.Expected.ShouldBeNull();
        diff.Observed.ShouldBe("junit-pytest#1 tests/test_a.py::test_a passed->passed");
    }

    [Fact]
    public void Diff_MissingEvent_IsReported()
    {
        var expected = Doc(2, Event(0), Event(1));
        var observed = Doc(2, Event(0));

        var diff = GoldenDiff.Compare(expected, observed).ShouldHaveSingleItem();

        diff.EventIndex.ShouldBe(1);
        diff.Field.ShouldBe("event");
        diff.Reason.ShouldBe(DiffReasons.MissingEvent);
        diff.Expected.ShouldBe("junit-pytest#1 tests/test_a.py::test_a passed->passed");
        diff.Observed.ShouldBeNull();
    }

    [Fact]
    public void Diff_RenumberedSequence_IsAFieldMismatch()
    {
        var expected = Doc(1, Event(1));
        var observed = Doc(1, Event(2));

        var diff = GoldenDiff.Compare(expected, observed).ShouldHaveSingleItem();

        diff.EventIndex.ShouldBe(0);
        diff.Field.ShouldBe("sequence");
        diff.Reason.ShouldBe(DiffReasons.FieldMismatch);
        diff.Expected.ShouldBe("1");
        diff.Observed.ShouldBe("2");
    }

    [Fact]
    public void Diff_EventIndex_IsTheSortedExpectedPosition()
    {
        var expected = Doc(2, Event(1, rawStatus: "failed"), Event(0));
        var observed = Doc(2, Event(0), Event(1));

        var diff = GoldenDiff.Compare(expected, observed).ShouldHaveSingleItem();

        // Sequence 1 is the first event in the file but the second of the two sorted events;
        // the index a reader looks up is the sorted one, which is what the runner prints.
        diff.EventIndex.ShouldBe(1);
        diff.Field.ShouldBe("raw_status");
        diff.Reason.ShouldBe(DiffReasons.StatusMismatch);
    }

    private static ExpectedDoc Doc(int declaredCount, params NormalisedEvent[] events) =>
        new("parsed", declaredCount, events);

    private static NormalisedEvent Event(
        long sequence,
        string caseKey = "tests/test_a.py::test_a",
        string rawStatus = "passed",
        string normalisedStatus = "passed") =>
        new("junit-pytest", sequence, caseKey, rawStatus, normalisedStatus);
}
