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
        result.Content.ShouldBe(filler);
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
    public async Task A_Streamed_Body_Above_The_Cap_Is_Refused_Without_Reading_It_All()
    {
        var body = new EndlessEnvelopeStream();

        var result = await InvokeAsync(new StreamContent(body));

        result.Status.ShouldBe(HealingLlmStatuses.Error);
        result.Content.ShouldBeNull();
        body.BytesRead.ShouldBeLessThan(Cap + (64 * 1024), "the read stops shortly after the cap instead of draining the stream");
    }

    [Fact]
    public async Task A_Body_Longer_Than_Its_Declared_Length_Is_Refused()
    {
        var oversize = "{\"content\":\"" + new string('x', Cap) + "\"}";
        var content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(oversize)));
        content.Headers.ContentLength = 10;

        var result = await InvokeAsync(content);

        result.Status.ShouldBe(HealingLlmStatuses.Error);
        result.Content.ShouldBeNull();
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    [InlineData("""{"content":""")]
    [InlineData("""{"content":"\ud800"}""")]
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

    [Fact]
    public async Task A_Send_That_Never_Answers_Is_Aborted_By_The_Total_Budget()
    {
        var options = new HealingLlmOptions { AttemptTimeout = TimeSpan.FromMilliseconds(100) };
        using var guard = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        guard.CancelAfter(options.TotalTimeout + TimeSpan.FromSeconds(10));
        using var client = new HttpClient(new NeverAnswers()) { BaseAddress = new Uri("https://api.provider.example/v1/") };
        var provider = new HttpHealingLlmProvider(client, Options.Create(options));

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var result = await provider.InvokeAsync(
            new HealingLlmRequest(Guid.NewGuid(), HealingLlmPurposes.AuthoringGeneration, null, null, "m", "v1", "{}", null),
            guard.Token);
        clock.Stop();

        result.Status.ShouldBe(HealingLlmStatuses.Timeout);
        result.Content.ShouldBeNull();
        clock.Elapsed.ShouldBeLessThan(options.TotalTimeout + TimeSpan.FromSeconds(5), "the deadline covers the send, not only the body");
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

    private sealed class NeverAnswers : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }

    /// <summary>
    /// A valid envelope that never ends: <c>{"content":"</c> followed by endless <c>x</c>, with no length, so only the
    /// streaming byte count can stop it.
    /// </summary>
    private sealed class EndlessEnvelopeStream : Stream
    {
        private static readonly byte[] Prefix = Encoding.UTF8.GetBytes("{\"content\":\"");

        internal long BytesRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => Fill(buffer.AsSpan(offset, count));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Fill(buffer.Span));

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Fill(Span<byte> target)
        {
            for (var i = 0; i < target.Length; i++)
            {
                target[i] = BytesRead < Prefix.Length ? Prefix[(int)BytesRead] : (byte)'x';
                BytesRead++;
            }

            return target.Length;
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
