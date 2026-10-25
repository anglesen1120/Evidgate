using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Evidgate.Modules.Identity.Login;
using Evidgate.Modules.Identity.Login.Google;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Login.Google;

/// <summary>
/// 02.3.1 §8 (S3): Google login logs a closed failure category and nothing else. No authorization code,
/// PKCE verifier, nonce, client secret, ID token, email or subject reaches a log line or an exception
/// message, and provider text never does either.
/// </summary>
/// <remarks>
/// Pure: every provider answer is a <see cref="GoogleTestHttpHandler"/> stand-in and the logger is a
/// capturing <see cref="ILoggerProvider"/>, so the assertion is on the exact strings the shipping code
/// would emit.
/// </remarks>
public sealed class LogRedactionTests
{
    private static readonly string[] ClosedFailureCategories =
    [
        "unconfigured",
        "token_invalid",
        "token_endpoint_rejected",
        "provider_unavailable",
    ];

    private static readonly string[] UnconfiguredMessage = ["Google login completion failed: unconfigured"];

    [Fact]
    public async Task GoogleProvider_HappyPath_LogsNothing()
    {
        using var loggers = new CapturingLoggerFactory();
        var harness = new RedactionHarness(tokenRespond: null, settings: null, loggers);
        var cancellationToken = TestContext.Current.CancellationToken;

        var result = await harness.Provider.CompleteAsync(Callback(), cancellationToken);

        result.Identity.ShouldNotBeNull();
        loggers.Messages.ShouldBeEmpty("a successful login has nothing to say, least of all who logged in");
    }

    [Fact]
    public async Task GoogleProvider_Failures_LogOnlyClosedCategories()
    {
        using var loggers = new CapturingLoggerFactory();
        var cancellationToken = TestContext.Current.CancellationToken;

        var rejected = new RedactionHarness(
            (_, _) => Task.FromResult(GoogleTestHttpHandler.Json(
                """{"error":"invalid_grant","error_description":"provider-said-this"}""", HttpStatusCode.BadRequest)),
            settings: null,
            loggers);
        (await rejected.Provider.CompleteAsync(Callback(), cancellationToken)).Failure.ShouldBe(LoginFailure.TokenInvalid);

        var unavailable = new RedactionHarness(
            (_, _) => Task.FromResult(GoogleTestHttpHandler.Json("""{"error":"provider-said-this"}""", HttpStatusCode.InternalServerError)),
            settings: null,
            loggers);
        (await unavailable.Provider.CompleteAsync(Callback(), cancellationToken)).Failure.ShouldBe(LoginFailure.ProviderUnavailable);

        var unconfigured = new RedactionHarness(
            tokenRespond: null,
            settings: GoogleTestHarness.Options(clientSecret: null),
            loggers);
        (await unconfigured.Provider.CompleteAsync(Callback(), cancellationToken)).Failure.ShouldBe(LoginFailure.ProviderUnavailable);

        var nonceMismatch = new RedactionHarness(tokenRespond: null, settings: null, loggers);
        nonceMismatch.TokenOverride = () => nonceMismatch.Tokens.Mint((_, claims) => claims["nonce"] = "another-nonce");
        (await nonceMismatch.Provider.CompleteAsync(Callback(), cancellationToken)).Failure.ShouldBe(LoginFailure.TokenInvalid);

        var messages = loggers.Messages;
        messages.ShouldNotBeEmpty("each failure is one line");

        foreach (var message in messages)
        {
            message.ShouldStartWith("Google login completion failed: ");

            var category = message["Google login completion failed: ".Length..];
            ClosedFailureCategories.ShouldContain(category, $"'{category}' must be one of the closed categories");
        }

        messages.ShouldContain("Google login completion failed: token_endpoint_rejected");
        messages.ShouldContain("Google login completion failed: provider_unavailable");
        messages.ShouldContain("Google login completion failed: unconfigured");
        messages.ShouldContain("Google login completion failed: token_invalid");
    }

    [Fact]
    public async Task GoogleProvider_NoSecretTokenOrIdentityValueEverReachesALogLine()
    {
        using var loggers = new CapturingLoggerFactory();
        var cancellationToken = TestContext.Current.CancellationToken;

        var failing = new RedactionHarness(
            (_, _) => Task.FromResult(GoogleTestHttpHandler.Json(
                """{"error":"invalid_grant","error_description":"provider-said-this"}""", HttpStatusCode.BadRequest)),
            settings: null,
            loggers);

        await failing.Provider.CompleteAsync(Callback(), cancellationToken);

        var mismatch = new RedactionHarness(tokenRespond: null, settings: null, loggers);
        var mintedIdToken = string.Empty;
        mismatch.TokenOverride = () =>
        {
            mintedIdToken = mismatch.Tokens.Mint((_, claims) => claims["nonce"] = "another-nonce");

            return mintedIdToken;
        };

        await mismatch.Provider.CompleteAsync(Callback(), cancellationToken);

        var captured = loggers.Messages;
        captured.ShouldNotBeEmpty();

        foreach (var forbidden in new[]
                 {
                     "test-authorization-code",
                     "test-code-verifier-0123456789abcdef",
                     GoogleTestHarness.Nonce,
                     GoogleTestHarness.ClientSecret,
                     GoogleTestHarness.Email,
                     GoogleTestHarness.Subject,
                     GoogleTestHarness.DisplayName,
                     mintedIdToken,
                     "provider-said-this",
                     "another-nonce",
                 })
        {
            if (forbidden.Length == 0)
            {
                continue;
            }

            captured
                .Where(message => message.Contains(forbidden, StringComparison.OrdinalIgnoreCase))
                .ShouldBeEmpty($"'{forbidden}' must never be logged");
        }
    }

