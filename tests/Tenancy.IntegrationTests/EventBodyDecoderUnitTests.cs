// 04.1.1-T3 (AC7, DECISIONS §8.1 D-A8): the gzip decoder's cap arithmetic, without a database or a host.
// The transport cap (1 MiB as sent) belongs to the endpoint and the 500-event cap to the parser; this file
// pins the two IngestionLimits caps and every well-formedness refusal of EventBodyDecoder.
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Evidgate.Modules.Ingestion.Application;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The gzip half of the 04.1.1-T3 caps (AC7): a valid member round-trips, the expanded-size and ratio caps
/// fire exactly on their boundary and one unit past it, the bomb aborts within one chunk of the cap, and
/// every malformed container — multi-member, trailing data, truncation, corruption, a stub — is refused
/// rather than ignored.
/// </summary>
public sealed class EventBodyDecoderUnitTests
{
    /// <summary>The boot defaults of DECISIONS §8.1 D-A8: 8 MiB expanded, 20:1.</summary>
    private static IngestionLimits Defaults() => new();

    /// <summary>
    /// Small limits, so the cap arithmetic is exercised on cheap bodies. The <c>Range</c> attribute on
    /// <see cref="IngestionLimits"/> is the configuration validator's guard and is tested by
    /// <c>IngestionLimitsOptionsTests</c>; the decoder is a pure function over whatever it is handed.
    /// </summary>
    private static IngestionLimits Small(int maxExpanded, int ratio) =>
        new() { MaxExpandedBytes = maxExpanded, MaxExpansionRatio = ratio };

    // ------------------------------------------------------------------ the happy path

    [Fact]
    public void valid_gzip_member_round_trips()
    {
        // A real batch body, gzip-compressed by the client: the decoded bytes must be the original bytes.
        var payload = Encoding.UTF8.GetBytes(
            """{"schema_version":1,"events":[{"event_id":"0198a0f0-0000-7000-8000-000000000000","sequence":1}]}""");

        using var decoded = EventBodyDecoder.DecodeGzip(GzipOf(payload), Defaults());

        decoded.Reason.ShouldBeNull();
        decoded.ExpandedBytes.ShouldBe(payload.Length);
        decoded.Bytes.ToArray().ShouldBe(payload);
    }

    [Fact]
    public void member_written_with_an_explicit_flush_is_accepted()
    {
        // A producer that flushes before closing (pigz, Node's zlib.flush(), a streaming client) emits an
        // extra empty stored block before the final one; the member must still be accepted, and its trailer
        // must still be found at the body's end.
        var payload = Encoding.UTF8.GetBytes(
            """{"schema_version":1,"events":[{"event_id":"0198a0f0-0000-7000-8000-000000000001","sequence":1}]}""");

        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(payload);
            gzip.Flush();
        }

        using var decoded = EventBodyDecoder.DecodeGzip(output.ToArray(), Defaults());

