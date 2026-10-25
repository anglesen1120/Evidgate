using System.Net;
using Evidgate.Modules.Identity.Login;
using Evidgate.Modules.Identity.Login.Google;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Login.Google;

/// <summary>
/// 02.3.1-T1.2 / AC8 (m1, m2): the two deadlines the Google half needed.
/// <list type="bullet">
/// <item>
/// The token exchange bounds the response BODY, not only the headers: with <c>ResponseHeadersRead</c> the
/// client timeout stops at the headers, so a body that stalls after them must still land on
/// <c>unavailable</c> instead of holding the request, the pooled connection and the caller's slot.
/// </item>
/// <item>
/// The JWKS fetch is shared single-flight work and must not run under the first caller's request token:
/// one client disconnecting cannot cancel the document the other waiters are holding the gate for.
/// </item>
/// </list>
/// </summary>
/// <remarks>
/// Pure: both endpoints are <see cref="GoogleTestHttpHandler"/> stand-ins. The provider and the cache are
/// built directly, so no resilience pipeline is involved here (see
/// <see cref="GoogleResiliencePipelineTests"/> for that).
/// </remarks>
public sealed class GoogleTimeoutTests
{
    private static readonly TimeSpan TestDeadline = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task GoogleProvider_StalledTokenBody_IsProviderUnavailable_UnderItsOwnDeadline()
    {
        // Headers arrive, then the body never completes: a token response the client timeout cannot stop.
        var handler = new GoogleTestHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StalledContent(),
        }));

        var provider = Provider(handler, Settings());

        var result = await provider.CompleteAsync(Callback(), TestContext.Current.CancellationToken).WaitAsync(TestDeadline, TestContext.Current.CancellationToken);

        result.Identity.ShouldBeNull();
        result.Failure.ShouldBe(
            LoginFailure.ProviderUnavailable,
            "a stalled body is the provider being unavailable, not a hang and not a 500");
        handler.Calls.ShouldBe(1, "the authorization code is single-use, so the exchange is never retried");
    }

    [Fact]
    public async Task GoogleJwksCache_FirstCallerCancelling_DoesNotCancelTheSharedFetch()
    {
        var keys = new GoogleTestKeys();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var handler = new GoogleTestHttpHandler(async (_, cancellationToken) =>
        {
            entered.TrySetResult();

            // Waits on the FETCH's token. It must survive the first caller's cancellation: otherwise this
            // wait throws as soon as that caller disconnects, and the key is never published.
            await release.Task.WaitAsync(cancellationToken);

            return GoogleTestHttpHandler.Json(keys.JwksJson());
        });

        using var cache = new HttpGoogleJwksCache(
            new HttpClient(handler),
            Options.Create(Settings()),
            new MutableTimeProvider(GoogleTestHarness.Now),
            NullLogger<HttpGoogleJwksCache>.Instance);

        var cancellationToken = TestContext.Current.CancellationToken;

        using var firstCaller = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var first = cache.GetKeyAsync(keys.KeyId, firstCaller.Token).AsTask();

        // Only cancel once the fetch is really in flight; before that the caller's token only governs the
        // wait on the gate.
        await entered.Task.WaitAsync(cancellationToken);
        await firstCaller.CancelAsync();

        // The second caller queues behind the gate while that one shared fetch is still running.
        var second = cache.GetKeyAsync(keys.KeyId, cancellationToken).AsTask();
        release.SetResult();

        (await second.WaitAsync(TestDeadline, cancellationToken)).ShouldNotBeNull(
            "the fetch is shared work, so one caller disconnecting must not deny the document to another caller");
        (await first.WaitAsync(TestDeadline, cancellationToken)).ShouldNotBeNull(
            "the fetch was already in flight, so the cancelled caller takes the published snapshot");
        handler.Calls.ShouldBe(1, "single flight: one fetch serves both callers");
    }

    private static GoogleLoginProvider Provider(HttpMessageHandler handler, GoogleLoginOptions settings)
    {
        var options = Options.Create(settings);

        return new GoogleLoginProvider(
            new HttpClient(handler),
            new GoogleIdTokenValidator(new GoogleTestJwksProvider(), options, new MutableTimeProvider(GoogleTestHarness.Now)),
            options,
            NullLogger<GoogleLoginProvider>.Instance);
    }

    private static GoogleLoginOptions Settings() => new()
    {
        ClientId = GoogleTestHarness.ClientId,
        ClientSecret = GoogleTestHarness.ClientSecret,

        // The smallest budget the options allow: one second, so the test stays quick.
        HttpTimeoutSeconds = 1,
    };

    private static LoginCallbackRequest Callback() => new(
        "test-authorization-code",
        "test-code-verifier-0123456789abcdef",
        GoogleTestHarness.Nonce,
        GoogleTestHarness.RedirectUri);

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
