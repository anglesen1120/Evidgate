using System.Globalization;
using System.Net;
using Evidgate.Modules.Catalog.Infrastructure.GitHub;
using Shouldly;
using Xunit;

namespace Catalog.UnitTests;

/// <summary>
/// The typed reads of the App client against a stub handler: required headers, the status mapping
/// and rate-limit delays, the response-size cap, and the single retry after a refused installation
/// token. No test opens a socket.
/// </summary>
public sealed class GitHubClientTests
{
    [Fact]
    public async Task Installation_Call_SendsVersionedHeadersAndTheAppJwt()
    {
        var bench = new GitHubTestBench(new StubHandler(_ => GitHubResponses.Json(GitHubResponses.InstallationJson)));

        var result = await bench.Client.GetInstallationAsync(7, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        result.Value.Id.ShouldBe(7);
        result.Value.AccountLogin.ShouldBe("acme");
        result.Value.RepositorySelection.ShouldBe("selected");

        var request = bench.Handler.Requests.Single();
        request.Method.ShouldBe(HttpMethod.Get);
        request.Uri.AbsolutePath.ShouldBe("/app/installations/7");
        request.ApiVersion.ShouldBe("2022-11-28");
        request.Accept.ShouldBe(GitHubEndpoints.ApiMediaType);
        request.Bearer!.Split('.').Length.ShouldBe(3);
    }

    [Fact]
    public async Task Repository_NotFound_IsANotFoundFailure()
    {
        var bench = new GitHubTestBench(GitHubReadStubs.Handler(_ => GitHubResponses.Status(HttpStatusCode.NotFound)));

        var result = await bench.Client.GetRepositoryAsync(7, 5, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(GitHubErrorCodes.NotFound);
        result.Error.Status.ShouldBe(404);
    }

    [Fact]
    public async Task RateLimited_429_CarriesRetryAfterSeconds()
    {
        var bench = new GitHubTestBench(GitHubReadStubs.Handler(_ => GitHubResponses.Status(HttpStatusCode.TooManyRequests, retryAfterSeconds: 7)));

        var result = await bench.Client.GetRepositoryAsync(7, 5, CancellationToken.None);

        result.Error!.Code.ShouldBe(GitHubErrorCodes.RateLimited);
        result.Error.Retryable.ShouldBeTrue();
        result.Error.Details!.Single().Actual.ShouldBe(7L);
    }

    [Fact]
    public async Task ServerError_IsARetryableUnavailableFailure()
    {
        var bench = new GitHubTestBench(GitHubReadStubs.Handler(_ => GitHubResponses.Status(HttpStatusCode.BadGateway)));

        var result = await bench.Client.GetTreeAsync(7, 5, GitHubReadStubs.ObjectSha, CancellationToken.None);

        result.Error!.Code.ShouldBe(GitHubErrorCodes.Unavailable);
        result.Error.Retryable.ShouldBeTrue();
    }

    [Fact]
    public async Task TransportFailure_IsARetryableUnavailableFailure()
    {
        var bench = new GitHubTestBench(
            new StubHandler((_, _) => throw new HttpRequestException("connection refused")));

        var result = await bench.Client.GetInstallationAsync(7, CancellationToken.None);

        result.Error!.Code.ShouldBe(GitHubErrorCodes.Unavailable);
        result.Error.Retryable.ShouldBeTrue();
        bench.Handler.Requests.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"text\"")]
    public async Task Reads_WithANonObjectRoot_AreRefused(string json)
    {
        var bench = new GitHubTestBench(GitHubReadStubs.Handler(_ => GitHubResponses.Json(json)));

        var repository = await bench.Client.GetRepositoryAsync(7, 5, CancellationToken.None);
        var installation = await bench.Client.GetInstallationAsync(7, CancellationToken.None);

        repository.IsSuccess.ShouldBeFalse();
        repository.Error!.Code.ShouldBe(GitHubErrorCodes.ResponseInvalid);
        installation.IsSuccess.ShouldBeFalse();
        installation.Error!.Code.ShouldBe(GitHubErrorCodes.ResponseInvalid);
    }

    [Fact]
    public async Task Unauthorized_WithAStaleBearer_KeepsTheNewerCachedToken()
    {
        var posts = 0;
        var gets = 0;
        var handler = new StubHandler((_, _) => throw new InvalidOperationException("replaced below"));
        var bench = new GitHubTestBench(handler);
        handler.Respond = async (request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                posts++;
                return GitHubResponses.AccessToken($"ghs_{posts}", GitHubReadStubs.TokenExpiry);
            }

            gets++;
            if (gets > 1)
            {
                return GitHubResponses.Json(GitHubResponses.RepositoryJson);
            }

            // A concurrent call replaced the cached token while the refused call was in flight.
            bench.Tokens.Invalidate(7, "ghs_1").ShouldBeTrue();
            var refreshed = await bench.Tokens.GetAsync(7, CancellationToken.None);
            refreshed.Value.Token.ShouldBe("ghs_2");

            return GitHubResponses.Status(HttpStatusCode.Unauthorized);
        };

        var result = await bench.Client.GetRepositoryAsync(7, 5, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        posts.ShouldBe(2);
        bench.Handler.Requests[^1].Bearer.ShouldBe("ghs_2");
    }

    [Fact]
    public async Task Forbidden_403_PrimaryRateLimit_DerivesTheDelayFromTheResetHeader()
    {
        const long ResetUnix = 1_767_225_690; // 90 s after the bench clock, 2026-01-01T00:00:00Z
        var bench = new GitHubTestBench(GitHubReadStubs.Handler(_ =>
        {
            var response = GitHubResponses.Status(HttpStatusCode.Forbidden);
            response.Headers.Add("X-RateLimit-Remaining", "0");
            response.Headers.Add("X-RateLimit-Reset", ResetUnix.ToString(CultureInfo.InvariantCulture));
            return response;
        }));

        var result = await bench.Client.GetTreeAsync(7, 5, GitHubReadStubs.ObjectSha, CancellationToken.None);

        result.Error!.Code.ShouldBe(GitHubErrorCodes.RateLimited);
        result.Error.Details!.Single().Actual.ShouldBe(90L);
    }

    [Theory]
    [InlineData("retry-after")]
    [InlineData("reset")]
    public async Task RateLimited_WithALargeReportedDelay_PreservesIt(string source)
    {
        const long ResetUnix = 1_767_225_600 + 200_000; // 200 000 s past the bench clock
        var bench = new GitHubTestBench(GitHubReadStubs.Handler(_ =>
        {
            if (string.Equals(source, "retry-after", StringComparison.Ordinal))
            {
                return GitHubResponses.Status(HttpStatusCode.TooManyRequests, retryAfterSeconds: 200_000);
            }

            var response = GitHubResponses.Status(HttpStatusCode.Forbidden);
            response.Headers.Add("X-RateLimit-Remaining", "0");
            response.Headers.Add("X-RateLimit-Reset", ResetUnix.ToString(CultureInfo.InvariantCulture));
            return response;
        }));

        var result = await bench.Client.GetRepositoryAsync(7, 5, CancellationToken.None);

        result.Error!.Code.ShouldBe(GitHubErrorCodes.RateLimited);
        result.Error.Details!.Single().Actual.ShouldBe(200_000L);
    }

    [Fact]
    public async Task RateLimited_WithAStaleReset_ReportsADelayOfAtLeastOneSecond()
    {
        var bench = new GitHubTestBench(GitHubReadStubs.Handler(_ =>
        {
            var response = GitHubResponses.Status(HttpStatusCode.Forbidden);
            response.Headers.Add("X-RateLimit-Remaining", "0");
            response.Headers.Add("X-RateLimit-Reset", "1767225590"); // ten seconds before the bench clock
            return response;
        }));

        var result = await bench.Client.GetRepositoryAsync(7, 5, CancellationToken.None);

        result.Error!.Code.ShouldBe(GitHubErrorCodes.RateLimited);
        result.Error.Details!.Single().Actual.ShouldBe(1L);
    }

    [Fact]
    public async Task PersistentUnauthorized_IsNotReusedByTheNextCall()
    {
        var posts = 0;
        var handler = new StubHandler(request => request.Method == HttpMethod.Post
            ? GitHubResponses.AccessToken($"ghs_{++posts}", GitHubReadStubs.TokenExpiry)
            : GitHubResponses.Status(HttpStatusCode.Unauthorized));
        var bench = new GitHubTestBench(handler);

        await bench.Client.GetRepositoryAsync(7, 5, CancellationToken.None);
        await bench.Client.GetRepositoryAsync(7, 5, CancellationToken.None);

        // A refused bearer is never sent again: each call mints its own token and spends it once.
        var bearers = bench.Handler.Requests
            .Where(request => request.Method == HttpMethod.Get)
            .Select(request => request.Bearer)
            .ToList();
        bearers.Count.ShouldBe(4);
        bearers.Distinct().Count().ShouldBe(bearers.Count);
    }

    [Fact]
    public async Task InvalidObjectId_IsRefusedWithoutARequest()
    {
        var bench = new GitHubTestBench(GitHubReadStubs.Handler(_ => GitHubResponses.Json("{}")));

        var result = await bench.Client.GetTreeAsync(7, 5, "not-a-sha", CancellationToken.None);

        result.Error!.Code.ShouldBe(GitHubErrorCodes.RequestInvalid);
        bench.Handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task BaseAddressOutsideTheAllowlist_IsRefusedWithoutARequest()
    {
        var bench = new GitHubTestBench(
            GitHubReadStubs.Handler(_ => GitHubResponses.Json(GitHubResponses.RepositoryJson)),
            new GitHubOptions { AppId = "12345", ApiBaseUrl = "https://evil.example.com" });

        var result = await bench.Client.GetRepositoryAsync(7, 5, CancellationToken.None);

        result.Error!.Code.ShouldBe(GitHubErrorCodes.RequestInvalid);
        bench.Handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task UnauthorizedInstallationToken_IsEvictedAndRetriedOnce()
    {
        var posts = 0;
        var gets = 0;
        var handler = new StubHandler(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                posts++;
                return GitHubResponses.AccessToken($"ghs_{posts}", GitHubReadStubs.TokenExpiry);
            }

            gets++;
            return gets == 1
                ? GitHubResponses.Status(HttpStatusCode.Unauthorized)
                : GitHubResponses.Json(GitHubResponses.RepositoryJson);
        });
        var bench = new GitHubTestBench(handler);

        var result = await bench.Client.GetRepositoryAsync(7, 5, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        posts.ShouldBe(2);
        gets.ShouldBe(2);
        bench.Handler.Requests.Where(request => request.Method == HttpMethod.Get).Last().Bearer.ShouldBe("ghs_2");
    }

    [Fact]
    public async Task Forbidden_403_WithNoRateLimitRemaining_IsRateLimited()
    {
        var bench = new GitHubTestBench(GitHubReadStubs.Handler(_ =>
        {
            var response = GitHubResponses.Status(HttpStatusCode.Forbidden);
            response.Headers.Add("X-RateLimit-Remaining", "0");
            return response;
        }));

        var result = await bench.Client.GetTreeAsync(7, 5, GitHubReadStubs.ObjectSha, CancellationToken.None);

        result.Error!.Code.ShouldBe(GitHubErrorCodes.RateLimited);
        result.Error.Status.ShouldBe(429);
        result.Error.Retryable.ShouldBeTrue();
        result.Error.Details!.Single().Actual.ShouldBe(60L);
    }

    [Fact]
    public async Task Forbidden_403_IsNotRetryable()
    {
        var bench = new GitHubTestBench(GitHubReadStubs.Handler(_ => GitHubResponses.Status(HttpStatusCode.Forbidden)));

        var result = await bench.Client.GetTreeAsync(7, 5, GitHubReadStubs.ObjectSha, CancellationToken.None);

        result.Error!.Code.ShouldBe(GitHubErrorCodes.Forbidden);
        result.Error.Status.ShouldBe(403);
        result.Error.Retryable.ShouldBeFalse();
    }

    [Fact]
    public async Task UnmappedStatus_IsAFailedFailure()
    {
        var bench = new GitHubTestBench(GitHubReadStubs.Handler(_ => GitHubResponses.Status((HttpStatusCode)418)));

        var result = await bench.Client.GetInstallationAsync(7, CancellationToken.None);

        result.Error!.Code.ShouldBe(GitHubErrorCodes.Failed);
    }

    [Fact]
    public async Task PersistentUnauthorized_AfterOneRetry_IsAnUnauthorizedFailure()
    {
        var posts = 0;
        var handler = new StubHandler(request => request.Method == HttpMethod.Post
            ? GitHubResponses.AccessToken($"ghs_{++posts}", GitHubReadStubs.TokenExpiry)
            : GitHubResponses.Status(HttpStatusCode.Unauthorized));
        var bench = new GitHubTestBench(handler);

        var result = await bench.Client.GetRepositoryAsync(7, 5, CancellationToken.None);

        result.Error!.Code.ShouldBe(GitHubErrorCodes.Unauthorized);
        posts.ShouldBe(2);
        bench.Handler.Requests.Count(request => request.Method == HttpMethod.Get).ShouldBe(2);
    }

    [Fact]
    public async Task BodyRead_AbortedByTheDeadline_IsAnUnavailableFailure()
    {
        var bench = new GitHubTestBench(
            GitHubReadStubs.Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new AbortingStream()) }));

