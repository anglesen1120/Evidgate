using System.IO.Compression;
using System.Text;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Evidence.Infrastructure.Parsing;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 13.1.1-T5.2 (13.1.1 AC16): the caps-explicit archive read <see cref="IParsing.ReadArchiveEntries"/> — the one
/// the authoring XLSX reader goes through, because a workbook's parts are separate entries of one zip and the
/// reader needs their content, not just their names. Every archive is generated in memory through the same two
/// builders <see cref="SafeZipArchiveTests"/> uses (a deflate one, and a stored one that writes names verbatim so a
/// hostile name reaches the profile exactly as intended), and every assertion is about a <c>Result</c> value: no
/// exception may escape the profile and no failure may carry a name or an entry byte (00 §5.7 S3).
/// <para>
/// The caps are the caller's — the profile's own defaults are <see cref="ParsingLimits"/>, and the last case proves
/// they did not move — so each guard is proven with tiny numbers. Where the AC16 number itself matters (the
/// 20:1 compression ratio) the test uses that number: a real deflate stream cannot be built to a byte-exact ratio,
/// because the profile measures the compressed bytes it consumed, the local header included.
/// </para>
/// </summary>
public sealed class SafeZipArchiveEntryReadTests
{
    /// <summary>The kind cap these tests bound the archive object against.</summary>
    private const long MaxBytes = 8 * 1024 * 1024;

    /// <summary>One byte of content, for the archives whose entry count is what the test is about (CA1861).</summary>
    private static readonly byte[] One = Encoding.UTF8.GetBytes("x");

    /// <summary>The normalized names the two-part archive exposes, in archive order (CA1861).</summary>
    private static readonly string[] TwoPartNames =
    [
        "[Content_Types].xml",
        "xl/workbook.xml",
    ];

    [Fact]
    public void Entries_AtCap_Accepted_OverCap_Refused()
    {
        // AC16's own number: 1 000 entries accepted, one more refused. The cap is the caller's (the wire archive
        // of this test holds a few kilobytes), and the profile's 10 000 must never decide for it — the last case of
        // this class proves the default path still accepts the same archive.
        var accepted = Read(CreateZip(Entries(1000)), Caps(maxEntries: 1000), _ => true);

        accepted.IsSuccess.ShouldBeTrue(accepted.Error?.Message);
        accepted.Value.Entries.Count.ShouldBe(1000);

        var refused = Read(CreateZip(Entries(1001)), Caps(maxEntries: 1000), _ => true);

        refused.IsSuccess.ShouldBeFalse();
        refused.Error!.Code.ShouldBe(ParsingErrorCode.ArtifactInvalid);
        refused.Error.Status.ShouldBe(422);
        ReasonOf(refused).ShouldBe(ParsingFailureReason.EntryCountExceeded);
    }

    [Fact]
    public void Expanded_AtCap_Accepted_OverCap_Refused()
    {
        // AC16: the cumulative expanded size is the caller's 64 MiB in XLSX; here exactly the cap is accepted and
        // one byte further is the 413 the profile maps the size cap to.
        var atCap = CreateZip(("sharedStrings.xml", new byte[64]));
        var overCap = CreateZip(("sharedStrings.xml", new byte[65]));

        var accepted = Read(atCap, Caps(maxExpandedBytes: 64), _ => true);

        accepted.IsSuccess.ShouldBeTrue(accepted.Error?.Message);
        accepted.Value.Entries.ShouldHaveSingleItem().ExpandedBytes.ShouldBe(64);

        var refused = Read(overCap, Caps(maxExpandedBytes: 64), _ => true);

        refused.IsSuccess.ShouldBeFalse();
        refused.Error!.Code.ShouldBe(ParsingErrorCode.PayloadTooLarge);
        refused.Error.Status.ShouldBe(413);
        ReasonOf(refused).ShouldBe(ParsingFailureReason.SizeExceeded);
    }

