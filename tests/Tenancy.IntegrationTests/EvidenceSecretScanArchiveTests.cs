// The archive arm of the masking scan: a trace or report bundle is a deflated zip, so the compressed bytes a byte
// scan sees hold no readable prefix of any entry and always answer clean. These cases prove the arm scans both views
// of the object — the container's own headers (entry names, the archive comment, a stored entry) and each entry
// decompressed through the parser profile — sums the counts per class, and refuses a bundle it cannot read instead of
// answering clean. Every archive is generated in memory and the planted key is assembled at run time, so this file
// carries no credential-shaped literal. No container, no database: the profile and the scan are pure.
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Evidgate.Modules.Evidence.Application;
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Evidence.Domain;
using Evidgate.Modules.Evidence.Infrastructure.Parsing;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// <see cref="ArtifactSecretScan"/> over the two shapes a stored object has: a document scanned in place and an
/// archive whose entries are scanned after the profile decompresses them.
/// </summary>
public sealed class EvidenceSecretScanArchiveTests
{
    /// <summary>The kind cap these cases pass as the object's cap and as the archive's expanded budget.</summary>
    private const long MaxBytes = 1024 * 1024;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A well-formed AWS access-key id, assembled so no literal in this file reads as a credential.</summary>
    private static string PlantedKey => string.Concat("AK", "IA", new string('Q', 16));

    private static ParsingService Parsing => new();

    /// <summary>
    /// A secret inside a deflated entry is reported: the compressed object alone is clean (the blind spot this arm
    /// closes), and the archive's entries are what the scan classifies.
    /// </summary>
    [Fact]
    public void Scan_Zip_With_Secret_In_A_Deflated_Entry_Is_Flagged()
    {
        var zipped = Zip(("report.xml", Encoding.UTF8.GetBytes("<report> " + PlantedKey + " </report>")));

        SecretPatternScanner.Scan(zipped, Token).ShouldBeEmpty();

        var hit = ArtifactSecretScan
            .Scan(Parsing, "application/zip", zipped, MaxBytes, Token)
            .ShouldHaveSingleItem();

        hit.PatternClass.ShouldBe(SecretPatternClass.AwsAccessKey);
        hit.HitCount.ShouldBe(1);
    }

    /// <summary>
    /// A secret in an entry's own name is flagged: the name lives in the container's headers, never in the decompressed
    /// entry, so scanning entries alone answers clean about a bundle that names a key. The name is written twice — in
    /// the local header and in the central directory — and the count is the sum over the container's bytes.
    /// </summary>
    [Fact]
    public void Scan_Zip_With_A_Secret_In_An_Entry_Name_Is_Flagged()
    {
        var zipped = Zip(("logs/key-" + PlantedKey + ".xml", Encoding.UTF8.GetBytes("<report/>")));

        var hit = ArtifactSecretScan
            .Scan(Parsing, "application/zip", zipped, MaxBytes, Token)
            .ShouldHaveSingleItem();

        hit.PatternClass.ShouldBe(SecretPatternClass.AwsAccessKey);
        hit.HitCount.ShouldBe(2);
    }

    /// <summary>
    /// A secret only in the archive's own comment is flagged: the comment sits in the end record, outside every entry,
    /// so no amount of decompression reaches it. The positive control is the same bundle whose comment is clean.
    /// </summary>
    [Fact]
    public void Scan_Zip_With_A_Secret_Only_In_The_Archive_Comment_Is_Flagged()
    {
        var entries = new[] { ("run/report.xml", Encoding.UTF8.GetBytes("<report/>")) };

        var hit = ArtifactSecretScan
            .Scan(Parsing, "application/zip", ZipWithComment("built from key-" + PlantedKey, entries), MaxBytes, Token)
            .ShouldHaveSingleItem();

        hit.PatternClass.ShouldBe(SecretPatternClass.AwsAccessKey);
        hit.HitCount.ShouldBe(1);

        ArtifactSecretScan
            .Scan(Parsing, "application/zip", ZipWithComment("built from a clean tree", entries), MaxBytes, Token)
            .ShouldBeEmpty();
    }

    /// <summary>
    /// A key that ends exactly at the end of an entry's name, or starts exactly at the start of the archive comment,
    /// is flanked by the format's own header bytes. Those are identifier bytes, so the boundary rule that protects a
    /// text scan must not veto the container view, or the key is reported clean.
    /// </summary>
    [Fact]
    public void Scan_Zip_With_A_Key_Flush_Against_The_Header_Bytes_Is_Flagged()
    {
        var entries = new[] { (PlantedKey, Encoding.UTF8.GetBytes("K<report/>")) };

        ArtifactSecretScan
            .Scan(Parsing, "application/zip", ZipWithComment("clean", entries), MaxBytes, Token)
            .ShouldHaveSingleItem()
            .HitCount.ShouldBe(2);

        ArtifactSecretScan
            .Scan(Parsing, "application/zip", ZipWithComment(PlantedKey, new[] { ("run/report.xml", Encoding.UTF8.GetBytes("<report/>")) }), MaxBytes, Token)
            .ShouldHaveSingleItem()
            .HitCount.ShouldBe(1);
    }