        var result = await bench.Client.GetInstallationAsync(7, CancellationToken.None);

        result.Error!.Code.ShouldBe(GitHubErrorCodes.Unavailable);
        result.Error.Retryable.ShouldBeTrue();
    }

    [Fact]
    public async Task StalledHeaders_EndAtTheCallsDeadline()
    {
        // The token exchange answers at once; the read call stalls before its headers. The call's own
        // deadline must turn that into github_unavailable instead of letting a cancellation escape.
        var options = new GitHubOptions { AppId = "12345", HttpTimeoutSeconds = 1 };
        var bench = new GitHubTestBench(
            new StubHandler((request, cancellationToken) => request.Method == HttpMethod.Post
                ? Task.FromResult(GitHubResponses.AccessToken("ghs_stalled", GitHubReadStubs.TokenExpiry))
                : StallAsync(cancellationToken)),
            options);

        var result = await bench.Client.GetInstallationAsync(7, CancellationToken.None);

        result.Error!.Code.ShouldBe(GitHubErrorCodes.Unavailable);
        result.Error.Retryable.ShouldBeTrue();
    }

    [Fact]
    public async Task StalledBodyRead_EndsAtTheCallsDeadline()
    {
        // HttpTimeoutSeconds = 1 → the whole call is 3 attempts + 2 s of slack, so a body that never
        // yields must be cut off there instead of waiting forever.
        var options = new GitHubOptions { AppId = "12345", HttpTimeoutSeconds = 1 };
        var bench = new GitHubTestBench(
            GitHubReadStubs.Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream()) }),
            options);

        var result = await bench.Client.GetInstallationAsync(7, CancellationToken.None);

        result.Error!.Code.ShouldBe(GitHubErrorCodes.Unavailable);
        result.Error.Retryable.ShouldBeTrue();
    }

    [Fact]
    public async Task Body_OverTheCapWithoutADeclaredLength_IsRefusedWhileStreaming()
    {
        const int DeclaredlessTotal = 1024 * 1024;
        var stream = new GeneratedStream(DeclaredlessTotal);
        var options = new GitHubOptions { AppId = "12345", MaxResponseBytes = 1024 };
        var bench = new GitHubTestBench(
            GitHubReadStubs.Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) }),
            options);

        var result = await bench.Client.GetInstallationAsync(7, CancellationToken.None);

        result.Error!.Code.ShouldBe(GitHubErrorCodes.ResponseTooLarge);
        stream.BytesRead.ShouldBeLessThan(DeclaredlessTotal);
    }

    [Fact]
    public async Task Logs_NeverContainTheTokenOrThePrivateKey()
    {
        var bench = new GitHubTestBench(GitHubReadStubs.Handler(_ => GitHubResponses.Status(HttpStatusCode.InternalServerError)));

        await bench.Client.GetRepositoryAsync(7, 5, CancellationToken.None);

        var lines = bench.ClientLogs.Lines.Concat(bench.TokenLogs.Lines).ToList();
        lines.ShouldNotBeEmpty();
        foreach (var line in lines)
        {
            line.ShouldNotContain("ghs_", Case.Sensitive);
            line.ShouldNotContain("eyJ", Case.Sensitive);
            line.ShouldNotContain("PRIVATE KEY", Case.Sensitive);
            line.ShouldNotContain("BEGIN", Case.Sensitive);
        }
    }


    private static async Task<HttpResponseMessage> StallAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return new HttpResponseMessage(HttpStatusCode.OK);
    }
}
