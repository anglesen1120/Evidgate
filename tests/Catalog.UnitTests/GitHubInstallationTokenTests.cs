using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;
using Evidgate.Modules.Catalog.Infrastructure.GitHub;
using Shouldly;
using Xunit;

namespace Catalog.UnitTests;

/// <summary>
/// The installation-token cache: one exchange per installation, reuse until the safety margin, a
/// refresh driven by the injected clock, and a bounded single flight when callers arrive together.
/// </summary>
public sealed class GitHubInstallationTokenTests
{
    private static readonly TimeSpan OneHour = TimeSpan.FromHours(1);

    private static readonly string[] SharedToken = ["ghs_shared"];

    [Fact]
    public async Task Token_SecondCall_ReusesTheCachedToken()
    {
        var bench = new GitHubTestBench(TokenHandler("ghs_first", TimeSpan.FromHours(1)));

        var first = await bench.Tokens.GetAsync(7, CancellationToken.None);
        var second = await bench.Tokens.GetAsync(7, CancellationToken.None);

        first.IsSuccess.ShouldBeTrue();
        second.Value.Token.ShouldBe("ghs_first");
        bench.Handler.Requests.Count(request => request.Method == HttpMethod.Post).ShouldBe(1);
    }

    [Fact]
    public async Task Token_InsideSafetyMargin_IsRefreshedOnTheNextCall()
    {
        var bench = new GitHubTestBench(TokenHandler("ghs_first", TimeSpan.FromMinutes(10)));

        await bench.Tokens.GetAsync(7, CancellationToken.None);
        bench.Time.Advance(TimeSpan.FromMinutes(4));
        await bench.Tokens.GetAsync(7, CancellationToken.None);
        bench.Handler.Requests.Count(request => request.Method == HttpMethod.Post).ShouldBe(1);

        bench.Time.Advance(TimeSpan.FromMinutes(2));
        await bench.Tokens.GetAsync(7, CancellationToken.None);
        bench.Handler.Requests.Count(request => request.Method == HttpMethod.Post).ShouldBe(2);
    }

    [Fact]
    [SuppressMessage(
        "VisualStudio.Threading",
        "VSTHRD003",
        Justification = "The tasks are started by this test through GetAsync and share one in-flight exchange; awaiting them together cannot deadlock.")]
    public async Task Token_ConcurrentCallers_ShareOneExchange()
    {
        var gate = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bench = new GitHubTestBench(new StubHandler((_, _) => gate.Task));

        var callers = Enumerable.Range(0, 8)
            .Select(_ => bench.Tokens.GetAsync(7, CancellationToken.None))
            .ToArray();
        gate.SetResult(GitHubResponses.AccessToken("ghs_shared", new DateTimeOffset(2026, 1, 1, 1, 0, 0, TimeSpan.Zero)));

        var results = await Task.WhenAll(callers);

        results.ShouldAllBe(result => result.IsSuccess);
        results.Select(result => result.Value.Token).Distinct().ShouldBe(SharedToken);
        bench.Handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Token_ExchangeFailure_IsNotCached()
    {
        var bench = new GitHubTestBench(new StubHandler(_ => GitHubResponses.Status(HttpStatusCode.InternalServerError)));

        var first = await bench.Tokens.GetAsync(7, CancellationToken.None);
        var second = await bench.Tokens.GetAsync(7, CancellationToken.None);

        first.Error!.Code.ShouldBe(GitHubErrorCodes.Unavailable);
        first.Error.Retryable.ShouldBeTrue();
        second.IsSuccess.ShouldBeFalse();
        bench.Handler.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Token_ExchangeRateLimited_CarriesRetryAfter()
    {
        var bench = new GitHubTestBench(
            new StubHandler(_ => GitHubResponses.Status(HttpStatusCode.TooManyRequests, retryAfterSeconds: 17)));

        var result = await bench.Tokens.GetAsync(7, CancellationToken.None);

        result.Error!.Code.ShouldBe(GitHubErrorCodes.RateLimited);
        result.Error.Retryable.ShouldBeTrue();
        result.Error.Details!.Single().Actual.ShouldBe(17L);
    }

    [Fact]
    public async Task Token_ExchangePresentsTheAppJwt_NotACachedToken()
    {
        var bench = new GitHubTestBench(TokenHandler("ghs_first", OneHour));

        await bench.Tokens.GetAsync(7, CancellationToken.None);

        var post = bench.Handler.Requests.Single();
        post.Method.ShouldBe(HttpMethod.Post);
        post.Uri.AbsolutePath.ShouldBe("/app/installations/7/access_tokens");
        post.Bearer!.Split('.').Length.ShouldBe(3);
    }

    [Fact]
    public async Task Token_Exchange_SendsReadOnlyPermissionsWithNoRepositoryIds()
    {
        var bench = new GitHubTestBench(TokenHandler("ghs_first", OneHour));

        await bench.Tokens.GetAsync(7, CancellationToken.None);

        var post = bench.Handler.Requests.Single();
        post.Body.ShouldNotBeNull();
        using var body = JsonDocument.Parse(post.Body);
        var permissions = body.RootElement.GetProperty("permissions");
        permissions.GetProperty("contents").GetString().ShouldBe("read");
        permissions.GetProperty("metadata").GetString().ShouldBe("read");
        body.RootElement.TryGetProperty("repository_ids", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Token_InvalidateWithAStaleValue_KeepsTheCachedToken()
    {
        var bench = new GitHubTestBench(TokenHandler("ghs_first", OneHour));
        await bench.Tokens.GetAsync(7, CancellationToken.None);

        bench.Tokens.Invalidate(7, "ghs_stale").ShouldBeFalse();
        var second = await bench.Tokens.GetAsync(7, CancellationToken.None);

        second.Value.Token.ShouldBe("ghs_first");
        bench.Handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Token_InvalidateWithTheCachedValue_ForcesANewExchange()
    {
        var bench = new GitHubTestBench(TokenHandler("ghs_first", OneHour));
        await bench.Tokens.GetAsync(7, CancellationToken.None);

        bench.Tokens.Invalidate(7, "ghs_first").ShouldBeTrue();
        var second = await bench.Tokens.GetAsync(7, CancellationToken.None);

        second.Value.Token.ShouldBe("ghs_first");
        bench.Handler.Requests.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData("""{"token":"ghs_x"}""")]
    [InlineData("""{"token":"ghs_x","expires_at":"not-a-date"}""")]
    [InlineData("""{"token":"ghs_x","expires_at":1767225600}""")]
    [InlineData("""{"token":"ghs_x","expires_at":"2025-12-31T23:59:59Z"}""")]
    public async Task Token_ExchangeWithAnUnusableExpiry_IsRefusedAndNotCached(string json)
    {
        var bench = new GitHubTestBench(new StubHandler(_ => GitHubResponses.Json(json)));

        var first = await bench.Tokens.GetAsync(7, CancellationToken.None);
        var second = await bench.Tokens.GetAsync(7, CancellationToken.None);

        first.Error!.Code.ShouldBe(GitHubErrorCodes.ResponseInvalid);
        second.IsSuccess.ShouldBeFalse();
        bench.Handler.Requests.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"text\"")]
    public async Task Token_ExchangeWithANonObjectRoot_IsRefused(string json)
    {
        var bench = new GitHubTestBench(new StubHandler(_ => GitHubResponses.Json(json)));

        var result = await bench.Tokens.GetAsync(7, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(GitHubErrorCodes.ResponseInvalid);
    }

    private static StubHandler TokenHandler(string token, TimeSpan lifetime) =>
        new(_ => GitHubResponses.AccessToken(token, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) + lifetime));
}