        decoded.Reason.ShouldBeNull();
        decoded.Bytes.ToArray().ShouldBe(payload);
    }

    [Fact]
    public void trailer_isize_is_little_endian()
    {
        // RFC 1952 §2.3.1: both trailer fields are little-endian. Reading them big-endian would make every
        // complete member look corrupt (the regression this pins).
        var member = GzipOf(new byte[4096]);

        BinaryPrimitives.ReadUInt32LittleEndian(member.AsSpan(member.Length - 4)).ShouldBe(4096u);
    }

    // ------------------------------------------------------------------ the expanded-size cap (AC7)

    [Fact]
    public void expanded_size_exactly_at_the_cap_is_accepted_and_one_byte_over_is_refused()
    {
        // A run of zeros is the cheapest expansion; ratio 1000 keeps the ratio cap far away, so only the
        // expanded-size cap can decide these two bodies.
        var limits = Small(maxExpanded: 4096, ratio: 1000);

        using var atCap = EventBodyDecoder.DecodeGzip(GzipOf(new byte[4096]), limits);

        atCap.Reason.ShouldBeNull();
        atCap.ExpandedBytes.ShouldBe(4096);

        using var overCap = EventBodyDecoder.DecodeGzip(GzipOf(new byte[4097]), limits);

        overCap.Reason.ShouldBe(IngestBatchRejection.ExpandedTooLarge);
    }

    [Fact]
    public void expansion_bomb_aborts_within_one_chunk_of_the_expanded_cap()
    {
        // 4 MiB of zeros compresses to ~16 KiB, so the ratio cap (1000:1) stays above the expanded cap and
        // the expanded cap is the one that fires. The guard must abort there, never inflate the payload.
        var limits = Small(maxExpanded: 4096, ratio: 1000);
        var bomb = GzipOf(new byte[4 * 1024 * 1024]);

        using var decoded = EventBodyDecoder.DecodeGzip(bomb, limits);

        decoded.Reason.ShouldBe(IngestBatchRejection.ExpandedTooLarge);
        decoded.Bytes.Length.ShouldBe(0);
        decoded.ExpandedBytes.ShouldBeLessThanOrEqualTo(4096 + EventBodyDecoder.ChunkSize);
        decoded.ExpandedBytes.ShouldBeLessThan(4 * 1024 * 1024);
    }

    // ------------------------------------------------------------------ the ratio cap (AC7)

    [Fact]
    public void expansion_ratio_exactly_at_the_cap_is_accepted_and_one_byte_over_is_refused()
    {
        // The production 20:1 default. A zero-run body is a step function of the payload length, so a length
        // whose compressed member is exactly 1/20 of the decoded length exists near every step; the helper
        // finds one and also returns the same-size neighbour, which is the "one byte over" body.
        var (length, compressed) = ExactRatioBody(20);
        var neighbour = GzipOf(new byte[length + 1]);
        neighbour.Length.ShouldBe(compressed.Length);

        using var atCap = EventBodyDecoder.DecodeGzip(compressed, Defaults());

        atCap.Reason.ShouldBeNull();
        atCap.ExpandedBytes.ShouldBe(length);
        (compressed.Length * 20).ShouldBe(length);

        using var overCap = EventBodyDecoder.DecodeGzip(neighbour, Defaults());

        overCap.Reason.ShouldBe(IngestBatchRejection.RatioExceeded);
    }

    [Fact]
    public void zero_run_bomb_aborts_at_the_ratio_cap_under_the_defaults()
    {
        // The AC7 bomb: 50 MiB of zeros from a compressed body of ~200 KiB. Under the 20:1 default the ratio
        // cap (~4 MiB) is below the expanded cap (8 MiB), so the ratio is the first guard.
        var bomb = GzipOf(new byte[50 * 1024 * 1024]);
        var ratioCap = 20L * bomb.Length;
        ratioCap.ShouldBeLessThan(8_388_608);

        using var decoded = EventBodyDecoder.DecodeGzip(bomb, Defaults());

        decoded.Reason.ShouldBe(IngestBatchRejection.RatioExceeded);
        decoded.Bytes.Length.ShouldBe(0);
        decoded.ExpandedBytes.ShouldBeLessThanOrEqualTo((int)ratioCap + EventBodyDecoder.ChunkSize);
    }

    // ------------------------------------------------------------------ malformed containers

    [Fact]
    public void multi_member_is_refused()
    {
        var first = GzipOf("first"u8.ToArray());
        var second = GzipOf("second"u8.ToArray());
        var both = new byte[first.Length + second.Length];
        first.CopyTo(both, 0);
        second.CopyTo(both, first.Length);

        using var decoded = EventBodyDecoder.DecodeGzip(both, Defaults());

        decoded.Reason.ShouldBe(IngestBatchRejection.GzipInvalid);
    }

    [Fact]
    public void trailing_data_is_refused()
    {
        var member = GzipOf("payload"u8.ToArray());
        var withTail = new byte[member.Length + 3];
        member.CopyTo(withTail, 0);
        "xyz"u8.CopyTo(withTail.AsSpan(member.Length));

        using var decoded = EventBodyDecoder.DecodeGzip(withTail, Defaults());

        decoded.Reason.ShouldBe(IngestBatchRejection.GzipInvalid);
    }

    [Fact]
    public void truncated_member_is_refused()
    {
        // Incompressible bytes, so the 20:1 ratio cannot fire first and the truncated trailer is what the
        // decoder must notice.
        var member = GzipOf(Incompressible(4096));
        var truncated = member.AsSpan(0, member.Length - 4).ToArray();

        using var decoded = EventBodyDecoder.DecodeGzip(truncated, Defaults());

        decoded.Reason.ShouldBe(IngestBatchRejection.GzipInvalid);
    }

    [Fact]
    public void corrupt_crc_is_refused()
    {
        // Flip a byte of the deflate payload, so the member is well-formed but the CRC-32 no longer matches.
        var member = GzipOf(Encoding.UTF8.GetBytes("the quick brown fox jumps over the lazy dog"));
        member[12] ^= 0xFF;

        using var decoded = EventBodyDecoder.DecodeGzip(member, Defaults());

        decoded.Reason.ShouldBe(IngestBatchRejection.GzipInvalid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(8)]
    public void body_too_short_to_be_a_member_is_refused(int length)
    {
        using var decoded = EventBodyDecoder.DecodeGzip(new byte[length], Defaults());

        decoded.Reason.ShouldBe(IngestBatchRejection.GzipInvalid);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>One well-formed gzip member of <paramref name="payload"/>, as a client would send it.</summary>
    private static byte[] GzipOf(byte[] payload)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(payload);
        }

        return output.ToArray();
    }

    /// <summary>Deterministic incompressible bytes, so a member of <paramref name="length"/> stays that size when gzipped.</summary>
    private static byte[] Incompressible(int length)
    {
        var bytes = new byte[length];
        var state = 0x2545F491u;
        for (var i = 0; i < length; i++)
        {
            state = (state * 1664525u) + 1013904223u;
            bytes[i] = (byte)(state >> 24);
        }

        return bytes;
    }

    /// <summary>
    /// A zero-run payload whose compressed member is exactly <c>1/ratio</c> of the decoded length, together
    /// with that member. The neighbour of the returned length has the same compressed size, which is what
    /// makes the boundary exact: one more decoded byte for the same compressed size is over the cap.
    /// </summary>
    private static (int Length, byte[] Compressed) ExactRatioBody(int ratio)
    {
        var length = 64;
        var compressed = GzipOf(new byte[length]);

        for (; length < 100_000; length++)
        {
            var next = GzipOf(new byte[length + 1]);
            if (next.Length == compressed.Length && compressed.Length * ratio == length)
            {
                return (length, compressed);
            }

            compressed = next;
        }

        throw new InvalidOperationException($"no zero-run length reaches an exact {ratio}:1 ratio boundary");
    }
}
