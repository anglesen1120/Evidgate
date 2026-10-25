using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Invites;
using Evidgate.Modules.Identity.Login;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Login;

/// <summary>
/// 02.3.1-T2 (Q1, Q4) callback plumbing: the staged invite token reaches the completion exactly once and its
/// cookie leaves the browser on EVERY outcome; the token never appears in a response header (<c>Location</c>
/// included), a body, a log line or a span; and the purpose inside the signed state cookie decides which seam
/// completes the attempt — a link attempt reaches only <see cref="ILinkCompletion"/>, a sign-in never does, and
/// a state cookie of the previous shape or of an unknown purpose is refused as 401 <c>token_invalid</c>.
/// </summary>
// The assembly fixture of this project (DatabaseFixture) needs Docker, so the smoke build, which has none, skips Category=Docker.
[Trait("Category", "Docker")]
public sealed class CallbackPlumbingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);

    private const string Provider = "fake";

    private const string OidcPath = "/v1/auth/oidc/";

    /// <summary>
    /// The non-vacuity probe of the log case: one Warning line written through the provider under test. A
    /// <see cref="LoggerMessage"/> delegate, because a bare <c>LogWarning</c> call is CA1848 at error severity.
    /// </summary>
    private static readonly Action<ILogger, string, Exception?> ProbeCapture =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(1, nameof(ProbeCapture)), "probe {Text}");

    [Fact]
    public async Task The_Staged_Token_Reaches_The_Completion_Once_And_Its_Cookie_Is_Deleted_On_Success()
    {
        var clock = new MutableClock(Now);
        var completion = new FakeLoginCompletion();
        using var host = LoginTestHost.Start(clock, completion, SignedIn());
        using var client = host.CreateLoginClient();
        var token = InviteTokenFormat.Generate().Full;

        var (payload, cookieValue) = await BeginAsync(host, client);
        var inviteValue = host.ProtectInvite(token, clock.UtcNow);

        using var response = await LoginTestHost.SendAsync(
            client,
            LoginTestHost.CallbackRequest(Provider, cookieValue, payload.State, inviteCookieValue: inviteValue));

        response.StatusCode.ShouldBe(HttpStatusCode.Found);
        completion.Calls.ShouldBe(1, "one verified callback is one hand-over");
        completion.Last!.InviteToken.ShouldBe(token);
        completion.Last.LinkUserId.ShouldBeNull("a sign-in names no link account");
        response.Headers.Location!.ToString().ShouldBe(completion.Target);
        LoginTestHost.InviteCookieDeletion(response).ShouldNotBeNull(
            "the staged token leaves the browser on a successful callback too");

        // One use: replaying the same callback with the same staged cookie never reaches the seam again.
        using var replay = await LoginTestHost.SendAsync(
            client,
            LoginTestHost.CallbackRequest(Provider, cookieValue, payload.State, inviteCookieValue: inviteValue));

        replay.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        completion.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task The_Invite_Cookie_Is_Deleted_When_The_Provider_Refuses_The_Token()
    {
        var clock = new MutableClock(Now);
        var completion = new FakeLoginCompletion();
        var provider = new FakeLoginProvider(Provider) { Result = LoginProviderResult.Fail(LoginFailure.TokenInvalid) };
        using var host = LoginTestHost.Start(clock, completion, provider);
        using var client = host.CreateLoginClient();
        var token = InviteTokenFormat.Generate().Full;

        var (payload, cookieValue) = await BeginAsync(host, client);

        using var response = await LoginTestHost.SendAsync(
            client,
            LoginTestHost.CallbackRequest(
                Provider,
                cookieValue,
                payload.State,
                inviteCookieValue: host.ProtectInvite(token, clock.UtcNow)));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await LoginTestHost.ErrorCodeAsync(response)).ShouldBe(ApiErrorCode.TokenInvalid);
        LoginTestHost.InviteCookieDeletion(response).ShouldNotBeNull("the token is consumed even by a refusal");
        completion.Calls.ShouldBe(0);

        await AssertNotOnTheWireAsync(response, token);
    }

    [Fact]
    public async Task The_Invite_Cookie_Is_Deleted_On_A_Provider_Outage()
    {
        var clock = new MutableClock(Now);
        var completion = new FakeLoginCompletion();
        var provider = new FakeLoginProvider(Provider)
        {
            OnComplete = _ => Task.FromException<LoginProviderResult>(new TimeoutException("the provider timed out")),
        };
        using var host = LoginTestHost.Start(clock, completion, provider);
        using var client = host.CreateLoginClient();
        var token = InviteTokenFormat.Generate().Full;

        var (payload, cookieValue) = await BeginAsync(host, client);

        using var response = await LoginTestHost.SendAsync(
            client,
            LoginTestHost.CallbackRequest(
                Provider,
                cookieValue,
                payload.State,
                inviteCookieValue: host.ProtectInvite(token, clock.UtcNow)));

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await LoginTestHost.ErrorCodeAsync(response)).ShouldBe(ApiErrorCode.Unavailable);
        response.Headers.RetryAfter.ShouldNotBeNull();
        LoginTestHost.InviteCookieDeletion(response).ShouldNotBeNull("an outage still consumes the staged token");
        completion.Calls.ShouldBe(0);

        await AssertNotOnTheWireAsync(response, token);
    }

    [Fact]
    public async Task The_Invite_Cookie_Is_Deleted_When_The_State_Is_Rejected()
    {
        var clock = new MutableClock(Now);
        var completion = new FakeLoginCompletion();
        var provider = SignedIn();
        using var host = LoginTestHost.Start(clock, completion, provider);
        using var client = host.CreateLoginClient();
        var token = InviteTokenFormat.Generate().Full;

        var (payload, cookieValue) = await BeginAsync(host, client);

        // A callback whose state does not match the cookie is refused before the provider is called — and the
        // staged token is still consumed, because it was presented.
        using var response = await LoginTestHost.SendAsync(
            client,
            LoginTestHost.CallbackRequest(
                Provider,
                cookieValue,
                state: payload.State + "x",
                inviteCookieValue: host.ProtectInvite(token, clock.UtcNow)));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        LoginTestHost.InviteCookieDeletion(response).ShouldNotBeNull();
        LoginTestHost.StateCookieDeletion(response).ShouldNotBeNull();
        provider.Completions.ShouldBe(0);
        completion.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Neither_The_Token_Nor_The_State_Reach_A_Response_A_Log_Or_A_Span()
    {
        using var logs = new RecordingLoggerProvider();
        using var spans = new SpanCollector();
        var clock = new MutableClock(Now);
        var completion = new FakeLoginCompletion();
        using var host = LoginTestHost.StartWithLogs(clock, logs, completion, SignedIn());
        using var client = host.CreateLoginClient();

        var token = InviteTokenFormat.Generate().Full;

        // Non-vacuity: the provider under test records a line written through it, so "nothing was captured"
        // can never be an artefact of a dead provider.
        ProbeCapture(logs.CreateLogger("capture-probe"), "token-probe", null);
        logs.Entries.ShouldContain(entry => entry.Contains("token-probe", StringComparison.Ordinal));
        logs.Clear();

        var (payload, cookieValue) = await BeginAsync(host, client);
        logs.Clear();

        using var response = await LoginTestHost.SendAsync(
            client,
            LoginTestHost.CallbackRequest(
                Provider,
                cookieValue,
                payload.State,
                inviteCookieValue: host.ProtectInvite(token, clock.UtcNow)));

        response.StatusCode.ShouldBe(HttpStatusCode.Found);

        // Headers: every one of them, Location included.
        foreach (var header in response.Headers)
        {
            foreach (var value in header.Value)
            {
                value.ShouldNotContain(token, Case.Sensitive, $"header {header.Key} must not carry the invite token");
                value.ShouldNotContain(payload.State, Case.Sensitive, $"header {header.Key} must not carry the state");
            }
        }

        // Body (the 302 has none, so this is the guard against a future one).
        var body = await LoginTestHost.BodyAsync(response);
        body.ShouldNotContain(token, Case.Sensitive);
        body.ShouldNotContain(payload.State, Case.Sensitive);

        // Logs: nothing the deployment would have written carries the token or the state.
        logs.Entries.ShouldNotContain(entry => entry.Contains(token, StringComparison.Ordinal));
        logs.Entries.ShouldNotContain(entry => entry.Contains(payload.State, StringComparison.Ordinal));

        // Traces: the server span of the callback keeps neither value in any tag.
        var callbackSpans = await spans.WaitForSpansUnderAsync(OidcPath, TestContext.Current.CancellationToken);
        callbackSpans.ShouldNotBeEmpty("the ASP.NET Core instrumentation must produce a server span for the callback");

        foreach (var span in callbackSpans)
        {
            foreach (var tag in span.Tags)
            {
                var value = tag.Value?.ToString() ?? string.Empty;
                value.ShouldNotContain(token, Case.Sensitive, $"span {span.DisplayName} tag {tag.Key}");
                value.ShouldNotContain(payload.State, Case.Sensitive, $"span {span.DisplayName} tag {tag.Key}");
            }
        }
    }

    [Fact]
    public async Task A_Link_Purpose_State_Reaches_Only_The_Link_Seam()
    {
        var clock = new MutableClock(Now);
        var completion = new FakeLoginCompletion();
        var link = new FakeLinkCompletion();
        using var host = LoginTestHost.Start(clock, completion, link, SignedIn());
        using var client = host.CreateLoginClient();
        var linkUserId = Guid.NewGuid();

        var (payload, _) = await BeginAsync(host, client);
        var linkPayload = payload with { Purpose = LoginStatePayload.PurposeLink, LinkUserId = linkUserId };

        using var response = await LoginTestHost.SendAsync(
            client,
            LoginTestHost.CallbackRequest(Provider, host.ProtectPending(linkPayload), linkPayload.State));

        response.StatusCode.ShouldBe(HttpStatusCode.Found);
        link.Calls.ShouldBe(1);
        link.Last!.LinkUserId.ShouldBe(linkUserId);
        link.Last.Identity.Provider.ShouldBe(Provider);
        completion.Calls.ShouldBe(0, "a link attempt must never be completed by the login completion");
        response.Headers.Location!.ToString().ShouldBe(link.Target);
    }

    [Fact]
    public async Task A_Sign_In_Never_Reaches_The_Link_Seam()
    {
        var clock = new MutableClock(Now);
        var completion = new FakeLoginCompletion();
        var link = new FakeLinkCompletion();
        using var host = LoginTestHost.Start(clock, completion, link, SignedIn());
        using var client = host.CreateLoginClient();

        var (payload, cookieValue) = await BeginAsync(host, client);

        using var response = await LoginTestHost.SendAsync(
            client,
            LoginTestHost.CallbackRequest(Provider, cookieValue, payload.State));

        response.StatusCode.ShouldBe(HttpStatusCode.Found);
        completion.Calls.ShouldBe(1);
        completion.Last!.LinkUserId.ShouldBeNull();
        link.Calls.ShouldBe(0, "the link seam belongs to link attempts only");
    }

    [Fact]
    public async Task A_Link_Purpose_State_Without_A_Link_Seam_Is_503_And_Never_Signs_In()
    {
        var clock = new MutableClock(Now);
        var completion = new FakeLoginCompletion();
        using var host = LoginTestHost.Start(clock, completion, SignedIn());
        using var client = host.CreateLoginClient();

        var (payload, _) = await BeginAsync(host, client);
        var linkPayload = payload with { Purpose = LoginStatePayload.PurposeLink, LinkUserId = Guid.NewGuid() };

        using var response = await LoginTestHost.SendAsync(
            client,
            LoginTestHost.CallbackRequest(Provider, host.ProtectPending(linkPayload), linkPayload.State));

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await LoginTestHost.ErrorCodeAsync(response)).ShouldBe(ApiErrorCode.Unavailable);
        response.Headers.RetryAfter.ShouldNotBeNull();
        completion.Calls.ShouldBe(0, "an uncomposed link seam must never fall back to a sign-in");
    }

    [Fact]
    public async Task A_State_Cookie_Of_The_Previous_Shape_Is_401()
    {
        var clock = new MutableClock(Now);
        var completion = new FakeLoginCompletion();
        var provider = SignedIn();
        using var host = LoginTestHost.Start(clock, completion, provider);
        using var client = host.CreateLoginClient();

        var (payload, _) = await BeginAsync(host, client);

        // The version-1 payload an earlier build wrote: no Purpose, no LinkUserId. It must be refused whole —
        // never interpreted as a sign-in.
        var legacy = $$"""
            {"Version":1,"Provider":"{{payload.Provider}}","State":"{{payload.State}}","Nonce":"{{payload.Nonce}}","Verifier":"{{payload.Verifier}}","ReturnUrl":"/","IssuedAt":"{{payload.IssuedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture)}}"}
            """;

        using var response = await LoginTestHost.SendAsync(
            client,
            LoginTestHost.CallbackRequest(Provider, host.ProtectRawPending(legacy), payload.State));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await LoginTestHost.ErrorCodeAsync(response)).ShouldBe(ApiErrorCode.TokenInvalid);
        provider.Completions.ShouldBe(0);
        completion.Calls.ShouldBe(0);
    }

    [Theory]
    [InlineData("signin")]
    [InlineData("LOGIN")]
    [InlineData("")]
    public async Task A_State_Cookie_Of_An_Unknown_Purpose_Is_401(string purpose)
    {
        var clock = new MutableClock(Now);
        var completion = new FakeLoginCompletion();
        var provider = SignedIn();
        using var host = LoginTestHost.Start(clock, completion, provider);
        using var client = host.CreateLoginClient();

        var (payload, _) = await BeginAsync(host, client);
        var tampered = payload with { Purpose = purpose };

        using var response = await LoginTestHost.SendAsync(
            client,
            LoginTestHost.CallbackRequest(Provider, host.ProtectPending(tampered), tampered.State));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await LoginTestHost.ErrorCodeAsync(response)).ShouldBe(ApiErrorCode.TokenInvalid);
        provider.Completions.ShouldBe(0);
        completion.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task A_Link_Purpose_State_Cookie_Without_An_Account_Is_401()
    {
        var clock = new MutableClock(Now);
        var completion = new FakeLoginCompletion();
        var link = new FakeLinkCompletion();
        var provider = SignedIn();
        using var host = LoginTestHost.Start(clock, completion, link, provider);
        using var client = host.CreateLoginClient();

        var (payload, _) = await BeginAsync(host, client);
        var inconsistent = payload with { Purpose = LoginStatePayload.PurposeLink, LinkUserId = null };

        using var response = await LoginTestHost.SendAsync(
            client,
            LoginTestHost.CallbackRequest(Provider, host.ProtectPending(inconsistent), inconsistent.State));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await LoginTestHost.ErrorCodeAsync(response)).ShouldBe(ApiErrorCode.TokenInvalid);
        provider.Completions.ShouldBe(0);
        link.Calls.ShouldBe(0);
        completion.Calls.ShouldBe(0);
    }

    /// <summary>
    /// The provider name of the metric case below: unique to it, because the meter and its counter are
    /// process-wide and a parallel suite's sign-in measurements must never be mistaken for this case's.
    /// </summary>
    private const string MetricProvider = "metricprobe";

    [Fact]
    public async Task Only_A_Sign_In_Callback_Is_Counted_As_A_Login_Attempt()
    {
        var attempts = new ConcurrentQueue<(string? Provider, string? Outcome)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, publisher) =>
            {
                if (instrument.Meter.Name == EvidgateTelemetry.MeterName
                    && instrument.Name == EvidgateTelemetry.LoginAttemptMetricName)
                {
                    publisher.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            if (instrument.Name != EvidgateTelemetry.LoginAttemptMetricName)
            {
                return;
            }

            var pairs = tags.ToArray();
            attempts.Enqueue((
                pairs.FirstOrDefault(pair => pair.Key == "provider").Value as string,
                pairs.FirstOrDefault(pair => pair.Key == "outcome").Value as string));
        });
        listener.Start();

        var clock = new MutableClock(Now);
        var completion = new FakeLoginCompletion();
        var link = new FakeLinkCompletion();
        var provider = new FakeLoginProvider(MetricProvider)
        {
            Result = LoginProviderResult.Ok(FakeLoginProvider.VerifiedIdentity(MetricProvider)),
        };
        using var host = LoginTestHost.Start(clock, completion, link, provider);
        using var client = host.CreateLoginClient();

        // (1) Non-vacuity: a sign-in callback IS one counted login attempt — so "nothing was counted" in the
        // link cases below cannot be an artefact of a listener that never fires.
        var (payload, cookieValue) = await BeginAsync(host, client, MetricProvider);

        using var signIn = await LoginTestHost.SendAsync(
            client,
            LoginTestHost.CallbackRequest(MetricProvider, cookieValue, payload.State));

        signIn.StatusCode.ShouldBe(HttpStatusCode.Found);
        completion.Calls.ShouldBe(1);
        Outcomes(attempts, MetricProvider).ShouldContain(
            "completed",
            "a sign-in callback is one counted login attempt (02.3.1-T1.1)");

        // (2) A link-purpose callback is a staging, not a sign-in, so it must not move the sign-in success
        // rate (the rule LinkErrors states): after its own `start`, which IS counted, only that `started`
        // measurement may exist — never `completed`.
        attempts.Clear();
        var (linkPayload, _) = await BeginAsync(host, client, MetricProvider);
        var staged = linkPayload with { Purpose = LoginStatePayload.PurposeLink, LinkUserId = Guid.NewGuid() };

        using var linkResponse = await LoginTestHost.SendAsync(
            client,
            LoginTestHost.CallbackRequest(MetricProvider, host.ProtectPending(staged), staged.State));

        linkResponse.StatusCode.ShouldBe(HttpStatusCode.Found);
        link.Calls.ShouldBe(1);
        completion.Calls.ShouldBe(1, "a link attempt never reaches the sign-in completion");
        Outcomes(attempts, MetricProvider).ShouldBe(
            ["started"],
            "the link's own start is counted; the link callback that completed it is not");

        // (3) The same for the failure side: a link refused by its seam must not appear as a refused sign-in
        // under its own error code.
        attempts.Clear();
        var (refusedPayload, _) = await BeginAsync(host, client, MetricProvider);
        var refusedStaging = refusedPayload with
        {
            Purpose = LoginStatePayload.PurposeLink,
            LinkUserId = Guid.NewGuid(),
        };
        link.Failure = new Error(ApiErrorCode.BindingConflict, "that identity belongs to another account", 409);

        using var refused = await LoginTestHost.SendAsync(
            client,
            LoginTestHost.CallbackRequest(MetricProvider, host.ProtectPending(refusedStaging), refusedStaging.State));

        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await LoginTestHost.ErrorCodeAsync(refused)).ShouldBe(ApiErrorCode.BindingConflict);
        Outcomes(attempts, MetricProvider).ShouldBe(
            ["started"],
            "a refused link is not a refused sign-in attempt");
    }

    /// <summary>The outcomes the attempt counter recorded for one provider, in order.</summary>
    /// <param name="attempts">What the case's listener captured.</param>
    /// <param name="provider">The provider label to keep.</param>
    private static List<string?> Outcomes(
        ConcurrentQueue<(string? Provider, string? Outcome)> attempts,
        string provider)
    {
        ArgumentNullException.ThrowIfNull(attempts);
        ArgumentNullException.ThrowIfNull(provider);

        return [.. attempts.Where(attempt => attempt.Provider == provider).Select(attempt => attempt.Outcome)];
    }

    /// <summary>A provider that verifies every callback as this suite's sign-in identity.</summary>
    private static FakeLoginProvider SignedIn() =>
        new(Provider) { Result = LoginProviderResult.Ok(FakeLoginProvider.VerifiedIdentity(Provider)) };

    /// <summary>Runs <c>start</c> and unprotects the pending-attempt cookie it wrote.</summary>
    /// <param name="host">The host under test.</param>
    /// <param name="client">The case's client.</param>
    /// <param name="provider">The provider route value to start with.</param>
    private static async Task<(LoginStatePayload Payload, string CookieValue)> BeginAsync(
        LoginTestHost host,
        HttpClient client,
        string provider = Provider)
    {
        using var start = await LoginTestHost.SendAsync(client, LoginTestHost.StartRequest(provider));
        start.StatusCode.ShouldBe(HttpStatusCode.Found);

        return host.PendingLogin(start);
    }

    /// <summary>Asserts a response carries the token in no header and no body.</summary>
    /// <param name="response">The response to inspect.</param>
    /// <param name="token">The invite token that must not be there.</param>
    private static async Task AssertNotOnTheWireAsync(HttpResponseMessage response, string token)
    {
        foreach (var header in response.Headers)
        {
            foreach (var value in header.Value)
            {
                value.ShouldNotContain(token, Case.Sensitive, $"header {header.Key}");
            }
        }

        (await LoginTestHost.BodyAsync(response)).ShouldNotContain(token, Case.Sensitive);
    }
}

