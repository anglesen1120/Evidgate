using System.Net;
using System.Text;
using Evidgate.Modules.Catalog.Infrastructure.GitHub;
using Shouldly;
using Xunit;

namespace Catalog.UnitTests;

/// <summary>
/// The typed reads of the App client against a stub handler: required headers, the tree's truncated
/// flag, size and path caps, the status mapping, and the one retry after a refused installation
/// token. No test opens a socket.
/// </summary>
public sealed class GitHubClientTests
{
    private static readonly string ObjectSha = new('c', 40);
    private static readonly DateTimeOffset TokenExpiry = new(2026, 1, 1, 1, 0, 0, TimeSpan.Zero);

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
        var bench = new GitHubTestBench(Reads(_ => GitHubResponses.Status(HttpStatusCode.NotFound)));

        var result = await bench.Client.GetRepositoryAsync(7, 5, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(GitHubErrorCodes.NotFound);
        result.Error.Status.ShouldBe(404);
    }

    [Fact]
    public async Task RateLimited_429_CarriesRetryAfterSeconds()
    {
        var bench = new GitHubTestBench(Reads(_ => GitHubResponses.Status(HttpStatusCode.TooManyRequests, retryAfterSeconds: 7)));

        var result = await bench.Client.GetRepositoryAsync(7, 5, CancellationToken.None);

        result.Error!.Code.ShouldBe(GitHubErrorCodes.RateLimited);
        result.Error.Retryable.ShouldBeTrue();
        result.Error.Details!.Single().Actual.ShouldBe(7L);
    }

