using System.IO.Compression;
using System.Text;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Evidence.Infrastructure.Parsing;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 06.3.1-T2.1 (AC3, AC4; 00 §5.7 S3/S5): the archive arm of the parser profile. Every hostile archive is
/// generated in memory — the shared corpus lands in 06.3.2 — and every assertion is about a <c>Result</c> value
/// and the absence of a leaked entry name, never about an exception escaping the profile. The caps the tests
/// exercise are scaled down through <see cref="ZipArchiveOptions"/> so each guard is proven with a tiny input;
/// the production caps are <see cref="ParsingLimits"/>.
/// </summary>
public sealed class SafeZipArchiveTests
{
    /// <summary>The kind cap these tests bound the archive object against.</summary>
    private const long MaxBytes = 8 * 1024 * 1024;

    /// <summary>The Unix <c>S_IFLNK</c> file type, in the upper 16 bits of <c>ExternalAttributes</c>.</summary>
    private const uint UnixSymlinkAttributes = (0xA000u | 0x1FFu) << 16;

    /// <summary>The mode the profile creates its extraction root with: owner-only (0700).</summary>
    private const UnixFileMode UserOnlyDirectory =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>The mode the profile creates every extracted file with: owner-only (0600).</summary>
    private const UnixFileMode UserOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>The normalized names the well-formed archive exposes, in archive order (CA1861).</summary>
    private static readonly string[] WellFormedNames =
    [
        "dir/file.txt",
        "root.json",
    ];

    /// <summary>The names the separator/directory archive normalizes to, in archive order (CA1861).</summary>
    private static readonly string[] SeparatorNames =
    [
        "dir",
        "a/b",
        "c",
    ];

    [Fact]
    public void EnumeratesAWellFormedArchiveWithNormalizedNames()
    {
        var archive = CreateZip(
            ("dir/file.txt", Encoding.UTF8.GetBytes("hello")),
            ("root.json", Encoding.UTF8.GetBytes("{\"a\":1}")));

        var result = Enumerate(archive, MaxBytes, Options());

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);