    [Fact]
    public async Task GoogleJwksCache_FailureMessage_CarriesNoProviderTextOrUri()
    {
        var handler = new GoogleTestHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("provider-said-this", Encoding.UTF8, "text/plain"),
        }));
        var cache = new HttpGoogleJwksCache(
            new HttpClient(handler),
            Options.Create(GoogleTestHarness.Options()),
            new MutableTimeProvider(GoogleTestHarness.Now),
            NullLogger<HttpGoogleJwksCache>.Instance);

        var exception = await Should.ThrowAsync<HttpRequestException>(
            () => cache.GetKeyAsync("test-key-1", TestContext.Current.CancellationToken).AsTask());

        exception.Message.ShouldNotContain("provider-said-this");
        exception.Message.ShouldNotContain("www.googleapis.com");
        exception.Message.ShouldNotContain("test-key-1");
    }

    [Fact]
    public async Task GoogleProvider_UnconfiguredProvider_LogsTheCategoryOnly()
    {
        using var loggers = new CapturingLoggerFactory();
        var harness = new RedactionHarness(
            tokenRespond: null,
            settings: GoogleTestHarness.Options(clientId: null, clientSecret: null),
            loggers);

        await harness.Provider.CompleteAsync(Callback(), TestContext.Current.CancellationToken);

        loggers.Messages.ToArray().ShouldBe(UnconfiguredMessage);
    }

    private static LoginCallbackRequest Callback() => new(
        Code: "test-authorization-code",
        CodeVerifier: "test-code-verifier-0123456789abcdef",
        Nonce: GoogleTestHarness.Nonce,
        RedirectUri: GoogleTestHarness.RedirectUri);

    /// <summary>The provider under test wired to capturing loggers, with both provider endpoints faked.</summary>
    private sealed class RedactionHarness
    {
        internal RedactionHarness(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? tokenRespond,
            GoogleLoginOptions? settings,
            ILoggerFactory loggers)
        {
            Settings = settings ?? GoogleTestHarness.Options();
            Clock = new MutableTimeProvider(GoogleTestHarness.Now);
            Keys = new GoogleTestKeys();
            Tokens = new GoogleTokenFactory(Clock, Keys);

            TokenHandler = new GoogleTestHttpHandler(tokenRespond ?? ((_, _) => Task.FromResult(
                GoogleTestHttpHandler.Json($$"""{"id_token":"{{TokenOverride?.Invoke() ?? Tokens.Mint()}}"}"""))));
            JwksHandler = new GoogleTestHttpHandler((_, _) => Task.FromResult(GoogleTestHttpHandler.Json(Keys.JwksJson())));

            var options = Options.Create(Settings);
            var cache = new HttpGoogleJwksCache(
                new HttpClient(JwksHandler),
                options,
                Clock,
                loggers.CreateLogger<HttpGoogleJwksCache>());

            Provider = new GoogleLoginProvider(
                new HttpClient(TokenHandler),
                new GoogleIdTokenValidator(cache, options, Clock),
                options,
                loggers.CreateLogger<GoogleLoginProvider>());
        }

        internal GoogleLoginOptions Settings { get; }

        internal MutableTimeProvider Clock { get; }

        internal GoogleTestKeys Keys { get; }

        internal GoogleTokenFactory Tokens { get; }

        internal GoogleTestHttpHandler TokenHandler { get; }

        internal GoogleTestHttpHandler JwksHandler { get; }

        /// <summary>Lets a test replace the ID token its default token endpoint returns.</summary>
        internal Func<string>? TokenOverride { get; set; }

        internal GoogleLoginProvider Provider { get; }
    }
}

/// <summary>Collects every formatted log line (and any exception text) the code under test emits.</summary>
internal sealed class CapturingLoggerFactory : ILoggerFactory, ILoggerProvider
{
    private readonly ConcurrentQueue<string> _messages = new();

    internal IReadOnlyList<string> Messages => _messages.ToArray();


    public ILogger CreateLogger(string categoryName) => new CapturingLogger(_messages);

    public void AddProvider(ILoggerProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
    }

    public void Dispose()
    {
        // Nothing to release: the queue is the only state.
    }

    private sealed class CapturingLogger(ConcurrentQueue<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            var text = formatter(state, exception);
            messages.Enqueue(exception is null ? text : $"{text} {exception.Message}");
        }
    }
}