/// <summary>
/// An <see cref="ILoggerProvider"/> that keeps what the host's own filters let through, so a case asserts on what
/// the deployment would log (the <c>RequestLogRedactionTests</c> mechanism, without a database host).
/// </summary>
internal sealed class RecordingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _entries = new();

    /// <summary>Every captured entry, as <c>level: message</c>.</summary>
    public IReadOnlyList<string> Entries => [.. _entries];

    /// <summary>Drops what was captured so far, so a case can observe one request at a time.</summary>
    public void Clear() => _entries.Clear();

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new RecordingLogger(_entries);

    /// <inheritdoc />
    public void Dispose()
    {
        // Nothing unmanaged to release: the captured entries belong to the case.
    }

    private sealed class RecordingLogger(ConcurrentQueue<string> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            entries.Enqueue($"{logLevel}: {formatter(state, exception)}");
        }
    }
}

/// <summary>The server spans of this process, snapshotted when each one stops (the <c>LoginTraceRedactionTests</c> probe).</summary>
internal sealed class SpanCollector : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly List<RecordedSpan> _stopped = [];

    public SpanCollector()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                var tags = activity.TagObjects.ToList();
                lock (_stopped)
                {
                    _stopped.Add(new RecordedSpan(activity.DisplayName, activity.GetTagItem("url.path") as string, tags));
                }
            },
        };

        ActivitySource.AddActivityListener(_listener);
    }

    public List<RecordedSpan> SpansUnder(string path)
    {
        lock (_stopped)
        {
            return _stopped
                .Where(span => (span.UrlPath?.StartsWith(path, StringComparison.Ordinal) ?? false)
                    || span.DisplayName.Contains(path, StringComparison.Ordinal))
                .ToList();
        }
    }

    // The server span stops after the response is flushed, so the client can see the response first.
    public async Task<List<RecordedSpan>> WaitForSpansUnderAsync(string path, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            var found = SpansUnder(path);
            if (found.Count > 0 || DateTime.UtcNow >= deadline)
            {
                return found;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
        }
    }

    public void Dispose() => _listener.Dispose();
}

/// <summary>One stopped span: what the probe asserts on, copied before the span is disposed.</summary>
internal sealed record RecordedSpan(
    string DisplayName,
    string? UrlPath,
    IReadOnlyList<KeyValuePair<string, object?>> Tags);