    /// <summary>
    /// The two views are summed per class, so one class still reports one row: a key in an entry's name and in the
    /// entry's content is one row whose count is the total over both views, not a second class or a second row.
    /// </summary>
    [Fact]
    public void Scan_Zip_Sums_The_Container_And_The_Entry_Content_Per_Class()
    {
        var zipped = Zip(
            ("logs/key-" + PlantedKey + ".xml", Encoding.UTF8.GetBytes("<report>" + PlantedKey + "</report>")));

        var hit = ArtifactSecretScan
            .Scan(Parsing, "application/zip", zipped, MaxBytes, Token)
            .ShouldHaveSingleItem();

        hit.PatternClass.ShouldBe(SecretPatternClass.AwsAccessKey);
        hit.HitCount.ShouldBe(3);
    }

    /// <summary>
    /// A bundle inside the bundle is refused rather than answered clean: the profile hands back the nested archive's
    /// own compressed bytes, so a key deflated inside it never reaches the scanner, and a clean verdict would claim a
    /// classification the pass never performed. The nest is recognized by content, not by name.
    /// </summary>
    [Fact]
    public void Scan_Zip_Holding_A_Nested_Archive_Throws_Instead_Of_Answering_Clean()
    {
        var inner = Zip(("report.xml", Encoding.UTF8.GetBytes("<report> " + PlantedKey + " </report>")));

        // The nested file is named like a document, so only its magic can tell the scan what it holds.
        var outer = Zip(("run/report.xml", inner));

        var thrown = Should.Throw<InvalidOperationException>(
            () => ArtifactSecretScan.Scan(Parsing, "application/zip", outer, MaxBytes, Token));

        thrown.Message.ShouldContain("nested archive");
    }

    /// <summary>
    /// The counts of every entry are summed per class, so one class reports one row with the total.
    /// </summary>
    [Fact]
    public void Scan_Zip_Sums_The_Counts_Of_Every_Entry()
    {
        var zipped = Zip(
            ("run/trace.json", Encoding.UTF8.GetBytes("[{\"header\":\"" + PlantedKey + "\"}]")),
            ("run/report.xml", Encoding.UTF8.GetBytes("<report>" + PlantedKey + " " + PlantedKey + "</report>")));

        var hit = ArtifactSecretScan
            .Scan(Parsing, "application/zip", zipped, MaxBytes, Token)
            .ShouldHaveSingleItem();

        hit.PatternClass.ShouldBe(SecretPatternClass.AwsAccessKey);
        hit.HitCount.ShouldBe(3);
    }

    /// <summary>A bundle without a match is clean, so the arm never answers flagged for an archive as such.</summary>
    [Fact]
    public void Scan_Zip_Without_A_Secret_Is_Clean()
    {
        var zipped = Zip(
            ("run/trace.json", Encoding.UTF8.GetBytes("[{\"step\":\"launch\",\"state\":\"done\"}]")),
            ("run/report.xml", Encoding.UTF8.GetBytes("<report><title>done</title></report>")));

        ArtifactSecretScan.Scan(Parsing, "application/zip", zipped, MaxBytes, Token).ShouldBeEmpty();
    }

    /// <summary>
    /// A declared archive the profile cannot read is thrown, never answered clean: nothing classified those bytes, so
    /// the caller's queue must retry and finally dead-letter while the artifact stays pending.
    /// </summary>
    [Fact]
    public void Scan_Unreadable_Archive_Throws_Instead_Of_Answering_Clean()
    {
        var notAnArchive = Encoding.UTF8.GetBytes("<report>" + PlantedKey + "</report>");

        var thrown = Should.Throw<InvalidOperationException>(
            () => ArtifactSecretScan.Scan(Parsing, "application/zip", notAnArchive, MaxBytes, Token));

        // The failure names the profile's own reason word and nothing from the bytes.
        thrown.Message.ShouldContain(ParsingFailureReason.Malformed);
        thrown.Message.ShouldNotContain(PlantedKey);
    }

    /// <summary>
    /// The expanded budget is the object's own cap: two entries that each fit the per-entry bound but together expand
    /// past it are refused rather than scanned part-way, because a partial scan is not a scan.
    /// </summary>
    [Fact]
    public void Scan_Archive_Over_The_Expanded_Budget_Throws()
    {
        const long budget = 4096;

        // Hex text of a random body compresses about 2:1, so both entries together weigh less than the budget while
        // their content expands past it — the case a per-entry bound alone would wave through.
        var hex = Encoding.UTF8.GetBytes(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(1250)));
        var zipped = Zip(("run/first.json", hex), ("run/second.json", hex));

