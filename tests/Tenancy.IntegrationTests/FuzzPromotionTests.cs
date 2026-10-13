// 06.3.2-T2.1 (AC4, plan §9 "A seeded crash is promoted into the corpus, not swallowed"): the promotion smoke.
// It drives the real helper against a TEMP COPY of fixtures/hostile/ — never the committed corpus — with one
// synthetic crash, and asserts the two things promotion has to do: a new committed fixture file appears next to
// the corpus, and its manifest row appears (so the 06.3.2-T1 suite asserts it from then on). Promoting the same
// finding again is a no-op — the file name is the finding's content hash — so a replay neither duplicates the row
// nor aborts the run, while bytes under that name that differ from the finding are refused. It also proves the
// helper writes nothing without the explicit opt-in, which is what keeps a CI run read-only.
using System.Text;
using System.Text.Json.Nodes;
using Evidgate.Modules.Evidence.Contracts;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 06.3.2-T2.1 (AC4): <see cref="FuzzPromotion"/> writes a fuzz finding into a corpus as a new fixture file plus
/// one manifest row. The suite owns the corpus copy it writes into, so a failing smoke never touches
/// <c>fixtures/hostile/</c>.
/// </summary>
[Trait("Category", "Fuzz")]
public sealed class FuzzPromotionTests : IDisposable
{
    private readonly string _corpus = Directory.CreateTempSubdirectory("evidgate-fuzz-promote-").FullName;

    /// <summary>Copies the committed corpus (files, manifest and layout note) into the suite's own temp directory.</summary>
    public FuzzPromotionTests()
    {
        foreach (var file in Directory.GetFiles(HostileFixtureCorpus.DirectoryPath))
        {
            File.Copy(file, Path.Combine(_corpus, Path.GetFileName(file)));
        }
    }

    /// <summary>
    /// AC4: promoting one finding adds exactly one file and one row, the row carries the guard, the entry point
    /// and the documented refusal, and the corpus's own count invariant (one row per committed file, plus one per
    /// generated fixture — 06.3.2-T1.2's AC1 check) still holds afterwards.
    /// </summary>
    [Fact]
    public void PromotingAFindingAddsTheFixtureAndOneManifestRow()
    {
        var rowsBefore = ReadRows(_corpus).Count;
        var finding = MarkupCrash();

        var promoted = FuzzPromotion.Promote(finding, _corpus, ParsingFailureReason.Malformed, allowPromotion: true);

        promoted.File.ShouldEndWith(".xml");
        promoted.File.ShouldStartWith("fuzz_");
        promoted.GuardId.ShouldBe(FuzzPromotion.MarkupGuardId);
        promoted.DrivenThrough.ShouldBe(HostileFixture.ParseXmlMode);
        promoted.ExpectedFailureCode.ShouldBe(ParsingFailureReason.Malformed);
        File.Exists(promoted.Path).ShouldBeTrue($"{promoted.File} is written next to the corpus");
        File.ReadAllBytes(promoted.Path).ShouldBe(finding.Bytes, "the promoted file is the finding's own bytes");

        var rows = ReadRows(_corpus);

        rows.Count.ShouldBe(rowsBefore + 1);

        var row = rows[^1]!.AsObject();

        row["file"]!.GetValue<string>().ShouldBe(promoted.File);
        row["guard"]!.GetValue<string>().ShouldStartWith($"{FuzzPromotion.MarkupGuardId} ");
        row["driven_through"]!.GetValue<string>().ShouldBe(HostileFixture.ParseXmlMode);
        row["expected_failure_code"]!.GetValue<string>().ShouldBe(ParsingFailureReason.Malformed);

        // 06.3.2-T1.2's AC1 count check, re-established for the promoted row: a committed fixture is exactly one
        // row, and the two generated fixtures keep their rows.
        rows.Count.ShouldBe(CorpusFiles(_corpus).Count + GeneratedRows(rows));

        // …and the row names a guard and an entry point of the corpus's closed lists, as the T1 suite asserts.
        HostileFixtureCorpus.GuardIds.ShouldContain(promoted.GuardId);
        HostileFixtureCorpus.DriveModes.ShouldContain(promoted.DrivenThrough);
    }

    /// <summary>AC4: an archive finding promotes as a <c>.zip</c> row driven through the archive entry point.</summary>
    [Fact]
    public void PromotingAnArchiveFindingNamesTheArchiveGuard()
    {
        var finding = new FuzzFinding(
            "traversal.zip",
            FuzzTarget.Archive,
            7,
            "InvalidDataException: synthetic crash injected by the smoke test",
            Encoding.UTF8.GetBytes("PK\u0003\u0004fuzz"));

        var promoted = FuzzPromotion.Promote(finding, _corpus, ParsingFailureReason.UnsafeEntryName, allowPromotion: true);

        promoted.File.ShouldEndWith(".zip");
        promoted.GuardId.ShouldBe(FuzzPromotion.ArchiveGuardId);
        promoted.DrivenThrough.ShouldBe(HostileFixture.EnumerateMode);
        File.Exists(promoted.Path).ShouldBeTrue();
    }

    /// <summary>
    /// AC4: without <c>EVIDGATE_FUZZ_PROMOTE=1</c> and the explicit opt-in the helper refuses and writes
    /// nothing — the reason a CI run can never modify the committed corpus.
    /// </summary>
    [Fact]
    public void PromotionIsRefusedWithoutTheExplicitOptIn()
    {
        var rowsBefore = ReadRows(_corpus).Count;
        var filesBefore = CorpusFiles(_corpus).Count;

        var refused = Should.Throw<InvalidOperationException>(
            () => FuzzPromotion.Promote(MarkupCrash(), _corpus, ParsingFailureReason.Malformed, allowPromotion: false));

        refused.Message.ShouldContain(FuzzPromotion.PromoteVariable);
        ReadRows(_corpus).Count.ShouldBe(rowsBefore, "a refused promotion writes no row");
        CorpusFiles(_corpus).Count.ShouldBe(filesBefore, "a refused promotion writes no file");
    }

