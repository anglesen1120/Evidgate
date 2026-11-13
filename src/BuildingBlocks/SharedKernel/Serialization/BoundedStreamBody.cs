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

/// <summary>The outcome of <see cref="BoundedStreamBody.ReadJsonAsync"/>; the document is the caller's to dispose.</summary>
/// <param name="Status">Whether a document was produced.</param>
/// <param name="Document">The parsed document when <paramref name="Status"/> is <see cref="BoundedBodyStatus.Ok"/>.</param>
public readonly record struct BoundedJsonBody(BoundedBodyStatus Status, JsonDocument? Document);

/// <summary>
/// Reads a stream under a byte cap, checking an optional declared length before reading and
/// counting streamed bytes so an inaccurate or missing length cannot bypass the cap.
/// The caller owns the input stream.
/// </summary>
public static class BoundedStreamBody
{
    private const int ChunkBytes = 8192;
    private const int MaxJsonDepth = 64;

    /// <summary>Reads at most <paramref name="maxBytes"/> bytes; returns null when the body is longer.</summary>
    /// <param name="stream">The body stream, which remains open.</param>
    /// <param name="maxBytes">Largest accepted body, in bytes.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <param name="declaredLength">Optional untrusted length used for early refusal and buffer sizing.</param>
    public static async Task<byte[]?> ReadBytesAsync(
        Stream stream, int maxBytes, CancellationToken cancellationToken, long? declaredLength = null)
    {
        using var buffer = await ReadBufferAsync(stream, maxBytes, declaredLength, cancellationToken).ConfigureAwait(false);
        return buffer?.ToArray();
    }

    /// <summary>Reads at most <paramref name="maxBytes"/> bytes as UTF-8 text (invalid bytes become U+FFFD); null when longer.</summary>
    /// <param name="stream">The body stream, which remains open.</param>
    /// <param name="maxBytes">Largest accepted body, in bytes.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <param name="declaredLength">Optional untrusted length used for early refusal and buffer sizing.</param>
    public static async Task<string?> ReadTextAsync(
        Stream stream, int maxBytes, CancellationToken cancellationToken, long? declaredLength = null)
    {
        var buffer = await ReadBufferAsync(stream, maxBytes, declaredLength, cancellationToken).ConfigureAwait(false);
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
    /// <param name="stream">The body stream, which remains open.</param>
    /// <param name="maxBytes">Largest accepted body, in bytes.</param>
    /// <param name="requireObject">Refuse a document whose root is not a JSON object.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <param name="declaredLength">Optional untrusted length used for early refusal and buffer sizing.</param>
    public static async Task<BoundedJsonBody> ReadJsonAsync(
        Stream stream,
        int maxBytes,
        bool requireObject,
        CancellationToken cancellationToken,
        long? declaredLength = null)
    {
        var buffer = await ReadBufferAsync(stream, maxBytes, declaredLength, cancellationToken).ConfigureAwait(false);
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

    private static int InitialCapacity(long? declaredLength, int maxBytes) =>
        declaredLength is >= 0 and var declared
            ? (int)Math.Min(declared, maxBytes)
            : Math.Min(maxBytes, 4096);

    private static async Task<MemoryStream?> ReadBufferAsync(
        Stream stream, int maxBytes, long? declaredLength, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);

        if (declaredLength is { } declared && declared > maxBytes)
        {
            return null;
        }

        var buffer = new MemoryStream(InitialCapacity(declaredLength, maxBytes));
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
