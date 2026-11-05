namespace Tenancy.IntegrationTests;

/// <summary>
/// A stream that holds more bytes than the cap while reporting <c>Length</c> 0: shared by the
/// profile tests to prove the size cap counts bytes actually read, never a length the caller controls.
/// </summary>
internal sealed class LyingLengthStream(byte[] content) : Stream
{
    private int offset;

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => 0;

    public override long Position
    {
        get => offset;
        set => offset = (int)value;
    }

    public override int Read(byte[] buffer, int offsetInBuffer, int count)
    {
        var take = Math.Min(count, content.Length - offset);
        if (take <= 0)
        {
            return 0;
        }

        content.AsSpan(offset, take).CopyTo(buffer.AsSpan(offsetInBuffer));
        offset += take;

        return take;
    }

    public override void Flush()
    {
        // Read-only.
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offsetInBuffer, int count) => throw new NotSupportedException();
}

/// <summary>
/// A stream that hands out small reads slowly, so the profile's wall-clock guard can be proven without waiting
/// for the real timeout: shared by the profile tests.
/// </summary>
internal sealed class SlowStream(byte[] content, int chunkBytes, TimeSpan delayPerRead) : Stream
{
    private int offset;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => content.Length;

    public override long Position
    {
        get => offset;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offsetInBuffer, int count)
    {
        Thread.Sleep(delayPerRead);

        var take = Math.Min(Math.Min(count, chunkBytes), content.Length - offset);
        if (take <= 0)
        {
            return 0;
        }

        content.AsSpan(offset, take).CopyTo(buffer.AsSpan(offsetInBuffer));
        offset += take;

        return take;
    }

    public override void Flush()
    {
        // Read-only.
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offsetInBuffer, int count) => throw new NotSupportedException();
}

/// <summary>
/// A stream whose every read fails with <see cref="InvalidDataException"/> — the shape a corrupt
/// <c>GZipStream</c> presents: shared by the profile tests to prove the profile classifies the caller's
/// own invalid-data failure as malformed content, never as a breached size cap.
/// </summary>
internal sealed class InvalidDataStream : Stream
{
    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new InvalidDataException("simulated corrupt content");

    public override void Flush()
    {
        // Read-only.
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
