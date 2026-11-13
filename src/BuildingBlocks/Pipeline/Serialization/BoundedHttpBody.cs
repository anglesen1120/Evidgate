using Evidgate.BuildingBlocks.SharedKernel.Serialization;

namespace Evidgate.BuildingBlocks.Pipeline.Serialization;

/// <summary>
/// Reads an outbound HTTP response body under a byte cap. The cap is checked against
/// <c>Content-Length</c> before any read and again while streaming, so a chunked body or a header that
/// lies is refused once a read crosses the cap instead of being buffered.
/// </summary>
/// <remarks>
/// The HTTP overloads live here, not in SharedKernel: SharedKernel is inside the evaluator's I/O-free
/// closure, and Pipeline is the shared BuildingBlock that already owns request/response plumbing and that
/// both outbound-client modules reference, so the move adds no project or dependency edge.
/// </remarks>
public static class BoundedHttpBody
{
    /// <summary>Reads at most <paramref name="maxBytes"/> bytes; returns null when the body is longer.</summary>
    /// <param name="content">The body. Its stream is disposed.</param>
    /// <param name="maxBytes">Largest accepted body, in bytes.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async Task<byte[]?> ReadBytesAsync(HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        var (stream, declared) = await OpenAsync(content, maxBytes, cancellationToken).ConfigureAwait(false);
        if (stream is null)
        {
            return null;
        }

        await using (stream.ConfigureAwait(false))
        {
            return await BoundedStreamBody
                .ReadBytesAsync(stream, maxBytes, cancellationToken, declared)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Reads at most <paramref name="maxBytes"/> bytes as UTF-8 text (invalid bytes become U+FFFD); null when longer.</summary>
    /// <param name="content">The body. Its stream is disposed.</param>
    /// <param name="maxBytes">Largest accepted body, in bytes.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async Task<string?> ReadTextAsync(HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        var (stream, declared) = await OpenAsync(content, maxBytes, cancellationToken).ConfigureAwait(false);
        if (stream is null)
        {
            return null;
        }

        await using (stream.ConfigureAwait(false))
        {
            return await BoundedStreamBody
                .ReadTextAsync(stream, maxBytes, cancellationToken, declared)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads at most <paramref name="maxBytes"/> bytes and parses them as one JSON document (nesting depth 64).
    /// </summary>
    /// <param name="content">The body. Its stream is disposed.</param>
    /// <param name="maxBytes">Largest accepted body, in bytes.</param>
    /// <param name="requireObject">Refuse a document whose root is not a JSON object.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async Task<BoundedJsonBody> ReadJsonAsync(
        HttpContent content,
        int maxBytes,
        bool requireObject,
        CancellationToken cancellationToken)
    {
        var (stream, declared) = await OpenAsync(content, maxBytes, cancellationToken).ConfigureAwait(false);
        if (stream is null)
        {
            return new BoundedJsonBody(BoundedBodyStatus.TooLarge, null);
        }

        await using (stream.ConfigureAwait(false))
        {
            return await BoundedStreamBody
                .ReadJsonAsync(stream, maxBytes, requireObject, cancellationToken, declared)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Validates the arguments, refuses an oversized declared length without touching the stream, and hands
    /// back the body stream plus that (untrusted) length for early refusal and buffer sizing.
    /// </summary>
    private static async Task<(Stream? Stream, long? Declared)> OpenAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);

        var declared = content.Headers.ContentLength;
        if (declared > maxBytes)
        {
            return (null, null);
        }

        return (await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), declared);
    }
}