    [Fact]
    public async Task ServerError_IsARetryableUnavailableFailure()
    {
        var bench = new GitHubTestBench(Reads(_ => GitHubResponses.Status(HttpStatusCode.BadGateway)));

        var result = await bench.Client.GetTreeAsync(7, 5, ObjectSha, CancellationToken.None);

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

    [Fact]
    public async Task Tree_TruncatedFlag_IsSurfaced()
    {
        var bench = new GitHubTestBench(Reads(_ => GitHubResponses.Json(
            $$"""{"sha":"{{ObjectSha}}","truncated":true,"tree":[{{Entry("src/a.ts")}}]}""")));

        var result = await bench.Client.GetTreeAsync(7, 5, ObjectSha, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        result.Value.Truncated.ShouldBeTrue();
        result.Value.Entries.Single().Path.ShouldBe("src/a.ts");
    }

    [Fact]
    public async Task Tree_OverEntryCap_IsRefusedInsteadOfBuffered()
    {
        var bench = new GitHubTestBench(
            Reads(_ => GitHubResponses.Json(
                $$"""{"sha":"{{ObjectSha}}","truncated":false,"tree":[{{Entry("a")}},{{Entry("b")}},{{Entry("c")}}]}""")),
            new GitHubOptions { AppId = "12345", MaxTreeEntries = 2 });

        var result = await bench.Client.GetTreeAsync(7, 5, ObjectSha, CancellationToken.None);

        result.Error!.Code.ShouldBe(GitHubErrorCodes.ResponseTooLarge);
    }

    [Fact]
    public async Task Tree_UnsafePath_IsRefused()
    {
        var bench = new GitHubTestBench(Reads(_ => GitHubResponses.Json(
            $$"""{"sha":"{{ObjectSha}}","truncated":false,"tree":[{{Entry("../../etc/passwd")}}]}""")));

        var result = await bench.Client.GetTreeAsync(7, 5, ObjectSha, CancellationToken.None);

        result.Error!.Code.ShouldBe(GitHubErrorCodes.ResponseInvalid);
    }

    [Fact]
    public async Task Blob_IsBase64Decoded()
    {
        var content = Convert.ToBase64String(Encoding.UTF8.GetBytes("hello"));
        var bench = new GitHubTestBench(Reads(_ => GitHubResponses.Json(
            $$"""{"sha":"{{ObjectSha}}","encoding":"base64","size":5,"content":"{{content}}"}""")));

        var result = await bench.Client.GetBlobAsync(7, 5, ObjectSha, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        result.Value.Size.ShouldBe(5);
        Encoding.UTF8.GetString(result.Value.Content).ShouldBe("hello");
    }

    [Fact]
    public async Task Blob_OverCap_IsRefused()
    {
        var oversized = Convert.ToBase64String(new byte[100]);
        var bench = new GitHubTestBench(Reads(_ => GitHubResponses.Json(
            $$"""{"sha":"{{ObjectSha}}","encoding":"base64","size":100,"content":"{{oversized}}"}""")));

        var result = await bench.Client.GetBlobAsync(7, 5, ObjectSha, CancellationToken.None);

        result.Error!.Code.ShouldBe(GitHubErrorCodes.ResponseTooLarge);
    }

    [Fact]
    public async Task InvalidObjectId_IsRefusedWithoutARequest()
    {
        var bench = new GitHubTestBench(Reads(_ => GitHubResponses.Json("{}")));

        var result = await bench.Client.GetTreeAsync(7, 5, "not-a-sha", CancellationToken.None);

        result.Error!.Code.ShouldBe(GitHubErrorCodes.RequestInvalid);
        bench.Handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task BaseAddressOutsideTheAllowlist_IsRefusedWithoutARequest()
    {
        var bench = new GitHubTestBench(
            Reads(_ => GitHubResponses.Json(GitHubResponses.RepositoryJson)),
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
                return GitHubResponses.AccessToken($"ghs_{posts}", TokenExpiry);
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
        var bench = new GitHubTestBench(Reads(_ =>
        {
            var response = GitHubResponses.Status(HttpStatusCode.Forbidden);
            response.Headers.Add("X-RateLimit-Remaining", "0");
            return response;
        }));

        var result = await bench.Client.GetTreeAsync(7, 5, ObjectSha, CancellationToken.None);

        result.Error!.Code.ShouldBe(GitHubErrorCodes.RateLimited);
        result.Error.Status.ShouldBe(429);
        result.Error.Retryable.ShouldBeTrue();
    }

    [Fact]
    public async Task Forbidden_403_IsNotRetryable()
    {
        var bench = new GitHubTestBench(Reads(_ => GitHubResponses.Status(HttpStatusCode.Forbidden)));

        var result = await bench.Client.GetTreeAsync(7, 5, ObjectSha, CancellationToken.None);

        result.Error!.Code.ShouldBe(GitHubErrorCodes.Forbidden);
        result.Error.Status.ShouldBe(403);
        result.Error.Retryable.ShouldBeFalse();
    }

    [Fact]
    public async Task UnmappedStatus_IsAFailedFailure()
    {
        var bench = new GitHubTestBench(Reads(_ => GitHubResponses.Status((HttpStatusCode)418)));

        var result = await bench.Client.GetInstallationAsync(7, CancellationToken.None);

        result.Error!.Code.ShouldBe(GitHubErrorCodes.Failed);
    }

    [Fact]
    public async Task PersistentUnauthorized_AfterOneRetry_IsAnUnauthorizedFailure()
    {
        var posts = 0;
        var handler = new StubHandler(request => request.Method == HttpMethod.Post
            ? GitHubResponses.AccessToken($"ghs_{++posts}", TokenExpiry)
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
            Reads(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new AbortingStream()) }));

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
                ? Task.FromResult(GitHubResponses.AccessToken("ghs_stalled", TokenExpiry))
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
            Reads(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream()) }),
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
            Reads(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) }),
            options);

        var result = await bench.Client.GetInstallationAsync(7, CancellationToken.None);

        result.Error!.Code.ShouldBe(GitHubErrorCodes.ResponseTooLarge);
        stream.BytesRead.ShouldBeLessThan(DeclaredlessTotal);
    }

    [Fact]
    public async Task Logs_NeverContainTheTokenOrThePrivateKey()
    {
        var bench = new GitHubTestBench(Reads(_ => GitHubResponses.Status(HttpStatusCode.InternalServerError)));

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

    private static StubHandler Reads(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(request => request.Method == HttpMethod.Post
            ? GitHubResponses.AccessToken("ghs_test", TokenExpiry)
            : respond(request));

    private static string Entry(string path) =>
        $$"""{"path":"{{path}}","type":"blob","sha":"{{ObjectSha}}","size":12}""";

    private static async Task<HttpResponseMessage> StallAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return new HttpResponseMessage(HttpStatusCode.OK);
    }
}
