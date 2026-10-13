// 06.3.2-T1.1 (AC1): zipbomb.zip is GENERATED, never committed — its only property that matters (more entries
// than the profile's MaxZipEntries cap, at a decompression ratio worth refusing) is reproducible from a seed,
// so a committed binary would add bytes to the repository for nothing.
using System.IO.Compression;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The 06.3.2 corpus's archive bomb: one archive with
/// <see cref="EntryCount"/> entries — one more than <c>ParsingLimits.MaxZipEntries</c> — each of a few
/// deterministic bytes. The count is what the profile refuses
/// (<c>entry_count_exceeded</c>), decided from the declared central-directory count before a single entry is
/// read, so the bomb costs a bounded build (a few MiB of writes, one <c>Deflate</c> per entry) and a bounded
/// refusal. The bytes are a pure function of <paramref name="seed"/> (no clock, no random source), so the
/// fixture is byte-identical on every run and in every process.
/// </summary>
internal static class GenerateBombFixture
{
    /// <summary>The committed fixture name the manifest marks <c>generated: true</c>.</summary>
    internal const string FileName = "zipbomb.zip";

    /// <summary>The seed every byte of the archive derives from.</summary>
    internal const int DefaultSeed = 20_261_013;

    /// <summary>The entry count: one above <c>ParsingLimits.MaxZipEntries</c>, so the cap alone decides.</summary>
    internal const int EntryCount = 10_500;

    /// <summary>The largest entry content, in bytes (0..7): the bomb's property is its count, not its size.</summary>
    internal const int MaxEntryBytes = 8;

    /// <summary>The entry name prefix; names are unique and carry no guard-relevant character.</summary>
    internal const string NamePrefix = "bomb/entry-";

    /// <summary>Builds the archive in memory (a few hundred KiB; the caller writes it under a temp directory).</summary>
    /// <param name="seed">The seed the entry names and contents derive from.</param>
    /// <returns>The archive bytes.</returns>
    internal static byte[] Build(int seed = DefaultSeed)
    {
        using var buffer = new MemoryStream();

        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (var index = 0; index < EntryCount; index++)
            {
                var entry = archive.CreateEntry(Name(index), CompressionLevel.Optimal);
                // A fixed DOS timestamp keeps the archive a pure function of the seed (the wall clock would not).
                entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

                using var content = entry.Open();
                content.Write(Content(seed, index));
            }
        }

        return buffer.ToArray();
    }

    /// <summary>The deterministic entry name of one entry.</summary>
    /// <param name="index">The zero-based entry index.</param>
    internal static string Name(int index) => $"{NamePrefix}{index:D5}.bin";

    /// <summary>The deterministic content of one entry: 0..7 bytes of one repeated value, chosen by the seed.</summary>
    /// <param name="seed">The corpus seed.</param>
    /// <param name="index">The zero-based entry index.</param>
    private static byte[] Content(int seed, int index)
    {
        var content = new byte[(seed + index) % MaxEntryBytes];

        for (var offset = 0; offset < content.Length; offset++)
        {
            content[offset] = (byte)(seed + index);
        }

        return content;
    }
}
