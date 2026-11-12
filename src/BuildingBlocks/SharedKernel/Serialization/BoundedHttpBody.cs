using System.Text;
using System.Text.Json;
using System.Text.Unicode;

namespace Evidgate.BuildingBlocks.SharedKernel.Serialization;

/// <summary>Why a bounded JSON read did not produce a document.</summary>
public enum BoundedBodyStatus
{
    /// <summary>The body was read within the cap and parsed.</summary>
    Ok,

    /// <summary>The declared or the streamed length exceeded the cap; nothing past the cap was buffered.</summary>
    TooLarge,

    /// <summary>The body was empty, not JSON, too deeply nested or (when required) not an object.</summary>
    Invalid,
}

/// <summary>The outcome of <see cref="BoundedHttpBody.ReadJsonAsync"/>; the document is the caller's to dispose.</summary>
/// <param name="Status">Whether a document was produced.</param>
/// <param name="Document">The parsed document when <paramref name="Status"/> is <see cref="BoundedBodyStatus.Ok"/>.</param>
public readonly record struct BoundedJsonBody(BoundedBodyStatus Status, JsonDocument? Document);

/// <summary>
/// Reads an outbound HTTP client's response body under a byte cap. The cap is checked against
/// <c>Content-Length</c> before any read and again while streaming, so a chunked body or a header that
/// lies is refused once a read crosses the cap instead of being buffered.
/// </summary>
public static class BoundedHttpBody
{
    private const int ChunkBytes = 8192;
    private const int MaxJsonDepth = 64;

    /// <summary>Reads at most <paramref name="maxBytes"/> bytes; returns null when the body is longer.</summary>
    /// <param name="content">The body. Its stream is disposed.</param>
    /// <param name="maxBytes">Largest accepted body, in bytes.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async Task<byte[]?> ReadBytesAsync(HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        var buffer = await ReadBufferAsync(content, maxBytes, cancellationToken).ConfigureAwait(false);
        return buffer?.ToArray();
    }

    /// <summary>Reads at most <paramref name="maxBytes"/> bytes as UTF-8 text (invalid bytes become U+FFFD); null when longer.</summary>
    /// <param name="content">The body. Its stream is disposed.</param>
    /// <param name="maxBytes">Largest accepted body, in bytes.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async Task<string?> ReadTextAsync(HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        var buffer = await ReadBufferAsync(content, maxBytes, cancellationToken).ConfigureAwait(false);
        if (buffer is null)
        {
            return null;
        }

        await using (buffer.ConfigureAwait(false))
        {
            return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
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
        var buffer = await ReadBufferAsync(content, maxBytes, cancellationToken).ConfigureAwait(false);
        if (buffer is null)
        {
            return new BoundedJsonBody(BoundedBodyStatus.TooLarge, null);
        }

        await using (buffer.ConfigureAwait(false))
        {
            var bytes = new ReadOnlyMemory<byte>(buffer.GetBuffer(), 0, (int)buffer.Length);

            // The parser defers string transcoding, so a later GetString on invalid UTF-8 would throw
            // outside the caller's refusal path; refuse the body here instead.
            if (!Utf8.IsValid(bytes.Span))
            {
                return new BoundedJsonBody(BoundedBodyStatus.Invalid, null);
            }

            try
            {
                var document = JsonDocument.Parse(
                    bytes,
                    new JsonDocumentOptions { MaxDepth = MaxJsonDepth });

                if (!requireObject || document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    return new BoundedJsonBody(BoundedBodyStatus.Ok, document);
                }

                document.Dispose();
                return new BoundedJsonBody(BoundedBodyStatus.Invalid, null);
            }
            catch (JsonException)
            {
                return new BoundedJsonBody(BoundedBodyStatus.Invalid, null);
            }
        }
    }

    private static int InitialCapacity(HttpContent content, int maxBytes) =>
        content.Headers.ContentLength is >= 0 and var declared
            ? (int)Math.Min(declared, maxBytes)
            : Math.Min(maxBytes, 4096);

    private static async Task<MemoryStream?> ReadBufferAsync(HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);

        if (content.Headers.ContentLength is { } declared && declared > maxBytes)
        {
            return null;
        }

        var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var buffer = new MemoryStream(InitialCapacity(content, maxBytes));
            var chunk = new byte[ChunkBytes];
            try
            {
                while (true)
                {
                    var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        return buffer;
                    }

                    if (buffer.Length + read > maxBytes)
                    {
                        await buffer.DisposeAsync().ConfigureAwait(false);
                        return null;
                    }

                    await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
            }
            catch
            {
                await buffer.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
    }
}