    [Fact]
    public void Ratio_AtCap_Accepted_OverCap_Refused()
    {
        // AC16: XLSX states 20:1. An entry that is inside it is accepted at that cap, and the same archive is
        // accepted when the caller's cap is wider — so the refusal below is the ratio and nothing else.
        var incompressible = CreateZip(("workbook.xml", Incompressible(4_000)));

        Read(incompressible, Caps(maxRatio: 20), _ => true)
            .IsSuccess.ShouldBeTrue("content deflate cannot squeeze is inside 20:1");

        var bomb = CreateZip(("sharedStrings.xml", new byte[100_000]));

        var refused = Read(bomb, Caps(maxRatio: 20), _ => true);

        refused.IsSuccess.ShouldBeFalse();
        refused.Error!.Code.ShouldBe(ParsingErrorCode.ArtifactInvalid);
        ReasonOf(refused).ShouldBe(ParsingFailureReason.CompressionRatioExceeded);

        Read(bomb, Caps(maxRatio: 5_000), _ => true)
            .IsSuccess.ShouldBeTrue("the same bytes are inside a wider ratio, so the cap is what refused them");
    }

    [Fact]
    public void SelectedEntry_BytesReturned_UnselectedNot()
    {
        // Every entry's metadata is reported, only the selected entries carry content, and a lookup answers false
        // for an entry that was not selected or that the archive does not hold — never an exception.
        var archive = CreateZip(
            ("[Content_Types].xml", Encoding.UTF8.GetBytes("<Types/>")),
            ("xl/workbook.xml", Encoding.UTF8.GetBytes("<workbook/>")));

        var result = Read(archive, Caps(), name => name == "xl/workbook.xml");

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        result.Value.Entries.Select(entry => entry.Name).ShouldBe(TwoPartNames);

        // Content is kept for exactly one of the two entries, and it is the one the predicate selected: every
        // entry is reported, one entry is retrievable.
        var withContent = result.Value.Entries
            .Where(entry => result.Value.TryGetEntry(entry.Name, out _))
            .Select(entry => entry.Name)
            .ToArray();

        withContent.ShouldHaveSingleItem().ShouldBe("xl/workbook.xml");

        result.Value.TryGetEntry("xl/workbook.xml", out var kept).ShouldBeTrue();
        Encoding.UTF8.GetString(kept.Span).ShouldBe("<workbook/>");

        // The profile's duplicate rule is case-insensitive, so the lookup is too: one archive cannot hold two
        // entries a spelling could confuse (06.3.1 AC4). Compared as a sequence: ShouldBe on two byte arrays would
        // bind to the equality overload and compare references.
        result.Value.TryGetEntry("XL/Workbook.XML", out var sameEntry).ShouldBeTrue();
        sameEntry.ToArray().SequenceEqual(kept.ToArray()).ShouldBeTrue("the case-variant name resolves to the same bytes");

        result.Value.TryGetEntry("[Content_Types].xml", out var unselected).ShouldBeFalse();
        unselected.IsEmpty.ShouldBeTrue();

        result.Value.TryGetEntry("xl/styles.xml", out _).ShouldBeFalse();
        result.Value.TryGetEntry(string.Empty, out _).ShouldBeFalse();

        // The reverse direction: a predicate that selects nothing still reports every entry's metadata and keeps
        // no content at all. The predicate gates content only, never the directory the caller reads.
        var nothingSelected = Read(archive, Caps(), _ => false);

        nothingSelected.IsSuccess.ShouldBeTrue(nothingSelected.Error?.Message);
        nothingSelected.Value.Entries.Select(entry => entry.Name).ShouldBe(TwoPartNames);

        // A local, not an expression-tree lambda: an `out` discard cannot live in a Shouldly predicate.
        var noContentKept = nothingSelected.Value.Entries
            .All(entry => !nothingSelected.Value.TryGetEntry(entry.Name, out _));

        noContentKept.ShouldBeTrue("a predicate that selects nothing keeps no content");
    }

