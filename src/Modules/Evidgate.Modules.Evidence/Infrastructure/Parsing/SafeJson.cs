using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Evidence.Contracts;

namespace Evidgate.Modules.Evidence.Infrastructure.Parsing;

/// <summary>
/// The JSON arm of the parser profile (06.3.1 AC2; 00 §5.7 S5). The bytes are bounded into one buffer first
/// (never more than the kind's cap, even when the stream lies about <see cref="Stream.Length"/>), a UTF-8 BOM
/// is dropped — the media sniffer already accepts one — and the document is parsed over that buffer with the
/// profile's depth cap and with comments and trailing commas disallowed.
/// <para>
/// JSON has no external-entity or DTD surface, so the depth and byte caps plus the wall clock are the whole
/// guard; <see cref="JsonDocument"/> is a read-only DOM over the profile's own buffer and never touches the
/// file system or the network.
/// </para>
/// </summary>
internal static class SafeJson
{
    private static readonly JsonDocumentOptions Options = new()
    {
        MaxDepth = ParsingLimits.MaxDepth,
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
    };

    /// <summary>
    /// Parses one JSON payload against the profile and returns the document, which the caller disposes.
    /// </summary>
    /// <param name="content">The untrusted bytes; the caller keeps ownership of the stream.</param>
    /// <param name="maxBytes">The per-kind byte cap.</param>
    /// <param name="cancellationToken">The caller's cancellation; a caller cancellation is never swallowed.</param>
    /// <returns>The parsed document, or the profile failure.</returns>
    public static Result<JsonDocument> Parse(Stream content, long maxBytes, CancellationToken cancellationToken) =>
        Parse(content, maxBytes, ParsingLimits.ParseTimeout, cancellationToken);

    /// <summary>
    /// The profile with an explicit timeout, so a test can prove the wall-clock guard without waiting 30 s.
    /// </summary>
    /// <param name="content">The untrusted bytes; the caller keeps ownership of the stream.</param>
    /// <param name="maxBytes">The per-kind byte cap.</param>
    /// <param name="timeout">The wall-clock cap for this parse.</param>
    /// <param name="cancellationToken">The caller's cancellation; a caller cancellation is never swallowed.</param>
    /// <returns>The parsed document, or the profile failure.</returns>
    internal static Result<JsonDocument> Parse(
        Stream content,
        long maxBytes,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var token = timeoutSource.Token;

        byte[]? buffer = null;
        var offset = 0;
        var length = 0;

        try
        {
            (buffer, length) = BoundedContent.ReadContiguous(content, maxBytes, token);

            token.ThrowIfCancellationRequested();

            // A UTF-8 BOM is dropped by offset, not by copying: the sniffer already accepts one.
            offset = length >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF ? 3 : 0;

            // JsonDocument does not reject every ill-formed UTF-8 sequence inside strings (an encoded surrogate
            // such as ED A0 80 is accepted), so the bytes are validated strictly first.
            if (!System.Text.Unicode.Utf8.IsValid(buffer.AsSpan(offset, length - offset)))
            {
                return Result.Failure<JsonDocument>(ParsingFailure.For(ParsingFailureReason.Malformed));
            }

            return Result.Success(
                JsonDocument.Parse(new ReadOnlyMemory<byte>(buffer, offset, length - offset), Options));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The caller's own cancellation propagates; only the profile's timeout becomes a failure value.
            return Result.Failure<JsonDocument>(ParsingFailure.For(ParsingFailureReason.Timeout));
        }
        catch (InvalidDataException)
        {
            // The bounded read refused the content: the stream is longer than the kind's cap.
            return Result.Failure<JsonDocument>(ParsingFailure.For(ParsingFailureReason.SizeExceeded));
        }
        catch (JsonException)
        {
            // JsonDocument reports a too-deep document through the same exception type as a syntax error, so the
            // reason is established by counting depth itself (cold path only: the parse already failed).
            var reason = buffer is not null && ExceedsDepth(buffer.AsSpan(offset, length - offset))
                ? ParsingFailureReason.DepthExceeded
                : ParsingFailureReason.Malformed;

            return Result.Failure<JsonDocument>(ParsingFailure.For(reason));
        }
    }

    /// <summary>
    /// True when the payload nests deeper than <see cref="ParsingLimits.MaxDepth"/>, counted over the same bytes
    /// with a <see cref="Utf8JsonReader"/>. Runs only after the document parse has already failed, so the happy
    /// path pays nothing for it; the reader's own depth cap is four times the profile's, which is enough to tell
    /// "deeper than the profile allows" from "not JSON at all".
    /// </summary>
    private static bool ExceedsDepth(ReadOnlySpan<byte> payload)
    {
        var options = new JsonReaderOptions
        {
            MaxDepth = ParsingLimits.MaxDepth * 4,
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
        };

        var deepest = 0;

        try
        {
            var reader = new Utf8JsonReader(payload, options);

            while (reader.Read())
            {
                // Only containers count towards depth: JsonDocument's own cap is checked when a container is
                // pushed, so a payload with the profile's depth plus a syntax error is a syntax error.
                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray
                    && reader.CurrentDepth > deepest)
                {
                    deepest = reader.CurrentDepth;
                }

                if (deepest >= ParsingLimits.MaxDepth)
                {
                    return true;
                }
            }
        }
        catch (JsonException)
        {
            // The probe met the same syntax problem, or its own depth cap: what it saw so far decides.
        }

        return deepest >= ParsingLimits.MaxDepth;
    }
}