        result.Value.Select(entry => entry.Name).ShouldBe(WellFormedNames);
        result.Value.Single(entry => entry.Name == "dir/file.txt").ExpandedBytes.ShouldBe(5);
        result.Value.ShouldAllBe(entry => !entry.IsDirectory);
    }

    [Fact]
    public void MarksDirectoryEntriesAndNormalizesRedundantSeparators()
    {
        // A directory entry (a trailing '/'), a doubled separator and a '.' segment must all normalize to the
        // same, root-relative path the caller can serve without re-opening the archive.
        var archive = RawZip(
            ("dir/", Array.Empty<byte>()),
            ("a//b", Encoding.UTF8.GetBytes("x")),
            ("./c", Encoding.UTF8.GetBytes("y")));

        var result = Enumerate(archive, MaxBytes, Options());

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);

        var directory = result.Value.Single(entry => entry.Name == "dir");
        directory.IsDirectory.ShouldBeTrue();
        directory.ExpandedBytes.ShouldBe(0);

        result.Value.Select(entry => entry.Name).ShouldBe(SeparatorNames);
    }

    [Fact]
    public void RejectsBombByEntryCount()
    {
        // AC3: the entry cap is checked on the central directory, before any entry byte is read or written.
        var archive = CreateZip(
            ("a", Encoding.UTF8.GetBytes("a")),
            ("b", Encoding.UTF8.GetBytes("b")),
            ("c", Encoding.UTF8.GetBytes("c")));

        var result = Enumerate(archive, MaxBytes, Options(maxEntries: 2));

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ParsingErrorCode.ArtifactInvalid);
        result.Error.Status.ShouldBe(422);
        ReasonOf(result).ShouldBe(ParsingFailureReason.EntryCountExceeded);
    }

    [Fact]
    public void RejectsADeclaredEntryCountOverTheCap()
    {
        // AC3: the count the end-of-central-directory record declares is the whole-directory cost a hostile object
        // can announce, so it is refused before ZipArchive materializes the directory — the archive really holds
        // one entry, but its record claims 0xFFFF, and the refusal is the cap rather than the mismatch that a
        // directory-materializing read would report as malformed.
        var archive = RawZip(("a.txt", Encoding.UTF8.GetBytes("x")));
        PatchDeclaredEntryCount(archive, ushort.MaxValue);

        var result = Enumerate(archive, MaxBytes, Options(maxEntries: 10));

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ParsingErrorCode.ArtifactInvalid);
        ReasonOf(result).ShouldBe(ParsingFailureReason.EntryCountExceeded);
    }

    [Fact]
    public void RejectsADeclaredEntryCountOverTheCapInAZip64Record()
    {
        // AC3: a ZIP64 archive keeps its real count in the ZIP64 record the locator points at. The classic record
        // here declares a count the cap accepts, so only the ZIP64 record can produce the refusal.
        var archive = Zip64WithDeclaredEntryCount(1_000_000);

        var result = Enumerate(archive, MaxBytes, Options(maxEntries: 10));

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ParsingErrorCode.ArtifactInvalid);
        ReasonOf(result).ShouldBe(ParsingFailureReason.EntryCountExceeded);
    }

    [Fact]
    public void AFakeEndOfCentralDirectoryInACommentCannotHideTheCap()
    {
        // AC3 (review fix): the reader takes the LAST signature in the window it scans, so a signature planted at
        // the end of a comment is the record it will use. The pre-check must pick that same record and refuse its
        // count - reading the real record instead would let the planted record's directory be materialized with
        // no declared count ever checked.
        var archive = WithComment(
            RawZip(("a.txt", Encoding.UTF8.GetBytes("x"))),
            EndOfCentralDirectoryRecord(declared: 10_001));

        var result = Enumerate(archive, MaxBytes, Options(maxEntries: 10));

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ParsingErrorCode.ArtifactInvalid);
        ReasonOf(result).ShouldBe(ParsingFailureReason.EntryCountExceeded);
    }

    [Fact]
    public void AnEndOfCentralDirectoryThatDoesNotEndTheObjectIsRefused()
    {
        // AC3 (review fix): the last signature in the window closes exactly at the object's end for every
        // well-formed archive. When it does not, the object is built around a signature planted in a comment, and
        // the count that record declares (here: inside the cap) is never trusted - the object is refused as
        // malformed before the reader is constructed at all.
        var archive = WithComment(
            RawZip(("a.txt", Encoding.UTF8.GetBytes("x"))),
            [.. EndOfCentralDirectoryRecord(declared: 3), .. new byte[8]]);

        var result = Enumerate(archive, MaxBytes, Options(maxEntries: 10));

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ParsingErrorCode.ArtifactInvalid);
        ReasonOf(result).ShouldBe(ParsingFailureReason.Malformed);
    }

    [Fact]
    public void ALegitimateArchiveWithALongCommentIsAccepted()
    {
        // AC3 (review fix): the fail-closed rule above never refuses a well-formed archive that carries a comment:
        // its last signature is the real record, and that record declares a comment length that reaches the
        // object's end exactly.
        var archive = WithComment(
            CreateZip(("a.txt", Encoding.UTF8.GetBytes("x"))),
            Encoding.ASCII.GetBytes(new string('c', 300)));

        var result = Enumerate(archive, MaxBytes, Options(maxEntries: 10));

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        result.Value.ShouldHaveSingleItem().Name.ShouldBe("a.txt");
    }

    [Fact]
    public void RejectsBombByExpandedSize()
    {
        // AC3: the cumulative expanded size is counted from the bytes actually read; a declared length is never
        // trusted, and the size cap maps to the existing 413 payload_too_large.
        var archive = CreateZip(("huge.bin", new byte[8192]));

        var result = Enumerate(archive, MaxBytes, Options(maxExpandedBytes: 2048));

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ParsingErrorCode.PayloadTooLarge);
        result.Error.Status.ShouldBe(413);
        ReasonOf(result).ShouldBe(ParsingFailureReason.SizeExceeded);
    }

    [Fact]
    public void RejectsBombByCompressionRatio()
    {
        // AC3: a highly compressible entry expands far past its compressed bytes and is refused while reading,
        // so the bomb never expands into memory at all.
        var archive = CreateZip(("bomb.bin", new byte[100_000]));

        var result = Enumerate(archive, MaxBytes, Options(maxRatio: 5));

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ParsingErrorCode.ArtifactInvalid);
        ReasonOf(result).ShouldBe(ParsingFailureReason.CompressionRatioExceeded);
    }

    [Theory]
    [InlineData(100, 1, true)] // exactly the profile ratio is inside the cap
    [InlineData(101, 1, false)] // one byte further is a bomb
    [InlineData(0, 0, true)] // no content expands from no compressed bytes
    [InlineData(1, 0, false)] // content that claims to come from nothing
    [InlineData(long.MaxValue, long.MaxValue, true)] // the saturating branch never refuses a legitimate entry
    [InlineData(long.MaxValue, long.MaxValue / 2, true)]
    public void RatioBoundaryIsExactlyTheProfileRatio(long expanded, long compressed, bool accepted) =>
        SafeZipArchive
            .RatioAccepted(expanded, compressed, Options(maxRatio: ParsingLimits.MaxCompressionRatio))
            .ShouldBe(accepted);

    [Fact]
    public void RejectsBombByNesting()
    {
        // AC3: a zip in a zip is level 2 (accepted); a zip inside that is level 3 and refused.
        var deepest = CreateZip(("a.txt", Encoding.UTF8.GetBytes("leaf")));
        var middle = CreateZip(("deeper.zip", deepest));
        var outer = CreateZip(("inner.zip", middle));

        var result = Enumerate(outer, MaxBytes, Options(maxNesting: 2));

        result.IsSuccess.ShouldBeFalse();
        ReasonOf(result).ShouldBe(ParsingFailureReason.NestingExceeded);

        // One level of nesting is inside the cap and must pass.
        var oneLevel = CreateZip(("inner.zip", deepest));

        Enumerate(oneLevel, MaxBytes, Options(maxNesting: 2))
            .IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("a/../../b")]
    [InlineData("..")]
    [InlineData("/etc/x")]
    [InlineData("C:\\x")]
    [InlineData("a\\b")]
    [InlineData(".")]
    [InlineData("./")]
    [InlineData("bad\u0001name")]
    public void RejectsTraversal(string name)
    {
        // AC4: the name is stored verbatim (a raw archive), so a backslash or control character is really present.
        var archive = RawZip((name, Encoding.UTF8.GetBytes("x")));

        var result = Enumerate(archive, MaxBytes, Options());

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ParsingErrorCode.ArtifactInvalid);
        ReasonOf(result).ShouldBe(ParsingFailureReason.UnsafeEntryName);
    }

    [Theory]
    [InlineData("a/b", "a//b")]
    [InlineData("a/b", "A/./b")]
    public void RejectsDuplicateNormalizedNames(string first, string second)
    {
        // AC4: two entries that resolve to the same path could shadow each other, so the archive is refused.
        var archive = RawZip((first, Encoding.UTF8.GetBytes("1")), (second, Encoding.UTF8.GetBytes("2")));

        var result = Enumerate(archive, MaxBytes, Options());

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ParsingErrorCode.ArtifactInvalid);
        ReasonOf(result).ShouldBe(ParsingFailureReason.DuplicateEntry);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("//etc/x")]
    [InlineData(".")]
    [InlineData("./")]
    [InlineData("..")]
    [InlineData("a/../b")]
    [InlineData("C:/x")]
    [InlineData("a\\b")]
    [InlineData("bad\u0001name")]
    [InlineData("a\u202Eb")] // a bidi override: the name reads in one order and resolves in another
    [InlineData("a\u200Bb")] // a zero-width space: an invisible character inside a path
    [InlineData("a\u2028b")] // a line separator
    [InlineData("a\u2029b")] // a paragraph separator
    [InlineData("a\uE000b")] // a private-use character
    [InlineData("a.")] // a segment trailing a dot, which Windows strips
    [InlineData("a ")] // a segment trailing a space, which Windows strips
    [InlineData("a. ")]
    public void NormalizationRefusesUnsafeNames(string name)
    {
        // AC4: the guard itself, including the empty name a ZipArchive writer cannot store.
        SafeZipArchive.TryNormalize(name, out _).ShouldBeFalse();
    }

    [Fact]
    public void NormalizationRefusesUnpairedSurrogates()
    {
        // Built at runtime: an unpaired surrogate in an InlineData literal is replaced with U+FFFD by the theory
        // serializer, which would test a different (legal) character.
        SafeZipArchive.TryNormalize("a" + (char)0xD800 + "b", out _).ShouldBeFalse();
        SafeZipArchive.TryNormalize("a" + (char)0xDC00 + "b", out _).ShouldBeFalse();
    }

    [Fact]
    public void AClassicCountOverTheCapIsRefusedEvenWhenAZip64RecordClaimsLess()
    {
        // A reader that ignores the ZIP64 record when no classic marker is present would use the classic count.
        var archive = Zip64WithDeclaredEntryCount(1, classicCount: 11);

        var result = Enumerate(archive, MaxBytes, Options(maxEntries: 10));

        result.IsSuccess.ShouldBeFalse();
        ReasonOf(result).ShouldBe(ParsingFailureReason.EntryCountExceeded);
    }

    [Fact]
    public void RejectsANameLongerThanTheProfileBound()
    {
        // AC4: the length bound is what keeps normalization linear — without it one entry name could be arbitrarily
        // long, and every separator in it would cost a substring.
        SafeZipArchive.TryNormalize(new string('a', ParsingLimits.MaxEntryNameLength + 1), out _).ShouldBeFalse();
        SafeZipArchive.TryNormalize(new string('a', ParsingLimits.MaxEntryNameLength), out _).ShouldBeTrue();
    }

    [Fact]
    public void RejectsANameDeeperThanTheProfileSegmentBound()
    {
        // AC4: the segment bound bounds the ancestor walk one name can ask for. The names here are far inside the
        // length bound, so only the segment count can produce the refusal.
        var tooDeep = string.Join('/', Enumerable.Repeat("a", ParsingLimits.MaxEntryNameSegments + 1));
        var atTheBound = string.Join('/', Enumerable.Repeat("a", ParsingLimits.MaxEntryNameSegments));

        tooDeep.Length.ShouldBeLessThan(ParsingLimits.MaxEntryNameLength);
        SafeZipArchive.TryNormalize(tooDeep, out _).ShouldBeFalse();
        SafeZipArchive.TryNormalize(atTheBound, out _).ShouldBeTrue();
    }

    [Fact]
    public void NormalizationKeepsANonAsciiNameWithASurrogatePair()
    {
        // AC4: the character rule refuses what does not render as itself, never what does: a Vietnamese path and an
        // astral code point (one surrogate pair) are kept verbatim.
        const string Name = "báo cáo/\uD83D\uDE00.json";

        SafeZipArchive.TryNormalize(Name, out var normalized).ShouldBeTrue();

        normalized.ShouldBe(Name);
    }

    [Theory]
    [InlineData("a/b", "a/b")]
    [InlineData("a//b", "a/b")]
    [InlineData("a/./b", "a/b")]
    [InlineData("dir/", "dir")]
    [InlineData("dir/file.txt", "dir/file.txt")]
    public void NormalizationCollapsesRedundantSeparators(string name, string expected)
    {
        SafeZipArchive.TryNormalize(name, out var normalized).ShouldBeTrue();

        normalized.ShouldBe(expected);
    }

    [Fact]
    public void RejectsASymbolicLinkEntry()
    {
        // AC4: an entry whose Unix mode says "symbolic link" is refused, so the profile never follows a link.
        var archive = RawZip(("link", Encoding.UTF8.GetBytes("../../etc/passwd"), UnixSymlinkAttributes));

        var result = Enumerate(archive, MaxBytes, Options());

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ParsingErrorCode.ArtifactInvalid);
        ReasonOf(result).ShouldBe(ParsingFailureReason.UnsafeEntryName);
    }

    [Fact]
    public void RejectsANonZipObject()
    {
        var result = Enumerate(Encoding.UTF8.GetBytes("not an archive at all"), MaxBytes, Options());

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ParsingErrorCode.ArtifactInvalid);
        ReasonOf(result).ShouldBe(ParsingFailureReason.Malformed);
    }

    [Fact]
    public void RejectsAnObjectLongerThanTheKindCap()
    {
        // The archive object itself is bounded first: a stream longer than the kind's cap is 413, before the zip
        // is even parsed. The entry is stored (uncompressed) so the object is genuinely longer than the cap.
        var archive = RawZip(("a.bin", new byte[4096]));

        var result = Enumerate(archive, 256, Options());

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ParsingErrorCode.PayloadTooLarge);
        ReasonOf(result).ShouldBe(ParsingFailureReason.SizeExceeded);
    }

    [Fact]
    public void ARejectionNeverEchoesTheEntryName()
    {
        // 00 §5.7 S3: no message, code or reason may carry an entry name.
        const string Hostile = "../etc/passwd";
        var archive = RawZip((Hostile, Encoding.UTF8.GetBytes("x")));

        var result = Enumerate(archive, MaxBytes, Options());

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Message.ShouldNotContain("passwd");
        result.Error.Message.ShouldNotContain("..");
        result.Error.Details!.Single().Reason.ShouldBe(ParsingFailureReason.UnsafeEntryName);
    }

    [Fact]
    public void ExtractionWritesOnlyInsideItsOwnedRootAndCleansUp()
    {
        // AC4: every byte lands under the root the profile created, no destination escapes it, and disposing the
        // handle removes the whole tree.
        var archive = CreateZip(
            ("dir/file.txt", Encoding.UTF8.GetBytes("hello")),
            ("top.json", Encoding.UTF8.GetBytes("{}")));

        var result = Extract(archive, MaxBytes, Options());
        result.IsSuccess.ShouldBeTrue(result.Error?.Message);

        var extraction = result.Value;
        var root = extraction.Root;
        var prefix = root + Path.DirectorySeparatorChar;

        try
        {
            Directory.Exists(root).ShouldBeTrue();
            File.ReadAllText(Path.Combine(root, "dir", "file.txt")).ShouldBe("hello");

            var written = Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).ToArray();
            written.ShouldNotBeEmpty();
            written.ShouldAllBe(path => path.StartsWith(prefix, StringComparison.Ordinal));

            if (!OperatingSystem.IsWindows())
            {
                // The root and every extracted file are owner-only: the tree holds untrusted artifact bytes under a
                // process temp directory other local users can reach.
                File.GetUnixFileMode(root).ShouldBe(UserOnlyDirectory);
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    File.GetUnixFileMode(file).ShouldBe(UserOnlyFile);
                }
            }
        }
        finally
        {
            extraction.Dispose();
        }

        Directory.Exists(root).ShouldBeFalse();
    }

    [Fact]
    public void ARejectedArchiveCreatesNoExtractionRoot()
    {
        // A central-directory rejection (an unsafe name) happens before any byte is written, so no root exists.
        var archive = RawZip(("../etc/passwd", Encoding.UTF8.GetBytes("x")));
        var before = ParseRoots();

        var result = Extract(archive, MaxBytes, Options());

        result.IsSuccess.ShouldBeFalse();
        ParseRoots().ShouldBe(before);
    }

    [Fact]
    public void RejectsAFileEntryThatShadowsAnAncestorFolder()
    {
        // AC4: 'a' as a file and 'a/b' cannot both be materialized — one path component cannot be both — so the
        // archive is refused from the central directory, before the extraction root exists.
        var archive = RawZip(("a", Encoding.UTF8.GetBytes("x")), ("a/b", Encoding.UTF8.GetBytes("y")));
        var before = ParseRoots();

        var result = Extract(archive, MaxBytes, Options());

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ParsingErrorCode.ArtifactInvalid);
        ReasonOf(result).ShouldBe(ParsingFailureReason.DuplicateEntry);
        ParseRoots().ShouldBe(before);
    }

    [Fact]
    public void ExtractionWritesATopLevelEntryButNotANestedArchivesChildren()
    {
        // A nested archive is walked for its caps and names only: its own entries are never written under the
        // root, so a nested name cannot collide with a top-level destination.
        var inner = CreateZip(("child.txt", Encoding.UTF8.GetBytes("inner")));
        var archive = CreateZip(("bundle.zip", inner));

        var result = Extract(archive, MaxBytes, Options());
        result.IsSuccess.ShouldBeTrue(result.Error?.Message);

        var root = result.Value.Root;

        try
        {
            File.Exists(Path.Combine(root, "bundle.zip")).ShouldBeTrue();
            File.Exists(Path.Combine(root, "child.txt")).ShouldBeFalse();
        }
        finally
        {
            result.Value.Dispose();
        }
    }

    [Fact]
    public void ASizeBreachIsRefusedBeforeAnyExtractionRootExists()
    {
        // AC3, D1: the expanded-size cap is proven by the non-extracting pass, so the extraction pass — the only
        // pass that creates a root — never starts: no root and no partial file exists when the breach is found.
        var archive = CreateZip(("huge.bin", new byte[8192]));
        var before = ParseRoots();

        var result = Extract(archive, MaxBytes, Options(maxExpandedBytes: 2048));

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ParsingErrorCode.PayloadTooLarge);
        ReasonOf(result).ShouldBe(ParsingFailureReason.SizeExceeded);
        ParseRoots().ShouldBe(before);
    }

    [Fact]
    public void ARatioBreachIsRefusedBeforeAnyExtractionRootExists()
    {
        // AC3, D1: the per-entry ratio is a content-derived cap, so it too is proven before the extraction pass
        // writes anything — the probe reads the bomb, the extraction pass never runs.
        var archive = CreateZip(("bomb.bin", new byte[100_000]));
        var before = ParseRoots();

        var result = Extract(archive, MaxBytes, Options(maxRatio: 5));

        result.IsSuccess.ShouldBeFalse();
        ReasonOf(result).ShouldBe(ParsingFailureReason.CompressionRatioExceeded);
        ParseRoots().ShouldBe(before);
    }

    [Fact]
    public void AWritePassRefusalRemovesTheRootItCreated()
    {
        // The probe pass only reads names, so a component longer than the volume's name limit (255 bytes on
        // APFS and ext4) is refused by the file system in the extraction pass. That is the failure the cleanup
        // path exists for: the root was created, the write failed, and nothing may survive.
        var archive = CreateZip((new string('a', 300), Encoding.UTF8.GetBytes("x")));
        var before = ParseRoots();

        var result = Extract(archive, MaxBytes, Options());

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ParsingErrorCode.ArtifactInvalid);
        ReasonOf(result).ShouldBe(ParsingFailureReason.Malformed);
        ParseRoots().ShouldBe(before);
    }

    /// <summary>Publishes <see cref="IParsing.EnumerateArchive"/> through the module's service adapter.</summary>
    [Fact]
    public void ParsingServicePublishesArchiveEnumeration()
    {
        var archive = CreateZip(("a.txt", Encoding.UTF8.GetBytes("x")));

        var result = new ParsingService().EnumerateArchive(new MemoryStream(archive), MaxBytes, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        result.Value.ShouldHaveSingleItem().Name.ShouldBe("a.txt");
    }

    [Fact]
    public void ACallerCancellationPropagatesAndLeavesNoRoot()
    {
        // The profile never swallows a caller's cancellation: it propagates, and no extraction tree survives it.
        // The token is cancelled on the stream's first read, so the walk is already inside the profile — the same
        // place a cancellation mid-walk lands, where the walk's single cleanup point deletes the root it created.
        var archive = CreateZip(("a.txt", Encoding.UTF8.GetBytes("x")));
        var before = ParseRoots();
        using var cancellation = new CancellationTokenSource();
        using var content = new CancellingStream(archive, cancellation);

        Should.Throw<OperationCanceledException>(() =>
        {
            _ = SafeZipArchive.Extract(content, MaxBytes, Options(), cancellation.Token);
        });

        ParseRoots().ShouldBe(before);
    }

    /// <summary>Scaled-down caps; the defaults the profile enforces are <see cref="ParsingLimits"/>.</summary>
    private static ZipArchiveOptions Options(
        int maxEntries = 1000,
        long maxExpandedBytes = 4L * 1024 * 1024,
        int maxRatio = 1000,
        int maxNesting = 2,
        TimeSpan? timeout = null) =>
        new(maxEntries, maxExpandedBytes, maxRatio, maxNesting, timeout ?? TimeSpan.FromSeconds(30));

    /// <summary>The generic reason a failure carries in its <c>details</c> (00 §5.7 S3).</summary>
    private static string? ReasonOf(Result result) =>
        result.Error?.Details?.SingleOrDefault()?.Reason;

    /// <summary>The temporary extraction roots currently under the process temp directory.</summary>
    private static string[] ParseRoots() =>
        Directory.Exists(Path.GetTempPath())
            ? Directory.GetDirectories(Path.GetTempPath(), "evidgate-parse-*")
            : Array.Empty<string>();

    /// <summary>Runs the profile's enumeration over a generated archive, with no caller cancellation.</summary>
    private static Result<IReadOnlyList<ArchiveEntry>> Enumerate(byte[] archive, long maxBytes, ZipArchiveOptions options)
    {
        using var content = new MemoryStream(archive);

        return SafeZipArchive.Enumerate(content, maxBytes, options, CancellationToken.None);
    }

    /// <summary>Runs the profile's extraction over a generated archive, with no caller cancellation.</summary>
    private static Result<ArchiveExtraction> Extract(byte[] archive, long maxBytes, ZipArchiveOptions options)
    {
        using var content = new MemoryStream(archive);

        return SafeZipArchive.Extract(content, maxBytes, options, CancellationToken.None);
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
    /// writer would normalize (backslashes, doubled separators) and a Unix external attribute reach the profile
    /// exactly as intended.
    /// </summary>
    private static byte[] RawZip(params (string Name, byte[] Content)[] entries) =>
        RawZip(entries.Select(entry => (entry.Name, entry.Content, 0u)).ToArray());

    private static byte[] RawZip(params (string Name, byte[] Content, uint ExternalAttributes)[] entries)
    {
        // Bit 11 (EFS) marks the entry names UTF-8.
        const ushort Flags = 0x0800;
        const ushort Method = 0;
        const ushort Version = 20;

        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true);
        var offsets = new List<uint>(entries.Length);

        foreach (var (name, content, _) in entries)
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
            var (name, content, externalAttributes) = entries[i];
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
            writer.Write(externalAttributes);
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
    /// Rewrites the entry count a classic end-of-central-directory record declares. <see cref="RawZip"/> writes the
    /// record without a comment, so it is the object's last 22 bytes and its count sits 10 bytes in.
    /// </summary>
    private static void PatchDeclaredEntryCount(byte[] archive, ushort declared)
    {
        var count = archive.Length - 22 + 10;

        archive[count] = (byte)(declared & 0xFF);
        archive[count + 1] = (byte)(declared >> 8);
    }

    /// <summary>
    /// Appends <paramref name="comment"/> to a classic end-of-central-directory record written without one, and
    /// rewrites the record's comment length to reach it. The record is the object's last 22 bytes before the call,
    /// so its comment length sits 20 bytes in.
    /// </summary>
    private static byte[] WithComment(byte[] archive, byte[] comment)
    {
        var record = archive.Length - 22;
        var patched = new byte[archive.Length + comment.Length];

        archive.CopyTo(patched, 0);
        comment.CopyTo(patched, archive.Length);

        patched[record + 20] = (byte)(comment.Length & 0xFF);
        patched[record + 21] = (byte)(comment.Length >> 8);

        return patched;
    }

    /// <summary>
    /// One classic end-of-central-directory record: signature, disk numbers, the declared count for this disk and
    /// in total, an empty central directory and no comment, so a caller appending it last makes a record that
    /// closes exactly at the object's end.
    /// </summary>
    private static byte[] EndOfCentralDirectoryRecord(ushort declared)
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true);

        writer.Write(0x06054b50u);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(declared);
        writer.Write(declared);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write((ushort)0);
        writer.Flush();

        return buffer.ToArray();
    }

    /// <summary>
    /// A ZIP64 end-of-central-directory record, its locator and a classic record, declaring
    /// <paramref name="declared"/> entries in the ZIP64 record. The classic record's own count is inside any cap,
    /// so the ZIP64 record is the only place the declared count can be read from.
    /// </summary>
    private static byte[] Zip64WithDeclaredEntryCount(ulong declared, ushort classicCount = 1)
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true);

        // The ZIP64 record's fixed part: signature, the size of the rest, versions, disk numbers, then the entry
        // count for this disk and the total.
        writer.Write(0x06064b50u);
        writer.Write(44ul);
        writer.Write((ushort)45);
        writer.Write((ushort)45);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(declared);
        writer.Write(declared);

        // The locator, directly before the classic record, pointing at the ZIP64 record at offset 0.
        writer.Write(0x07064b50u);
        writer.Write(0u);
        writer.Write(0ul);
        writer.Write(1u);

        // The classic record, with no comment and a count the cap accepts.
        writer.Write(0x06054b50u);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(classicCount);
        writer.Write(classicCount);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write((ushort)0);
        writer.Flush();

        return buffer.ToArray();
    }

    /// <summary>
    /// A stream that cancels the caller's token on its first read, so a walk is interrupted while it is already
    /// inside the profile.
    /// </summary>
    private sealed class CancellingStream(byte[] content, CancellationTokenSource cancellation) : MemoryStream(content)
    {
        private bool cancelled;

        public override int Read(byte[] buffer, int offset, int count)
        {
            Cancel();

            return base.Read(buffer, offset, count);
        }

        public override int Read(Span<byte> buffer)
        {
            Cancel();

            return base.Read(buffer);
        }

        private void Cancel()
        {
            if (cancelled)
            {
                return;
            }

            cancelled = true;
            cancellation.Cancel();
        }
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