    [Fact]
    public void SelectedEntry_OverMaxEntryBytes_RefusedBeforeAllocation()
    {
        // 13.1.1 AC16: one XLSX part may not expand past the caller's per-entry cap. 2 MiB of zeros compress to a
        // couple of kB, so the archive stays tiny while an implementation that buffered the entry and only then
        // refused would allocate 2 MiB - twice the 1 MiB bound the measurement sets, so the bound really does
        // separate "refused before allocating" from "refused after". The ratio cap is stated wide on purpose:
        // this case is about the per-entry cap and nothing else.
        const int EntryBytes = 2 * 1024 * 1024;
        var archive = CreateZip(("sharedStrings.xml", new byte[EntryBytes]));

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

        var refused = Read(archive, Caps(maxRatio: 5_000), _ => true, maxEntryBytes: 1024);

        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        refused.IsSuccess.ShouldBeFalse();
        refused.Error!.Code.ShouldBe(ParsingErrorCode.PayloadTooLarge);
        refused.Error.Status.ShouldBe(413);
        ReasonOf(refused).ShouldBe(ParsingFailureReason.SizeExceeded);
        allocated.ShouldBeLessThan(1024 * 1024, "an entry over the per-entry cap must not be buffered");

        // The same bytes are inside the cap when the caller allows them, so the cap is the only cause.
        var accepted = Read(archive, Caps(maxRatio: 5_000), _ => true, maxEntryBytes: EntryBytes);

        accepted.IsSuccess.ShouldBeTrue(accepted.Error?.Message);
        accepted.Value.TryGetEntry("sharedStrings.xml", out var content).ShouldBeTrue();
        content.Length.ShouldBe(EntryBytes);
    }

    [Fact]
    public void PathTraversalName_Refused()
    {
        // AC4/00 §5.7 S3: the name is stored verbatim in a raw archive, so the traversal really reaches the
        // profile — which refuses it before any selected content is read.
        var archive = RawZip(("xl/../../evil.xml", Encoding.UTF8.GetBytes("<x/>")));

        var result = Read(archive, Caps(), _ => true);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ParsingErrorCode.ArtifactInvalid);
        ReasonOf(result).ShouldBe(ParsingFailureReason.UnsafeEntryName);