    /// <summary>AC4: the row must carry a documented reason, so a typo cannot commit an unrunnable corpus row.</summary>
    [Fact]
    public void PromotionRefusesAnUndocumentedFailureCode()
    {
        var rowsBefore = ReadRows(_corpus).Count;

        Should.Throw<InvalidOperationException>(
            () => FuzzPromotion.Promote(MarkupCrash(), _corpus, "not_a_documented_reason", allowPromotion: true));

        ReadRows(_corpus).Count.ShouldBe(rowsBefore);
    }

    /// <summary>
    /// AC4: the file name is the finding's content hash, so promoting the same finding a second time finds its own
    /// bytes already next to the corpus: the helper reports the row as already promoted and changes nothing — no
    /// second file, no second manifest row, and no abort that would cut the run short.
    /// </summary>
    [Fact]
    public void PromotingTheSameFindingTwiceIsANoOp()
    {
        var finding = MarkupCrash();

        var first = FuzzPromotion.Promote(finding, _corpus, ParsingFailureReason.Malformed, allowPromotion: true);
        var rowsAfterFirst = ReadRows(_corpus).Count;
        var filesAfterFirst = CorpusFiles(_corpus).ToList();

        first.AlreadyPromoted.ShouldBeFalse("the first promotion writes the fixture");

        var second = FuzzPromotion.Promote(finding, _corpus, ParsingFailureReason.Malformed, allowPromotion: true);

        second.AlreadyPromoted.ShouldBeTrue("the second promotion finds the identical file already in the corpus");
        second.File.ShouldBe(first.File);
        second.GuardId.ShouldBe(first.GuardId);
        second.Path.ShouldBe(first.Path);

        ReadRows(_corpus).Count.ShouldBe(rowsAfterFirst, "an already-promoted finding appends no second row");
        CorpusFiles(_corpus).ShouldBe(filesAfterFirst, "an already-promoted finding writes no second file");
        ReadRows(_corpus).Count.ShouldBe(CorpusFiles(_corpus).Count + GeneratedRows(ReadRows(_corpus)));
        File.ReadAllBytes(second.Path).ShouldBe(finding.Bytes, "the committed bytes are the finding's own");
    }

    /// <summary>
    /// AC4: bytes under the promoted name that are not the finding's own mean the name no longer identifies the
    /// content, so the helper refuses instead of overwriting a committed fixture and writes nothing.
    /// </summary>
    [Fact]
    public void PromotionRefusesAnExistingFileWithDifferentBytes()
    {
        var finding = MarkupCrash();

        var first = FuzzPromotion.Promote(finding, _corpus, ParsingFailureReason.Malformed, allowPromotion: true);
        var rowsAfterFirst = ReadRows(_corpus).Count;
        var foreign = Encoding.UTF8.GetBytes("not this finding's bytes");

        // The foreign bytes replace the file the first promotion wrote; the file set is captured with them in place,
        // so the refusal below is asserted against the corpus as it now stands.
        File.WriteAllBytes(first.Path, foreign);

        var filesAfterFirst = CorpusFiles(_corpus).ToList();

        var refused = Should.Throw<InvalidOperationException>(
            () => FuzzPromotion.Promote(finding, _corpus, ParsingFailureReason.Malformed, allowPromotion: true));

        refused.Message.ShouldContain(first.File);
        ReadRows(_corpus).Count.ShouldBe(rowsAfterFirst, "a refused promotion appends no row");
        CorpusFiles(_corpus).ShouldBe(filesAfterFirst, "a refused promotion writes no file");
        File.ReadAllBytes(first.Path).ShouldBe(foreign, "the existing bytes are left alone");
    }

    /// <summary>Deletes the suite's temp corpus; a leftover temp tree never fails the run.</summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(_corpus, recursive: true);
        }
        catch (IOException)
        {
            // Already gone, or held open by another handle.
        }
        catch (UnauthorizedAccessException)
        {
            // A temp tree this process may not remove is not a corpus failure.
        }
    }

    /// <summary>The synthetic crash the smoke injects: shaped exactly like the run's own finding.</summary>
    private static FuzzFinding MarkupCrash() => new(
        "xxe.xml",
        FuzzTarget.XmlReport,
        1234,
        "FormatException: synthetic crash injected by the smoke test",
        Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><suite><case/></suite>"));

    /// <summary>The corpus manifest rows of a directory, as JSON nodes.</summary>
    private static JsonArray ReadRows(string corpus) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(corpus, HostileFixtureCorpus.ManifestFileName)))!.AsArray();

    /// <summary>The committed fixture file names of a directory (everything but the manifest and the layout note).</summary>
    private static IReadOnlyList<string> CorpusFiles(string corpus) =>
    [
        .. Directory
            .GetFiles(corpus)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(name => name is not HostileFixtureCorpus.ManifestFileName and not HostileFixtureCorpus.ReadmeFileName)
            .OrderBy(name => name, StringComparer.Ordinal),
    ];

    /// <summary>How many rows the manifest marks generated (their files are built at test time, not committed).</summary>
    private static int GeneratedRows(JsonArray rows) =>
        rows.Count(row => row!.AsObject()["generated"]?.GetValue<bool>() == true);
}
