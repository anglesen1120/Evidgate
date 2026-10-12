using System.IO.Compression;
using System.Text;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Evidence.Infrastructure.Parsing;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 06.3.1-T2 QA (AC3): exact-boundary and cumulative-size negatives for the archive caps, and a pin of the
/// production cap values the plan names. The helpers are copies of <see cref="SafeZipArchiveTests"/>' private ones.
/// </summary>
public sealed class SafeZipArchiveQaT2Tests
{
    /// <summary>The kind cap these tests bound the archive object against.</summary>
    private const long MaxBytes = 8 * 1024 * 1024;

    [Fact]
    public void ExpandedSizeExactlyAtTheCapIsAccepted()
    {
        var archive = CreateZip(("a.bin", new byte[2048]));

        var result = Enumerate(archive, MaxBytes, Options(maxExpandedBytes: 2048));

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
    }

    [Fact]
    public void ExpandedSizeOneByteOverTheCapIsRefused()
    {
        var archive = CreateZip(("a.bin", new byte[2049]));

        var result = Enumerate(archive, MaxBytes, Options(maxExpandedBytes: 2048));

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ParsingErrorCode.PayloadTooLarge);
        ReasonOf(result).ShouldBe(ParsingFailureReason.SizeExceeded);
    }

    [Fact]
    public void ExpandedSizeIsCumulativeAcrossEntries()
    {
        // AC3: each entry is under the cap on its own; the sum (3000) exceeds it, so the archive is refused.
        var archive = CreateZip(
            ("a.bin", new byte[1500]),
            ("b.bin", new byte[1500]));

        var result = Enumerate(archive, MaxBytes, Options(maxExpandedBytes: 2048));

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ParsingErrorCode.PayloadTooLarge);
        ReasonOf(result).ShouldBe(ParsingFailureReason.SizeExceeded);
    }

    [Fact]
    public void ExpandedSizeSummedAcrossEntriesExactlyAtTheCapIsAccepted()
    {
        // AC3 boundary: 1500 + 548 = 2048 is the cap itself, so it is accepted.
        var archive = CreateZip(
            ("a.bin", new byte[1500]),
            ("b.bin", new byte[548]));

        var result = Enumerate(archive, MaxBytes, Options(maxExpandedBytes: 2048));

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        result.Value.Sum(entry => entry.ExpandedBytes).ShouldBe(2048);
    }

    [Fact]
    public void EntryCountExactlyAtTheCapIsAccepted()
    {
        var archive = CreateZip(
            ("a", Encoding.UTF8.GetBytes("a")),
            ("b", Encoding.UTF8.GetBytes("b")),
            ("c", Encoding.UTF8.GetBytes("c")));

        var result = Enumerate(archive, MaxBytes, Options(maxEntries: 3));

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        result.Value.Count.ShouldBe(3);
    }

    [Fact]
    public void EntryCountOneOverTheCapIsRefused()
    {
        var archive = CreateZip(
            ("a", Encoding.UTF8.GetBytes("a")),
            ("b", Encoding.UTF8.GetBytes("b")),
            ("c", Encoding.UTF8.GetBytes("c")),
            ("d", Encoding.UTF8.GetBytes("d")));

        var result = Enumerate(archive, MaxBytes, Options(maxEntries: 3));

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ParsingErrorCode.ArtifactInvalid);
        result.Error.Status.ShouldBe(422);
        ReasonOf(result).ShouldBe(ParsingFailureReason.EntryCountExceeded);
    }

    [Fact]
    public void ProductionArchiveCapsMatchThePlanNumbers()
    {
        // AC3 pins: 10 000 entries, 512 MiB expanded, 100:1 ratio, nesting depth 2. A silent change to any of them
        // would move the guard without failing a scaled-down test, so the production values are asserted here.
        Convert.ToInt64(ParsingLimits.MaxZipEntries).ShouldBe(10_000L);
        Convert.ToInt64(ParsingLimits.MaxExpandedBytes).ShouldBe(512L * 1024 * 1024);
        Convert.ToInt64(ParsingLimits.MaxCompressionRatio).ShouldBe(100L);
        Convert.ToInt64(ParsingLimits.MaxNesting).ShouldBe(2L);
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

    /// <summary>Runs the profile's enumeration over a generated archive, with no caller cancellation.</summary>
    private static Result<IReadOnlyList<ArchiveEntry>> Enumerate(byte[] archive, long maxBytes, ZipArchiveOptions options)
    {
        using var content = new MemoryStream(archive);

        return SafeZipArchive.Enumerate(content, maxBytes, options, CancellationToken.None);
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
}
