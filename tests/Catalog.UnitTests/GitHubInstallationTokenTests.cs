using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.GitHub;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Shouldly;
using Xunit;

namespace Catalog.UnitTests;

/// <summary>
/// The installation-token cache: one exchange per cache key, reuse until the safety margin, a refresh driven by the
/// injected clock, a bounded single flight when callers arrive together, and a mint body that always names the
/// feature's permissions and (for every repository-scoped feature) the stored repositories.
/// </summary>
public sealed class GitHubInstallationTokenTests
{
    private static readonly TimeSpan OneHour = TimeSpan.FromHours(1);

    private static readonly string[] SharedToken = ["ghs_shared"];

    [Fact]
    public async Task Token_ReusedBelowExpiry_NoHttp()
    {
        var bench = new GitHubTestBench(TokenHandler("ghs_first", OneHour));

        var first = await bench.MintAsync();
        var second = await bench.MintAsync();

        first.IsSuccess.ShouldBeTrue();
        second.Value.Token.ShouldBe("ghs_first");
        bench.Handler.Requests.Count(request => request.Method == HttpMethod.Post).ShouldBe(1);
    }

    [Fact]
    public async Task Token_RemintedWithinSkew()
    {
        var bench = new GitHubTestBench(TokenHandler("ghs_first", TimeSpan.FromMinutes(10)));

        await bench.MintAsync();
        bench.Time.Advance(TimeSpan.FromMinutes(4));
        await bench.MintAsync();
        bench.Handler.Requests.Count(request => request.Method == HttpMethod.Post).ShouldBe(1);

        // Ten minutes of lifetime against a five-minute margin: past five minutes the token is no longer served.
        bench.Time.Advance(TimeSpan.FromMinutes(2));
        await bench.MintAsync();
        bench.Handler.Requests.Count(request => request.Method == HttpMethod.Post).ShouldBe(2);
    }

    [Fact]
    [SuppressMessage(
        "VisualStudio.Threading",
        "VSTHRD003",
        Justification = "The tasks are started by this test through GetAsync and share one in-flight exchange; awaiting them together cannot deadlock.")]
    public async Task Token_SingleFlight_50Callers()
    {
        var gate = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bench = new GitHubTestBench(new StubHandler((_, _) => gate.Task));

        var callers = Enumerable.Range(0, 50)
            .Select(_ => bench.MintAsync())
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

        var first = await bench.MintAsync();
        var second = await bench.MintAsync();

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

        var result = await bench.MintAsync();

        result.Error!.Code.ShouldBe(GitHubErrorCodes.RateLimited);
        result.Error.Retryable.ShouldBeTrue();
        result.Error.Details!.Single().Actual.ShouldBe(17L);
    }

    [Fact]
    public async Task Token_ExchangePresentsTheAppJwt_NotACachedToken()
    {
        var bench = new GitHubTestBench(TokenHandler("ghs_first", OneHour));

        await bench.MintAsync();

        var post = bench.Handler.Requests.Single();
        post.Method.ShouldBe(HttpMethod.Post);
        post.Uri.AbsolutePath.ShouldBe("/app/installations/7/access_tokens");
        post.Bearer!.Split('.').Length.ShouldBe(3);
    }

    [Fact]
    public async Task TokenRequest_AlwaysCarriesPermissionsAndRepositoryIds()
    {
        var bench = new GitHubTestBench(TokenHandler("ghs_first", OneHour));

        await bench.MintAsync(repositoryIds: [5, 9]);

        var post = bench.Handler.Requests.Single();
        post.Body.ShouldNotBeNull();
        using var body = JsonDocument.Parse(post.Body);
        var permissions = body.RootElement.GetProperty("permissions");
        permissions.GetProperty("contents").GetString().ShouldBe("read");
        permissions.GetProperty("metadata").GetString().ShouldBe("read");
        permissions.EnumerateObject().Count().ShouldBe(2);

        var repositories = body.RootElement.GetProperty("repository_ids")
            .EnumerateArray().Select(entry => entry.GetInt64()).Order().ToArray();
        repositories.ShouldBe([5L, 9L]);
    }