        zipped.Length.ShouldBeLessThan((int)budget);

        var thrown = Should.Throw<InvalidOperationException>(
            () => ArtifactSecretScan.Scan(Parsing, "application/zip", zipped, budget, Token));

        thrown.Message.ShouldContain(ParsingFailureReason.SizeExceeded);
    }

    /// <summary>
    /// A decompression bomb is refused by the profile's own ratio cap before its content is kept, so a bundle built to
    /// expand far past what it weighs never reaches the scanner and never answers clean.
    /// </summary>
    [Fact]
    public void Scan_Zip_Bomb_Shaped_Archive_Throws()
    {
        var zipped = Zip(("run/zeros.bin", new byte[1024 * 1024]));

        var thrown = Should.Throw<InvalidOperationException>(
            () => ArtifactSecretScan.Scan(Parsing, "application/zip", zipped, MaxBytes, Token));

        thrown.Message.ShouldContain(ParsingFailureReason.CompressionRatioExceeded);
    }

    /// <summary>
    /// A cancelled attempt stops in the archive arm too, so a shutdown or a lost lease does not keep decompressing.
    /// </summary>
    [Fact]
    public void Scan_Archive_Honours_Cancellation()
    {
        var zipped = Zip(("run/report.xml", Encoding.UTF8.GetBytes("<report> " + PlantedKey + " </report>")));

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Should.Throw<OperationCanceledException>(
            () => ArtifactSecretScan.Scan(Parsing, "application/zip", zipped, MaxBytes, cancelled.Token));
    }

    /// <summary>
    /// The archive rule is the declared type's, with its parameters and letter case ignored — the same comparison the
    /// completion applies — so a document is never decompressed and a bundle is never scanned in place.
    /// </summary>
    [Fact]
    public void Scan_Only_A_Zip_Declared_Type_Takes_The_Archive_Arm()
    {
        ArtifactSecretScan.IsArchive("application/zip").ShouldBeTrue();
        ArtifactSecretScan.IsArchive("APPLICATION/ZIP").ShouldBeTrue();
        ArtifactSecretScan.IsArchive("application/zip; charset=binary").ShouldBeTrue();
        ArtifactSecretScan.IsArchive("application/xml").ShouldBeFalse();
        ArtifactSecretScan.IsArchive("application/zip-compressed").ShouldBeFalse();
        ArtifactSecretScan.IsArchive(null).ShouldBeFalse();
    }

    /// <summary>An object that is not declared an archive is scanned in place, and its secret is still reported.</summary>
    [Fact]
    public void Scan_Document_Media_Type_Is_Scanned_In_Place()
    {
        var document = Encoding.UTF8.GetBytes("<report> " + PlantedKey + " </report>");

        ArtifactSecretScan.Scan(Parsing, "application/xml", document, MaxBytes, Token)
            .ShouldHaveSingleItem()
            .PatternClass.ShouldBe(SecretPatternClass.AwsAccessKey);
    }

    /// <summary>
    /// Builds a deflated archive in memory: the compression level the producers use, so every entry is a real deflate
    /// stream rather than a stored one.
    /// </summary>
    /// <param name="entries">The entry name and content of each part, in archive order.</param>
    /// <returns>The archive's bytes.</returns>
    private static byte[] Zip(params (string Name, byte[] Content)[] entries) => Zip(null, entries);

    /// <summary>
    /// The same bundle with the archive's own comment in the end record: text the container carries outside every
    /// entry.
    /// </summary>
    /// <param name="comment">The comment to store.</param>
    /// <param name="entries">The entry name and content of each part, in archive order.</param>
    /// <returns>The archive's bytes.</returns>
    private static byte[] ZipWithComment(string comment, params (string Name, byte[] Content)[] entries) =>
        Zip(comment, entries);

    /// <summary>The one archive builder both helpers above forward to.</summary>
    /// <param name="comment">The archive's comment, or null for none.</param>
    /// <param name="entries">The entry name and content of each part, in archive order.</param>
    /// <returns>The archive's bytes.</returns>
    private static byte[] Zip(string? comment, (string Name, byte[] Content)[] entries)
    {
        using var buffer = new MemoryStream();

        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            archive.Comment = comment ?? string.Empty;

            foreach (var (name, content) in entries)
            {
                using var entry = archive.CreateEntry(name, CompressionLevel.SmallestSize).Open();
                entry.Write(content);
            }
        }

        return buffer.ToArray();
    }
}
