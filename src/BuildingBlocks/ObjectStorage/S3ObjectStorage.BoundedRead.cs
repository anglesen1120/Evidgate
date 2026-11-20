using System.Buffers;

namespace Evidgate.BuildingBlocks.ObjectStorage;

internal sealed partial class S3ObjectStorage
{
    /// <summary>
    /// Reads the response stream up to <paramref name="maxBytes"/> bytes, disposing it first. The ranged GET
    /// already caps the body, but a store that ignores the range is still read no further than the cap, so the
    /// read is bounded by construction.
    /// <para>
    /// The buffer is allocated once, at the length the store's own <c>Content-Length</c> declares
    /// (<paramref name="declaredLength"/>), so the peak is about one copy of the object: no doubling writer and no
    /// second copy at the end. The length is believed only as far as the cap and only as a starting size — a store
    /// that declares less than it sends still cannot turn a truncated read into a complete document, because the
    /// stream is read on past the declared length (bounded by the cap) and the buffer then grows the old way.
    /// </para>
    /// </summary>
    /// <param name="stream">The response body.</param>
    /// <param name="maxBytes">The most bytes to read; one byte past it is enough to prove the object is longer.</param>
    /// <param name="declaredLength">
    /// The response's own <c>Content-Length</c>, or a negative value when the store reported none.
    /// </param>
    /// <param name="cancellationToken">Cancels the read.</param>
    private static async Task<byte[]> ReadBoundedAsync(
        Stream stream, int maxBytes, long declaredLength, CancellationToken cancellationToken)
    {
        await using (stream.ConfigureAwait(false))
        {
            if (declaredLength is >= 0 && declaredLength <= maxBytes)
            {
                var exact = new byte[declaredLength];
                var filled = 0;

                while (filled < exact.Length)
                {
                    var read = await stream.ReadAsync(exact.AsMemory(filled), cancellationToken).ConfigureAwait(false);

                    if (read == 0)
                    {
                        // The store sent less than it declared: the object is that shorter body, not the promise.
                        return exact[..filled];
                    }

                    filled += read;
                }

                if (filled < maxBytes)
                {
                    // A store that declares less than it sends must not be able to hand back a truncated object as
                    // if it were whole: the excess is read too (bounded by the cap) and the buffer grows for it.
                    var probe = await ProbeAsync(stream, cancellationToken).ConfigureAwait(false);

                    if (probe.Length == 0)
                    {
                        return exact;
                    }

                    return await ReadGrowingAsync(stream, exact, probe, maxBytes, cancellationToken)
                        .ConfigureAwait(false);
                }

                return exact;
            }

            return await ReadGrowingAsync(stream, [], [], maxBytes, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads one more byte than a response declared, so a store that padded its body is noticed instead of trusted.
    /// </summary>
    /// <param name="stream">The response body, positioned after the declared bytes.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The bytes that followed the declared length; empty when the body ended where it said it would.</returns>
    private static async Task<byte[]> ProbeAsync(Stream stream, CancellationToken cancellationToken)
    {
        var probe = new byte[1];

        if (await stream.ReadAsync(probe, cancellationToken).ConfigureAwait(false) == 0)
        {
            return [];
        }

        return probe;
    }

    /// <summary>
    /// The growth path: an undeclared, or unexpectedly longer, body is read into an
    /// <see cref="ArrayBufferWriter{T}"/> seeded with what already arrived, still bounded by
    /// <paramref name="maxBytes"/>. <see cref="ArrayBufferWriter{T}"/> doubles, so the transient overhead stays
    /// proportional to the object.
    /// </summary>
    /// <param name="stream">The response body.</param>
    /// <param name="prefix">The bytes already read into the exact buffer.</param>
    /// <param name="probe">The bytes the declaring store sent past its own length, empty when none were read.</param>
    /// <param name="maxBytes">The most bytes to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The bytes read, at most <paramref name="maxBytes"/> of them.</returns>
    private static async Task<byte[]> ReadGrowingAsync(
        Stream stream, byte[] prefix, byte[] probe, int maxBytes, CancellationToken cancellationToken)
    {
        const int InitialCapacity = 82_944;

        var writer = new ArrayBufferWriter<byte>(Math.Min(maxBytes, InitialCapacity));

        writer.Write(prefix);
        writer.Write(probe);

        while (writer.WrittenCount < maxBytes)
        {
            var memory = writer.GetMemory(Math.Min(InitialCapacity, maxBytes - writer.WrittenCount));
            var read = await stream.ReadAsync(memory, cancellationToken).ConfigureAwait(false);

            if (read == 0)
            {
                break;
            }

            writer.Advance(read);
        }

        return writer.WrittenSpan.ToArray();
    }
}