    [Fact]
    public async Task TokenRequest_FeatureNarrowsThePermissionSet()
    {
        var bench = new GitHubTestBench(TokenHandler("ghs_first", OneHour));

        await bench.MintAsync(feature: GitHubFeature.ActionsRerun);

        using var body = JsonDocument.Parse(bench.Handler.Requests.Single().Body!);
        var permissions = body.RootElement.GetProperty("permissions");
        permissions.GetProperty("actions").GetString().ShouldBe("write");
        permissions.TryGetProperty("contents", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Token_CachesPerFeatureAndRepositorySet()
    {
        var bench = new GitHubTestBench(TokenHandler("ghs_first", OneHour));

        await bench.MintAsync(repositoryIds: [5]);
        await bench.MintAsync(repositoryIds: [5]);
        await bench.MintAsync(repositoryIds: [9]);
        await bench.MintAsync(feature: GitHubFeature.Checks, repositoryIds: [5]);

        bench.Handler.Requests.Count.ShouldBe(3);
    }

    [Fact]
    public async Task Token_RepositoryScopedFeatureWithoutRepositoryIds_Fails()
    {
        var bench = new GitHubTestBench(TokenHandler("ghs_first", OneHour));

        var result = await bench.MintAsync(repositoryIds: []);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(GitHubErrorCodes.PermissionMapEmpty);
        bench.Handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public void PermissionMap_EmptyForFeature_Throws()
    {
        Should.Throw<InvalidOperationException>(() => GitHubFeaturePermissions.For((GitHubFeature)999));
    }

    [Fact]
    public void PermissionMap_EveryFeatureHasPermissions()
    {
        foreach (var feature in Enum.GetValues<GitHubFeature>())
        {
            GitHubFeaturePermissions.For(feature).Permissions.ShouldNotBeEmpty();
        }
    }

    [Fact]
    public async Task Token_InvalidateWithAStaleValue_KeepsTheCachedToken()
    {
        var bench = new GitHubTestBench(TokenHandler("ghs_first", OneHour));
        await bench.MintAsync();

        bench.Tokens.Invalidate("ghs_stale").ShouldBeFalse();
        var second = await bench.MintAsync();

        second.Value.Token.ShouldBe("ghs_first");
        bench.Handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Token_InvalidateWithTheCachedValue_ForcesANewExchange()
    {
        var bench = new GitHubTestBench(TokenHandler("ghs_first", OneHour));
        await bench.MintAsync();

        bench.Tokens.Invalidate("ghs_first").ShouldBeTrue();
        var second = await bench.MintAsync();

        second.Value.Token.ShouldBe("ghs_first");
        bench.Handler.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Token_InvalidateAll_FlushesEveryFeatureOfTheInstallation()
    {
        var bench = new GitHubTestBench(TokenHandler("ghs_first", OneHour));
        await bench.MintAsync(feature: GitHubFeature.Catalog);
        await bench.MintAsync(feature: GitHubFeature.Checks);
        await bench.Tokens.GetAsync(GitHubTestBench.OrgId, GitHubFeature.Catalog, [5], CancellationToken.None);

        bench.Tokens.InvalidateAll(7);

        (await bench.MintAsync(feature: GitHubFeature.Catalog)).IsSuccess.ShouldBeTrue();
        (await bench.MintAsync(feature: GitHubFeature.Checks)).IsSuccess.ShouldBeTrue();
        (await bench.Tokens.GetAsync(GitHubTestBench.OrgId, GitHubFeature.Catalog, [5], CancellationToken.None))
            .IsSuccess.ShouldBeTrue();

        bench.Handler.Requests.Count.ShouldBe(6);
    }

    [Fact]
    public async Task Token_OrgKeyedWithoutAResolver_FailsClosed()
    {
        var bench = new GitHubTestBench(TokenHandler("ghs_first", OneHour), withResolver: false);

        var result = await bench.Tokens.GetAsync(
            Guid.CreateVersion7(),
            GitHubFeature.Catalog,
            [5],
            CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(GitHubErrorCodes.NotConfigured);
        bench.Handler.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("""{"token":"ghs_x"}""")]
    [InlineData("""{"token":"ghs_x","expires_at":"not-a-date"}""")]
    [InlineData("""{"token":"ghs_x","expires_at":1767225600}""")]
    [InlineData("""{"token":"ghs_x","expires_at":"2025-12-31T23:59:59Z"}""")]
    public async Task Token_ExchangeWithAnUnusableExpiry_IsRefusedAndNotCached(string json)
    {
        var bench = new GitHubTestBench(new StubHandler(_ => GitHubResponses.Json(json)));

        var first = await bench.MintAsync();
        var second = await bench.MintAsync();

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

        var result = await bench.MintAsync();

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(GitHubErrorCodes.ResponseInvalid);
    }

    [Fact]
    public async Task Token_ToString_RedactsTheBearerValue()
    {
        var bench = new GitHubTestBench(TokenHandler("ghs_secret_value", OneHour));

        var result = await bench.MintAsync();

        result.Value.ToString().ShouldNotContain("ghs_secret_value");
        bench.TokenLogs.Lines.ShouldNotContain(line => line.Contains("ghs_secret_value", StringComparison.Ordinal));
    }

    private static StubHandler TokenHandler(string token, TimeSpan lifetime) =>
        new(_ => GitHubResponses.AccessToken(token, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) + lifetime));
    /// <summary>
    /// The org entry point is the sanctioned mint path: once the installation is disconnected the resolver refuses,
    /// so a token cached before the disconnect is not served and no exchange is attempted for the stale id.
    /// </summary>
    [Fact]
    public async Task Token_OrgKeyedAfterDisconnect_RefusesMint()
    {
        var bench = new GitHubTestBench(TokenHandler("ghs_first", OneHour));
        (await bench.Tokens.GetAsync(GitHubTestBench.OrgId, GitHubFeature.Catalog, [5], CancellationToken.None))
            .IsSuccess.ShouldBeTrue();
        var exchangesBefore = bench.Handler.Requests.Count(request => request.Method == HttpMethod.Post);

        bench.Resolver.Failure = Result.Failure<long>(Error.Conflict("installation_unavailable", "the org has no active installation"));
        var result = await bench.Tokens.GetAsync(GitHubTestBench.OrgId, GitHubFeature.Catalog, [5], CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe("installation_unavailable");
        bench.Handler.Requests.Count(request => request.Method == HttpMethod.Post).ShouldBe(exchangesBefore);
    }

}
