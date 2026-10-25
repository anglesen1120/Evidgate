using System.Net;
using Evidgate.Modules.Identity.Login;
using Evidgate.Modules.Identity.Login.GitHub;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Login.GitHub;

/// <summary>
/// 02.3.1-T1.3 / AC8: both GitHub halves bound the response BODY, not only the headers. With
/// <c>ResponseHeadersRead</c> the client timeout stops at the headers, so a body that stalls after them
/// must still land on <c>unavailable</c> instead of holding the request, the pooled connection and the
/// caller's slot.
/// </summary>
/// <remarks>
/// Pure: both clients are <see cref="GitHubTestHttpHandler"/> stand-ins. The provider is built directly, so
/// no resilience pipeline is involved here (see <see cref="GitHubResiliencePipelineTests"/> for that).
/// </remarks>
// The assembly fixture of this project (DatabaseFixture) needs Docker, so the smoke build, which has none, skips Category=Docker.
[Trait("Category", "Docker")]
public sealed class GitHubTimeoutTests
{
    private static readonly TimeSpan TestDeadline = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task GitHubProvider_StalledTokenBody_IsProviderUnavailable_UnderItsOwnDeadline()
    {
        // Headers arrive, then the body never completes: a token response the client timeout cannot stop.
        var harness = new GitHubProviderHarness(
            token: (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StalledContent() }),
            settings: GitHubTestHarness.Options(httpTimeoutSeconds: 1));

        var result = await harness.Provider.CompleteAsync(GitHubTestHarness.Callback(), TestContext.Current.CancellationToken)
            .WaitAsync(TestDeadline, TestContext.Current.CancellationToken);

        result.Identity.ShouldBeNull();
        result.Failure.ShouldBe(
            LoginFailure.ProviderUnavailable,
            "a stalled body is the provider being unavailable, not a hang and not a 500");
        harness.TokenHandler.Calls.ShouldBe(1, "the authorization code is single-use, so the exchange is never retried");
        harness.ApiHandler.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task GitHubProvider_StalledUserBody_IsProviderUnavailable_UnderItsOwnDeadline()
    {
        var harness = new GitHubProviderHarness(
            api: (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StalledContent() }),
            settings: GitHubTestHarness.Options(httpTimeoutSeconds: 1));

        var result = await harness.Provider.CompleteAsync(GitHubTestHarness.Callback(), TestContext.Current.CancellationToken)
            .WaitAsync(TestDeadline, TestContext.Current.CancellationToken);

        result.Identity.ShouldBeNull();
        result.Failure.ShouldBe(LoginFailure.ProviderUnavailable);
    }

    [Fact]
    public async Task GitHubProvider_StalledEmailsBody_IsProviderUnavailable_UnderItsOwnDeadline()
    {
        var harness = new GitHubProviderHarness(
            api: (request, _) => Task.FromResult(
                request.RequestUri?.AbsolutePath.EndsWith("/user/emails", StringComparison.Ordinal) == true
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StalledContent() }
                    : GitHubTestHarness.DefaultApi(request)),
            settings: GitHubTestHarness.Options(httpTimeoutSeconds: 1));

        var result = await harness.Provider.CompleteAsync(GitHubTestHarness.Callback(), TestContext.Current.CancellationToken)
            .WaitAsync(TestDeadline, TestContext.Current.CancellationToken);

        result.Identity.ShouldBeNull();
        result.Failure.ShouldBe(LoginFailure.ProviderUnavailable);
    }

    /// <summary>A response body that arrives as a stream which only ends when the reader gives up.</summary>
    private sealed class StalledContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new NotSupportedException("the stalled body is only ever read as a stream");

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new StalledStream());

        // False is the point: no Content-Length, so the declared-size cap cannot fire before the read.
        protected override bool TryComputeLength(out long length)
        {
            length = 0;

            return false;
        }
    }

    /// <summary>A stream whose reads never produce a byte; it honours the reader's cancellation token.</summary>
    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        // Never used by a reader that only pulls bytes, and a getter must not throw (CA1065).
        public override long Length => 0;

        public override long Position
        {
            get => 0;
            set
            {
            }
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException("the stalled body is only ever read asynchronously");

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