        // 00 §5.7 S3: the refusal names the reason, never the entry.
        result.Error.Message.ShouldNotContain("evil");
    }

    [Fact]
    public void DuplicateNormalizedName_Refused()
    {
        // AC4: two entries that resolve to the same path could shadow each other, whichever one a lookup would
        // find, so the archive is refused before the selection is asked anything.
        var archive = RawZip(
            ("xl/workbook.xml", Encoding.UTF8.GetBytes("1")),
            ("XL/Workbook.XML", Encoding.UTF8.GetBytes("2")));

        var result = Read(archive, Caps(), _ => true);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ParsingErrorCode.ArtifactInvalid);
        ReasonOf(result).ShouldBe(ParsingFailureReason.DuplicateEntry);
    }

    [Fact]
    public void NotAZip_Malformed()
    {
        var result = Read(Encoding.UTF8.GetBytes("this is not a zip file"), Caps(), _ => true);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ParsingErrorCode.ArtifactInvalid);
        result.Error.Status.ShouldBe(422);
        ReasonOf(result).ShouldBe(ParsingFailureReason.Malformed);
    }

    [Fact]
    public void EmptyArchive_Ok()
    {
        // A valid archive holding no entries is not a malformed one: it is read, reports nothing, and keeps
        // nothing — the caller decides what a missing part means.
        var result = Read(CreateZip(), Caps(), _ => true);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        result.Value.Entries.ShouldBeEmpty();
        result.Value.TryGetEntry("[Content_Types].xml", out var missing).ShouldBeFalse();
        missing.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void DefaultCallers_Unchanged()
    {
        // 13.1.1-T5 regression guard: the caps-explicit member must not move the profile's own defaults, so an
        // archive with more than 1 000 entries — over the XLSX number, inside ParsingLimits.MaxZipEntries — still
        // passes EnumerateArchive untouched, and only the caps-explicit read refuses it when the caller says 1 000.
        var archive = CreateZip(Entries(1001));

        var enumerated = new ParsingService().EnumerateArchive(
            new MemoryStream(archive), MaxBytes, CancellationToken.None);

        enumerated.IsSuccess.ShouldBeTrue(enumerated.Error?.Message);
        enumerated.Value.Count.ShouldBe(1001);

        var refused = Read(archive, Caps(maxEntries: 1000), _ => true);

        refused.IsSuccess.ShouldBeFalse();
        ReasonOf(refused).ShouldBe(ParsingFailureReason.EntryCountExceeded);
    }

    /// <summary>
    /// <paramref name="count"/> one-byte entries named <c>entry-N.txt</c>, for the archives whose entry count is
    /// what the test is about.
    /// </summary>
    private static (string Name, byte[] Content)[] Entries(int count) =>
        Enumerable.Range(0, count)
            .Select(index => ($"entry-{index}.txt", One))
            .ToArray();

    /// <summary>
    /// The caller-stated caps: the same three numbers the XLSX reader states for AC16, scaled down per test so
    /// every guard is proven with a tiny input. Nesting and the timeout are not here — they stay the profile's.
    /// </summary>
    private static ArchiveCaps Caps(
        int maxEntries = 1000,
        long maxExpandedBytes = 4L * 1024 * 1024,
        int maxRatio = 1000) =>
        new(maxEntries, maxExpandedBytes, maxRatio);

    /// <summary>The generic reason a failure carries in its <c>details</c> (00 §5.7 S3).</summary>
    private static string? ReasonOf(Result result) =>
        result.Error?.Details?.SingleOrDefault()?.Reason;

    /// <summary>
    /// Runs the caps-explicit read through the module's service adapter, which is the surface the XLSX reader
    /// resolves from DI, with no caller cancellation.
    /// </summary>
    private static Result<ArchiveContents> Read(
        byte[] archive,
        ArchiveCaps caps,
        Func<string, bool> selectEntry,
        long maxEntryBytes = 1024 * 1024,
        long maxBytes = MaxBytes)
    {
        using var content = new MemoryStream(archive);

        return new ParsingService().ReadArchiveEntries(
            content, maxBytes, caps, selectEntry, maxEntryBytes, CancellationToken.None);
    }

    /// <summary>A deflate-compressed archive, so the ratio cap sees real compression.</summary>
    private static byte[] CreateZip(params (string Name, byte[] Content)[] entries)
    {
        using var buffer = new MemoryStream();

        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                using var stream = entry.Open();
                stream.Write(content, 0, content.Length);
            }
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// A stored (uncompressed) archive whose entry names are written verbatim, so a name a <see cref="ZipArchive"/>
    /// writer would normalize — a traversal, a backslash, a doubled separator — reaches the profile exactly as
    /// intended (the same builder <see cref="SafeZipArchiveTests"/> uses).
    /// </summary>
    private static byte[] RawZip(params (string Name, byte[] Content)[] entries)
    {
        // Bit 11 (EFS) marks the entry names UTF-8.
        const ushort Flags = 0x0800;
        const ushort Method = 0;
        const ushort Version = 20;

        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true);
        var offsets = new List<uint>(entries.Length);

        foreach (var (name, content) in entries)
        {
            offsets.Add((uint)buffer.Position);
            var nameBytes = Encoding.UTF8.GetBytes(name);
            var crc = Crc32(content);

            writer.Write(0x04034b50u);
            writer.Write(Version);
            writer.Write(Flags);
            writer.Write(Method);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write(crc);
            writer.Write((uint)content.Length);
            writer.Write((uint)content.Length);
            writer.Write((ushort)nameBytes.Length);
            writer.Write((ushort)0);
            writer.Write(nameBytes);
            writer.Write(content);
        }

        var centralStart = (uint)buffer.Position;

        for (var i = 0; i < entries.Length; i++)
        {
            var (name, content) = entries[i];
            var nameBytes = Encoding.UTF8.GetBytes(name);

            writer.Write(0x02014b50u);
            writer.Write(Version);
            writer.Write(Version);
            writer.Write(Flags);
            writer.Write(Method);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write(Crc32(content));
            writer.Write((uint)content.Length);
            writer.Write((uint)content.Length);
            writer.Write((ushort)nameBytes.Length);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write(0u);
            writer.Write(offsets[i]);
            writer.Write(nameBytes);
        }

        var centralSize = (uint)buffer.Position - centralStart;

        writer.Write(0x06054b50u);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)entries.Length);
        writer.Write((ushort)entries.Length);
        writer.Write(centralSize);
        writer.Write(centralStart);
        writer.Write((ushort)0);
        writer.Flush();

        return buffer.ToArray();
    }

    /// <summary>
    /// A deterministic byte pattern deflate cannot squeeze, so an entry built from it stays inside a small
    /// compression ratio and only a cap the test states can refuse it.
    /// </summary>
    private static byte[] Incompressible(int length)
    {
        var content = new byte[length];
        var state = 0x1234_5678u;

        for (var i = 0; i < content.Length; i++)
        {
            state = (state * 1_664_525u) + 1_013_904_223u;
            content[i] = (byte)(state >> 24);
        }

        return content;
    }

    private static uint Crc32(ReadOnlySpan<byte> content)
    {
        var crc = 0xFFFFFFFFu;

        foreach (var value in content)
        {
            crc ^= value;

            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }
}
