using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Evidgate.BuildingBlocks.SharedKernel.Secrets;
using Evidgate.Modules.Identity.Login;
using Evidgate.Modules.Identity.Login.GitHub;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Identity.IntegrationTests.Login.GitHub;

/// <summary>The fixed values the GitHub login suite configures and asserts against (02.3.1-T1.3).</summary>
internal static class GitHubTestHarness
{
    internal const string ClientId = "evidgate-test-github-client-id";
    internal const string ClientSecret = "test-client-secret-value";
    internal const string AccessToken = "test-access-token-value";
    internal const string Subject = "123456789";
    internal const string Login = "test-user";
    internal const string Email = "user@evidgate.test";
    internal const string DisplayName = "Test User";

    internal static readonly Uri RedirectUri = new("https://app.evidgate.test/v1/auth/oidc/github/callback", UriKind.Absolute);

    internal static GitHubLoginOptions Options(
        string? clientId = ClientId,
        string? clientSecret = ClientSecret,
        string? authorizeUrl = "https://github.com/login/oauth/authorize",
        string? tokenUrl = "https://github.com/login/oauth/access_token",
        string? apiBaseUrl = "https://api.github.com",
        int httpTimeoutSeconds = 5,
        int maxResponseBytes = 65536) => new()
        {
            ClientId = clientId,
            ClientSecret = clientSecret,
            AuthorizeUrl = authorizeUrl ?? "https://github.com/login/oauth/authorize",
            TokenUrl = tokenUrl ?? "https://github.com/login/oauth/access_token",
            ApiBaseUrl = apiBaseUrl ?? "https://api.github.com",
            HttpTimeoutSeconds = httpTimeoutSeconds,
            MaxResponseBytes = maxResponseBytes,
        };

    internal static LoginAuthorizationRequest AuthorizationRequest() =>
        new("test-state-1", "test-nonce-1", "test-code-challenge-1", RedirectUri);

    internal static LoginCallbackRequest Callback(string code = "test-authorization-code") => new(
        Code: code,
        CodeVerifier: "test-code-verifier-0123456789abcdef",
        Nonce: "test-nonce-1",
        RedirectUri: RedirectUri);

    /// <summary>A token-endpoint body of a complete exchange.</summary>
    internal static string TokenJson(string accessToken = AccessToken) =>
        $$"""{"access_token":"{{accessToken}}","token_type":"bearer","scope":"read:user,user:email"}""";

    /// <summary>The <c>GET /user</c> body of the fixture account.</summary>
    internal static string UserJson(string id = Subject, string? name = DisplayName, string login = Login) =>
        name is null
            ? $$"""{"id":{{id}},"login":"{{login}}","name":null}"""
            : $$"""{"id":{{id}},"login":"{{login}}","name":"{{name}}"}""";

    /// <summary>The <c>GET /user/emails</c> body: one verified primary address.</summary>
    internal static string EmailsJson(string email = Email, bool primary = true, bool verified = true) =>
        $$"""[{"email":"{{email}}","primary":{{Lower(primary)}},"verified":{{Lower(verified)}}}]""";

    private static string Lower(bool value) => value ? "true" : "false";

    /// <summary>The API answer the harness returns when a test does not script one: the fixture account.</summary>
    internal static HttpResponseMessage DefaultApi(HttpRequestMessage request) =>
        request.RequestUri?.AbsolutePath.EndsWith("/user/emails", StringComparison.Ordinal) == true
            ? GitHubTestHttpHandler.Json(EmailsJson())
            : GitHubTestHttpHandler.Json(UserJson());
}

/// <summary>Resolves the one GitHub secret and nothing else, so a composition test needs no environment.</summary>
internal sealed class GitHubTestSecretProvider(string? clientSecret) : ISecretProvider
{
    /// <inheritdoc />
    public ValueTask<string?> GetSecretAsync(string name, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(string.Equals(name, SecretNames.OauthGithubClientSecret, StringComparison.Ordinal) ? clientSecret : null);
}

/// <summary>What one call of <see cref="GitHubTestHttpHandler"/> saw, captured before the message is disposed.</summary>
/// <param name="Uri">The request URI.</param>
/// <param name="Body">The request body, when it carried one.</param>
/// <param name="Authorization">The <c>Authorization</c> header value.</param>
/// <param name="Accept">The accepted media types, joined.</param>
/// <param name="ApiVersion">The <c>X-GitHub-Api-Version</c> header value.</param>
/// <param name="UserAgent">The <c>User-Agent</c> product tokens, joined.</param>
internal sealed record CapturedRequest(
    Uri? Uri,
    string? Body,
    string? Authorization,
    string? Accept,
    string? ApiVersion,
    string? UserAgent);

/// <summary>An <see cref="HttpMessageHandler"/> that answers from a delegate and records what it saw.</summary>
internal sealed class GitHubTestHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    private int _calls;

    internal int Calls => Volatile.Read(ref _calls);

    internal ConcurrentQueue<CapturedRequest> Captured { get; } = new();

    internal IReadOnlyList<CapturedRequest> Requests => Captured.ToArray();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);

        var body = request.Content is { } content
            ? await content.ReadAsStringAsync(cancellationToken)
            : null;

        Captured.Enqueue(new CapturedRequest(
            request.RequestUri,
            body,
            request.Headers.Authorization?.ToString(),
            request.Headers.Accept.Count == 0 ? null : string.Join(", ", request.Headers.Accept.Select(header => header.MediaType)),
            request.Headers.TryGetValues("X-GitHub-Api-Version", out var version) ? string.Join(", ", version) : null,
            request.Headers.UserAgent.Count == 0 ? null : string.Join(" ", request.Headers.UserAgent.Select(product => product.ToString()))));

        return await respond(request, cancellationToken);
    }

    /// <summary>A JSON answer with the given status.</summary>
    internal static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}

/// <summary>
/// The provider under test with its two clients replaced by stand-ins: the token POST and the API GETs
/// are separate handlers because they are separate clients, and the API handler dispatches on the path so
/// one delegate can script <c>/user</c> and <c>/user/emails</c> independently.
/// </summary>
internal sealed class GitHubProviderHarness
{
    internal GitHubProviderHarness(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? token = null,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? api = null,
        GitHubLoginOptions? settings = null,
        ILoggerFactory? loggers = null)
    {
        Settings = settings ?? GitHubTestHarness.Options();
        TokenHandler = new GitHubTestHttpHandler(token ?? ((_, _) => Task.FromResult(
            GitHubTestHttpHandler.Json(GitHubTestHarness.TokenJson()))));
        ApiHandler = new GitHubTestHttpHandler(api ?? ((request, _) => Task.FromResult(GitHubTestHarness.DefaultApi(request))));

        var options = Options.Create(Settings);
        var factory = loggers ?? NullLoggerFactory.Instance;

        Provider = new GitHubLoginProvider(
            new HttpClient(TokenHandler),
            new HttpClient(ApiHandler),
            options,
            factory.CreateLogger<GitHubLoginProvider>());
    }

    internal GitHubLoginOptions Settings { get; }

    internal GitHubTestHttpHandler TokenHandler { get; }

    internal GitHubTestHttpHandler ApiHandler { get; }

    internal GitHubLoginProvider Provider { get; }
}
