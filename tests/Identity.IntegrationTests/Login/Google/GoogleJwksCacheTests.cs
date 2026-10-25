using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using Evidgate.Modules.Identity.Login.Google;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Login.Google;

/// <summary>
/// 02.3.1-T1.2 / AC2: the JWKS cache. An unknown <c>kid</c> forces exactly one refresh — concurrent
/// callers share that one fetch (single flight) and a second unknown kid inside the minimum refresh
/// window refetches nothing and fails closed, so random kids cannot stampede Google. A key that rotates
/// is served after the window, a JWKS outage answers <c>unavailable</c> instead of pretending the token
/// is invalid, and a key set already held keeps verifying during the outage.
/// </summary>
/// <remarks>
/// Pure: the JWKS endpoint is a <see cref="GoogleTestHttpHandler"/> and the clock is
/// <see cref="MutableTimeProvider"/>, so the TTL and refresh-window boundaries are exact.
/// </remarks>
// The assembly fixture of this project (DatabaseFixture) needs Docker, so the smoke build, which has none, skips Category=Docker.
[Trait("Category", "Docker")]
public sealed class GoogleJwksCacheTests
{
    [Fact]
    public async Task GoogleJwksCache_ServesAPublishedKey()
    {
        var keys = new GoogleTestKeys();
        var harness = new CacheHarness(_ => Task.FromResult(GoogleTestHttpHandler.Json(keys.JwksJson())));

        var key = await harness.Cache.GetKeyAsync(keys.KeyId, TestContext.Current.CancellationToken);

        key.ShouldNotBeNull().KeyId.ShouldBe(keys.KeyId);
        harness.Handler.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task GoogleJwksCache_SingleFlight_OneFetchForConcurrentUnknownKids()
    {
        var keys = new GoogleTestKeys();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = 1;

        var harness = new CacheHarness(async (_, cancellationToken) =>
        {
            if (Interlocked.Exchange(ref first, 0) == 1)
            {
                entered.TrySetResult();

                // Hold the fetch open until every concurrent caller is queued behind the gate.
                await release.Task.WaitAsync(cancellationToken);
            }

            return GoogleTestHttpHandler.Json(keys.JwksJson());
        });

        var cancellationToken = TestContext.Current.CancellationToken;
        var lookups = Enumerable.Range(0, 8)
            .Select(index => Task.Run(() => harness.Cache.GetKeyAsync($"unpublished-{index}", cancellationToken).AsTask(), cancellationToken))
            .ToArray();

        await entered.Task.WaitAsync(cancellationToken);
        release.SetResult();

        var results = await Task.WhenAll(lookups);

        results.ShouldAllBe(key => key == null);
        harness.Handler.Calls.ShouldBe(1, "one in-flight fetch is shared by every concurrent caller");
    }

    [Fact]
    public async Task GoogleJwksCache_SecondUnknownKidInsideTheRefreshWindow_DoesNotRefetch()
    {
        var keys = new GoogleTestKeys();
        var harness = new CacheHarness(_ => Task.FromResult(GoogleTestHttpHandler.Json(keys.JwksJson())));
        var cancellationToken = TestContext.Current.CancellationToken;

        (await harness.Cache.GetKeyAsync(keys.KeyId, cancellationToken)).ShouldNotBeNull();

        harness.Clock.Advance(TimeSpan.FromSeconds(5));
        (await harness.Cache.GetKeyAsync("first-unpublished", cancellationToken)).ShouldBeNull();
        harness.Handler.Calls.ShouldBe(1, "a refresh inside the minimum window would let random kids stampede Google");

        harness.Clock.Advance(TimeSpan.FromSeconds(31));
        (await harness.Cache.GetKeyAsync("second-unpublished", cancellationToken)).ShouldBeNull();
        harness.Handler.Calls.ShouldBe(2, "past the window one more refresh is allowed, and it still fails closed");
    }

    [Fact]
    public async Task GoogleJwksCache_RotatedKidAfterTheRefreshWindow_IsServed()
    {
        var keys = new GoogleTestKeys();
        var rotated = new GoogleTestKeys("rotated-key-1");
        var publishRotation = false;

        var harness = new CacheHarness(_ => Task.FromResult(GoogleTestHttpHandler.Json(
            publishRotation ? GoogleTestKeys.JwksJson(keys, rotated) : keys.JwksJson())));
        var cancellationToken = TestContext.Current.CancellationToken;

        (await harness.Cache.GetKeyAsync(keys.KeyId, cancellationToken)).ShouldNotBeNull();
        harness.Handler.Calls.ShouldBe(1);

        publishRotation = true;
        harness.Clock.Advance(TimeSpan.FromSeconds(31));

        (await harness.Cache.GetKeyAsync(rotated.KeyId, cancellationToken)).ShouldNotBeNull();
        harness.Handler.Calls.ShouldBe(2, "the unknown kid forced exactly one refresh, which found the rotated key");
    }

    [Fact]
    public async Task GoogleJwksCache_Outage_FailsClosedWithUnavailable_AndDoesNotRefetchInsideTheWindow()
    {
        var harness = new CacheHarness(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var cancellationToken = TestContext.Current.CancellationToken;

        await Should.ThrowAsync<HttpRequestException>(() => harness.Cache.GetKeyAsync("test-key-1", cancellationToken).AsTask());

        await Should.ThrowAsync<HttpRequestException>(() => harness.Cache.GetKeyAsync("test-key-1", cancellationToken).AsTask());
        harness.Handler.Calls.ShouldBe(1, "the backoff window stops every caller queueing behind the next timeout");
    }

    [Fact]
    public async Task GoogleJwksCache_HeldKeyAnswersWhileTheEndpointIsDown()
    {
        var keys = new GoogleTestKeys();
        var outage = false;
        var harness = new CacheHarness(_ => Task.FromResult(outage
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : GoogleTestHttpHandler.Json(keys.JwksJson())));
        var cancellationToken = TestContext.Current.CancellationToken;

        (await harness.Cache.GetKeyAsync(keys.KeyId, cancellationToken)).ShouldNotBeNull();

        outage = true;
        harness.Clock.Advance(TimeSpan.FromHours(2));

        (await harness.Cache.GetKeyAsync(keys.KeyId, cancellationToken)).ShouldNotBeNull();
        harness.Handler.Calls.ShouldBe(2, "the refresh was attempted and failed, and the held key set still answered");
    }

    [Fact]
    public async Task GoogleJwksCache_OversizedDocument_IsRefused()
    {
        var keys = new GoogleTestKeys();
        var oversized = keys.JwksJson() + new string(' ', 4096);
        var cancellationToken = TestContext.Current.CancellationToken;

        // A declared Content-Length over the cap is refused before the body is read.
        var declared = new CacheHarness(
            _ => Task.FromResult(GoogleTestHttpHandler.Json(oversized)),
            GoogleTestHarness.Options(maxResponseBytes: 1024));
        await Should.ThrowAsync<HttpRequestException>(() => declared.Cache.GetKeyAsync(keys.KeyId, cancellationToken).AsTask());

        // And a body without a Content-Length is refused once the capped read passes the cap.
        var streamed = new CacheHarness(
            _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new LengthlessContent(oversized),
            }),
            GoogleTestHarness.Options(maxResponseBytes: 1024));
        await Should.ThrowAsync<HttpRequestException>(() => streamed.Cache.GetKeyAsync(keys.KeyId, cancellationToken).AsTask());

        streamed.Handler.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task GoogleJwksCache_SymmetricOnlyDocument_IsNotACandidate()
    {
        // "Keys parsed only as RSA": a symmetric entry can never verify an RS256 ID token.
        var harness = new CacheHarness(_ => Task.FromResult(GoogleTestHttpHandler.Json(GoogleTestKeys.SymmetricOnlyJwksJson("symmetric-key"))));

        (await harness.Cache.GetKeyAsync("symmetric-key", TestContext.Current.CancellationToken)).ShouldBeNull();
        harness.Handler.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task GoogleJwksCache_TooManyKeys_AreRefused()
    {
        var harness = new CacheHarness(
            _ => Task.FromResult(GoogleTestHttpHandler.Json(GoogleTestKeys.OversizedJwksJson(HttpGoogleJwksCache.MaxDocumentKeys + 1))));

        await Should.ThrowAsync<HttpRequestException>(() => harness.Cache.GetKeyAsync("key-0", TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task GoogleJwksCache_RefreshCounter_CarriesTheClosedTagSetOnly()
    {
        var keys = new GoogleTestKeys();
        var measurements = new ConcurrentQueue<(long Value, string[] Keys, string? Provider, string? Outcome)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, publisher) =>
            {
                if (instrument.Meter.Name == GoogleLoginMetrics.MeterName
                    && instrument.Name == GoogleLoginMetrics.JwksRefreshCounterName)
                {
                    publisher.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            if (instrument.Name != GoogleLoginMetrics.JwksRefreshCounterName)
            {
                return;
            }

            var pairs = tags.ToArray();
            measurements.Enqueue((
                measurement,
                pairs.Select(pair => pair.Key).OrderBy(key => key, StringComparer.Ordinal).ToArray(),
                pairs.FirstOrDefault(pair => pair.Key == GoogleLoginMetrics.ProviderTag).Value as string,
                pairs.FirstOrDefault(pair => pair.Key == GoogleLoginMetrics.OutcomeTag).Value as string));
        });
        listener.Start();

        var harness = new CacheHarness(_ => Task.FromResult(GoogleTestHttpHandler.Json(keys.JwksJson())));
        (await harness.Cache.GetKeyAsync(keys.KeyId, TestContext.Current.CancellationToken)).ShouldNotBeNull();

        measurements.ShouldNotBeEmpty("one fetch is one measurement of evidgate_oidc_jwks_refresh_total");
        foreach (var measurement in measurements)
        {
            measurement.Value.ShouldBe(1);
            measurement.Keys.ShouldBe(new[] { GoogleLoginMetrics.OutcomeTag, GoogleLoginMetrics.ProviderTag }, ignoreOrder: true);
            measurement.Provider.ShouldBe(GoogleLoginMetrics.Google);
        }

        measurements.Any(measurement => measurement.Outcome == GoogleLoginMetrics.Succeeded)
            .ShouldBeTrue("the fetch succeeded");

        // No kid, key material, token or subject can be in a tag: the tag set is two fixed names.
        measurements.SelectMany(measurement => measurement.Keys)
            .ShouldAllBe(key => key == GoogleLoginMetrics.ProviderTag || key == GoogleLoginMetrics.OutcomeTag);
    }

    /// <summary>The cache under test, its endpoint stand-in and the clock it reads.</summary>
    private sealed class CacheHarness
    {
        internal CacheHarness(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond, GoogleLoginOptions? settings = null)
        {
            Settings = settings ?? GoogleTestHarness.Options();
            Clock = new MutableTimeProvider(GoogleTestHarness.Now);
            Handler = new GoogleTestHttpHandler(respond);
            Cache = new HttpGoogleJwksCache(
                new HttpClient(Handler),
                Microsoft.Extensions.Options.Options.Create(Settings),
                Clock,
                NullLogger<HttpGoogleJwksCache>.Instance);
        }

        /// <summary>The same harness for a test that does not look at the request or the cancellation token.</summary>
        internal CacheHarness(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond, GoogleLoginOptions? settings = null)
            : this((request, _) => respond(request), settings)
        {
        }

        internal GoogleLoginOptions Settings { get; }

        internal MutableTimeProvider Clock { get; }

        internal GoogleTestHttpHandler Handler { get; }

        internal HttpGoogleJwksCache Cache { get; }
    }
}
