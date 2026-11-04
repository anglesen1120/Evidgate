using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Microsoft.AspNetCore.Http;

namespace Evidgate.Modules.Evidence.Endpoints;

/// <summary>
/// The refusal and the reader of the module's JSON routes whose body must be bounded before it is parsed. One
/// definition of the 00 §9.2 413 <c>payload_too_large</c> envelope, because the routes answer the identical shape
/// and it echoes nothing of the body.
/// </summary>
internal static class CappedRequestBody
{
    /// <summary>413 <c>payload_too_large</c> for a body over the route's cap.</summary>
    internal static Error TooLarge { get; } = new(
        ApiErrorCode.PayloadTooLarge,
        "the request body is too large",
        StatusCodes.Status413PayloadTooLarge);

    /// <summary>
    /// Maps a failed body read to its envelope: <see cref="TooLarge"/> when the read was refused for the cap, and
    /// <c>request_invalid</c> with Kestrel's own status for any other bad-body failure (a truncated chunked body).
    /// </summary>
    /// <param name="exception">The exception the read raised.</param>
    /// <returns>The error the endpoint returns unchanged.</returns>
    internal static Error Unreadable(BadHttpRequestException exception) =>
        exception.StatusCode == StatusCodes.Status413PayloadTooLarge
            ? TooLarge
            : new Error(ApiErrorCode.RequestInvalid, "the request body could not be read", exception.StatusCode);
}

/// <summary>
/// Counts every byte read through the request body and refuses the first one PAST <paramref name="limit"/> with the
/// same 413 <see cref="BadHttpRequestException"/> Kestrel raises for its own body-size limit, so the caller's catch
/// maps it to <see cref="CappedRequestBody.TooLarge"/>. A declared <c>Content-Length</c> cannot bound a chunked or
/// length-less body, which is why the count is taken while reading; nothing is buffered, a body of exactly the limit
/// is read whole (one byte past it is always requested, so only the byte that passes the limit refuses), and no
/// body byte is ever copied out, logged or echoed.
/// </summary>
/// <param name="inner">The request body Kestrel hands the endpoint.</param>
/// <param name="limit">The largest body the route accepts, in bytes.</param>
internal sealed class CappedBodyStream(Stream inner, int limit) : Stream
{
    /// <summary>The bytes read through this stream so far; never above <paramref name="limit"/> when a read returns.</summary>
    private long read;

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => read;
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        var taken = inner.Read(buffer, offset, Room(count));
        Count(taken);
        return taken;
    }

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        var taken = inner.Read(buffer[..Room(buffer.Length)]);
        Count(taken);
        return taken;
    }

    /// <inheritdoc />
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        ReadCoreAsync(buffer, cancellationToken);

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        return ReadCoreAsync(buffer.AsMemory(offset, Room(count)), cancellationToken).AsTask();
    }

    /// <inheritdoc />
    public override void Flush()
    {
        // Read-only stream: nothing to flush.
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <summary>
    /// How many bytes this read may take without passing the limit: the requested count, capped at the one byte
    /// that would prove the body is over it. A body of exactly the limit therefore ends in a normal
    /// end-of-stream read instead of a refusal.
    /// </summary>
    /// <param name="requested">The count the caller asked for.</param>
    private int Room(int requested)
    {
        var room = (limit + 1) - read;
        return requested < room ? requested : (int)room;
    }

    /// <summary>Counts a completed read and refuses the moment it passes the limit.</summary>
    /// <param name="taken">The bytes the inner stream returned.</param>
    private void Count(int taken)
    {
        read += taken;

        if (read > limit)
        {
            throw new BadHttpRequestException("the request body is too large", StatusCodes.Status413PayloadTooLarge);
        }
    }

    private async ValueTask<int> ReadCoreAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var taken = await inner.ReadAsync(buffer[..Room(buffer.Length)], cancellationToken).ConfigureAwait(false);
        Count(taken);
        return taken;
    }
}
