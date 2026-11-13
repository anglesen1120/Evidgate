using System.Net;
using System.Text;
using Evidgate.Modules.Healing.Application;
using Evidgate.Modules.Healing.Contracts;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Hosts.IntegrationTests;

/// <summary>
/// How the healing adapter treats the provider's response body: one answer within the byte cap is parsed, and anything
/// else (too large by header or by stream, not a JSON object, never finished) is an error result with no content and
/// nothing spent, never an exception and never a partial parse.
/// </summary>
public sealed class HealingLlmResponseBodyTests
{
    private const int Cap = HealingLlmHttpClients.MaxResponseBytes;

    [Fact]
    public async Task A_Valid_Envelope_Is_Parsed()
    {
        var result = await InvokeAsync(Json("""{"content":{"checklist":["a"]},"tokens":7,"cost":0.25}"""));

        result.Status.ShouldBe(HealingLlmStatuses.Ok);
        result.Content.ShouldBe("""{"checklist":["a"]}""");
        result.Tokens.ShouldBe(7);
        result.Cost.ShouldBe(0.25m);
    }

    [Fact]
    public async Task A_Body_Exactly_At_The_Cap_Is_Accepted()
    {
        const string Prefix = "{\"content\":\"";
        const string Suffix = "\"}";
        var filler = new string('x', Cap - Prefix.Length - Suffix.Length);
        var body = Prefix + filler + Suffix;
        Encoding.UTF8.GetByteCount(body).ShouldBe(Cap);

        var result = await InvokeAsync(Json(body));

        result.Status.ShouldBe(HealingLlmStatuses.Ok);
        result.Content!.Length.ShouldBe(filler.Length + 1);
    }

    [Fact]
    public async Task A_Declared_Length_Above_The_Cap_Is_Refused_Without_Reading()
    {
        var body = new ThrowingStream();
        var content = new StreamContent(body);
        content.Headers.ContentLength = Cap + 1;

        var result = await InvokeAsync(content);

        result.Status.ShouldBe(HealingLlmStatuses.Error);
        result.Content.ShouldBeNull();
        body.Reads.ShouldBe(0, "a body the header already refuses is never opened");
    }

    [Fact]
    public async Task A_Streamed_Body_Above_The_Cap_Is_Refused_Even_Without_A_Declared_Length()
    {
        var result = await InvokeAsync(new UnsizedContent(new string('x', Cap + 1)));

        result.Status.ShouldBe(HealingLlmStatuses.Error);
        result.Content.ShouldBeNull();
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    [InlineData("""{"content":""")]
    public async Task A_Body_That_Is_Not_A_Json_Object_Is_An_Error_Result(string body)
    {
        var result = await InvokeAsync(Json(body));

        result.Status.ShouldBe(HealingLlmStatuses.Error);
        result.Content.ShouldBeNull();
        result.Tokens.ShouldBe(0);
        result.Cost.ShouldBe(0m);
    }

    [Fact]
    public async Task A_Body_That_Never_Finishes_Is_Aborted_By_The_Attempt_Budget()
    {
        var content = new StreamContent(new HangingStream());

        var result = await InvokeAsync(content, new HealingLlmOptions { AttemptTimeout = TimeSpan.FromMilliseconds(100) });

        result.Status.ShouldBe(HealingLlmStatuses.Timeout);
        result.Content.ShouldBeNull();
    }

    private static StringContent Json(string body) => new StringContent(body, Encoding.UTF8, "application/json");

    private static async Task<HealingLlmResult> InvokeAsync(HttpContent responseBody, HealingLlmOptions? options = null)
    {
        using var client = new HttpClient(new Fixed(responseBody)) { BaseAddress = new Uri("https://api.provider.example/v1/") };
        var provider = new HttpHealingLlmProvider(client, Options.Create(options ?? new HealingLlmOptions()));

        return await provider.InvokeAsync(
            new HealingLlmRequest(
                Guid.NewGuid(),
                HealingLlmPurposes.AuthoringGeneration,
                null,
                null,
                "m",
                "v1",
                "{}",
                null),
            TestContext.Current.CancellationToken);
    }

    private sealed class Fixed(HttpContent content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }

    /// <summary>A body with no <c>Content-Length</c>, as a chunked response arrives.</summary>
    private sealed class UnsizedContent(string text) : HttpContent
    {
        private readonly byte[] _bytes = Encoding.UTF8.GetBytes(text);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(_bytes, 0, _bytes.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class ThrowingStream : Stream
    {
        internal int Reads { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Reads++;
            throw new InvalidOperationException("the body must not be read");
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Reads++;
            throw new InvalidOperationException("the body must not be read");
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A body that sends nothing and waits for the caller's deadline.</summary>
    private sealed class HangingStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
