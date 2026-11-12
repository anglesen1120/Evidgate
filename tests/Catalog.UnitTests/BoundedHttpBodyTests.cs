using System.Net;
using Evidgate.BuildingBlocks.SharedKernel.Serialization;
using Shouldly;
using Xunit;

namespace Catalog.UnitTests;

public sealed class BoundedHttpBodyTests
{
    private const int Cap = 64;

    [Fact]
    public async Task Body_Exactly_At_The_Cap_Is_Read()
    {
        using var content = new StreamContent(new MemoryStream(Json(Cap)));
        var bytes = await BoundedHttpBody.ReadBytesAsync(content, Cap, TestContext.Current.CancellationToken);

        bytes.ShouldNotBeNull();
        bytes.Length.ShouldBe(Cap);
    }

    [Fact]
    public async Task Body_One_Byte_Over_The_Cap_Is_Refused()
    {
        using var content = new StreamContent(new MemoryStream(Json(Cap + 1)));
        content.Headers.ContentLength = null;

        var result = await BoundedHttpBody.ReadJsonAsync(content, Cap, requireObject: false, TestContext.Current.CancellationToken);

        result.Status.ShouldBe(BoundedBodyStatus.TooLarge);
        result.Document.ShouldBeNull();
    }

    [Fact]
    public async Task Chunked_Body_Without_Content_Length_Is_Counted_While_Streaming()
    {
        using var content = new StreamContent(new TrickleStream(Cap + 10, chunk: 7));
        content.Headers.ContentLength.ShouldBeNull();

        var bytes = await BoundedHttpBody.ReadBytesAsync(content, Cap, TestContext.Current.CancellationToken);

        bytes.ShouldBeNull();
    }

    [Fact]
    public async Task Declared_Length_Over_The_Cap_Is_Refused_Without_Reading()
    {
        var stream = new TrickleStream(10, chunk: 10);
        using var content = new StreamContent(stream);
        content.Headers.ContentLength = Cap + 1;

        var bytes = await BoundedHttpBody.ReadBytesAsync(content, Cap, TestContext.Current.CancellationToken);

        bytes.ShouldBeNull();
        stream.Reads.ShouldBe(0);
    }

    [Fact]
    public async Task Lying_Small_Content_Length_Does_Not_Lift_The_Cap()
    {
        using var content = new StreamContent(new TrickleStream(Cap * 4, chunk: 16));
        content.Headers.ContentLength = 5;

        var result = await BoundedHttpBody.ReadJsonAsync(content, Cap, requireObject: false, TestContext.Current.CancellationToken);

        result.Status.ShouldBe(BoundedBodyStatus.TooLarge);
    }

    [Fact]
    public async Task Cancellation_Is_Honoured()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        using var content = new StreamContent(new TrickleStream(10, chunk: 1));

        await Should.ThrowAsync<OperationCanceledException>(
            () => BoundedHttpBody.ReadBytesAsync(content, Cap, cts.Token));
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    [InlineData("42")]
    public async Task Non_Object_Root_Is_Invalid_Only_When_An_Object_Is_Required(string body)
    {
        using var strict = new StringContent(body);
        var refused = await BoundedHttpBody.ReadJsonAsync(strict, Cap, requireObject: true, TestContext.Current.CancellationToken);
        refused.Status.ShouldBe(BoundedBodyStatus.Invalid);
        refused.Document.ShouldBeNull();

        using var lax = new StringContent(body);
        var accepted = await BoundedHttpBody.ReadJsonAsync(lax, Cap, requireObject: false, TestContext.Current.CancellationToken);
        accepted.Status.ShouldBe(BoundedBodyStatus.Ok);
        accepted.Document!.Dispose();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{\"a\":")]
    public async Task Empty_Or_Malformed_Body_Is_Invalid(string body)
    {
        using var content = new StringContent(body);

        var result = await BoundedHttpBody.ReadJsonAsync(content, Cap, requireObject: false, TestContext.Current.CancellationToken);

        result.Status.ShouldBe(BoundedBodyStatus.Invalid);
    }

    [Fact]
    public async Task Object_Body_Parses()
    {
        using var content = new StringContent("{\"id\":7}");

        var result = await BoundedHttpBody.ReadJsonAsync(content, Cap, requireObject: true, TestContext.Current.CancellationToken);

        result.Status.ShouldBe(BoundedBodyStatus.Ok);
        using var document = result.Document!;
        document.RootElement.GetProperty("id").GetInt32().ShouldBe(7);
    }

    [Fact]
    public async Task Nesting_Deeper_Than_Sixty_Four_Is_Invalid()
    {
        using var content = new StringContent(new string('[', 65) + new string(']', 65));

        var result = await BoundedHttpBody.ReadJsonAsync(content, 1024, requireObject: false, TestContext.Current.CancellationToken);

        result.Status.ShouldBe(BoundedBodyStatus.Invalid);
    }

    private static byte[] Json(int length)
    {
        var bytes = new byte[length];
        Array.Fill(bytes, (byte)' ');
        bytes[0] = (byte)'{';
        bytes[^1] = (byte)'}';
        return bytes;
    }

    /// <summary>A forward-only stream with no length that hands out at most <c>chunk</c> bytes per read.</summary>
    private sealed class TrickleStream(int total, int chunk) : Stream
    {
        private int _left = total;

        public int Reads { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Fill(buffer.AsSpan(offset, count));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<int>(Fill(buffer.Span));
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Fill(Span<byte> target)
        {
            Reads++;
            var n = Math.Min(Math.Min(_left, chunk), target.Length);
            target[..n].Fill((byte)' ');
            _left -= n;
            return n;
        }
    }
}
