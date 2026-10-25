using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Sessions.Http;

/// <summary>
/// D9 / 02.3.2 §8 over the real deployment: whatever the request path does — serve the request or refuse it as
/// expired, revoked, unknown, disabled or antiforgery-failed — it never writes the cookie value, the session id,
/// the pepper, the user agent or the antiforgery token into a log line, and never puts one of them in a metric
/// tag. The counters stay labelled by the closed outcome vocabulary only.
/// </summary>
/// <remarks>
/// <para>
/// The capture is the deployment's own: every log entry the host's filters let through reaches a provider the
/// case installs, and every measurement of the session instruments reaches a <see cref="MeterListener"/> over the
/// platform meter (<see cref="EvidgateTelemetry.MeterName"/>). Each case proves the capture is ALIVE before it
/// asserts on absence (a probe line for the log capture, a measurement of the expected outcome for the metrics),
/// because "nothing found" in a capture that was never wired proves nothing.
/// </para>
/// <para>
/// The canaries are the values a leak would carry: the session cookie value, the antiforgery request token, the
/// session row id in both of its spellings, the pepper in its configuration and hex spellings, and the user agent
/// this suite sends. None may appear anywhere in the captured text or in a tag.
/// </para>
/// </remarks>
[Collection(HarnessSuite.Name)]
[Trait("Category", "Docker")]
public sealed class LogRedactionTests : HarnessTest
{
    /// <summary>The route the authenticated cases drive (it needs a member and answers their orgs).</summary>
    private const string OrgsPath = "/v1/orgs";

    /// <summary>The one non-error outcome token of the auth counter (02.3.2 §8).</summary>
    private const string ActiveOutcome = "active";

    /// <summary>
    /// The closed vocabulary of every session instrument's tag VALUES: the outcome codes of 00 §9.2 the request
    /// path answers, the slide flag (as the runtime prints a bool), and the rotation trigger. A value outside this
    /// set would mean the tags carry request data.
    /// </summary>
    private static readonly HashSet<string> AllowedTagValues = new(StringComparer.Ordinal)
    {
        ActiveOutcome,
        "unauthenticated",
        "session_expired",
        "session_revoked",
        "account_disabled",
        "csrf_failed",
        "true",
        "false",
        "True",
        "False",
        "login",
        "privilege_change",
    };

    /// <summary>The closed vocabulary of tag KEYS: a new key is a new decision about what may travel.</summary>
    private static readonly string[] AllowedTagKeys = ["outcome", "trigger", "written"];

    /// <summary>The session instruments this file listens to.</summary>
    private static readonly string[] SessionInstruments =
    [
        EvidgateTelemetry.SessionAuthOutcomeMetricName,
        EvidgateTelemetry.SessionRotatedMetricName,
        EvidgateTelemetry.SessionRevokedMetricName,
        EvidgateTelemetry.SessionSlideMetricName,
    ];

    /// <summary>
    /// The non-vacuity probe: one Warning line written through the host's own logger factory, so "the capture
    /// holds no credential" is asserted against a capture that provably receives lines. A bare
    /// <c>LogWarning</c> call would be CA1848 at error severity, hence the <see cref="LoggerMessage"/> delegate.
    /// </summary>
    private static readonly Action<ILogger, string, Exception?> Probe =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(1, nameof(Probe)), "log capture probe {Canary}");

    public LogRedactionTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>D9 (success path): the served request's logs and metrics carry no credential.</summary>
    [Fact]
    public async Task A_Served_Request_Keeps_The_Credential_Out_Of_Logs_And_Metrics()
    {
        using var host = SessionTestHost.Start(Fixture, logProvider: new CapturingLogProvider());
        var logs = LogsOf(host);
        var metrics = new ConcurrentQueue<Measurement>();
        using var listener = CaptureMetrics(metrics);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);

        logs.Clear();
        using var response = await SessionTestHost
            .SendAsync(client, HttpMethod.Get, OrgsPath, SessionTestHost.CookieHeaderFor(member.SessionCookie))
            ;

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        await AssertNothingLeaksAsync(host, logs, metrics, member, ActiveOutcome);
    }

    /// <summary>D9 (AC2): an expired session is refused without its value or its row id reaching a log or a tag.</summary>
    [Fact]
    public async Task An_Expired_Session_Keeps_The_Credential_Out_Of_Logs_And_Metrics()
    {
        using var host = SessionTestHost.Start(Fixture, logProvider: new CapturingLogProvider());
        var logs = LogsOf(host);
        var metrics = new ConcurrentQueue<Measurement>();
        using var listener = CaptureMetrics(metrics);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);

        host.Clock.Advance(TimeSpan.FromSeconds(host.IdleSeconds));
        logs.Clear();

        using var response = await SessionTestHost
            .SendAsync(client, HttpMethod.Get, OrgsPath, SessionTestHost.CookieHeaderFor(member.SessionCookie))
            ;

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await SessionTestHost.ErrorCodeAsync(response)).ShouldBe(ApiErrorCode.SessionExpired);

        await AssertNothingLeaksAsync(host, logs, metrics, member, "session_expired");
    }

    /// <summary>D9 (AC4): a revoked session is refused without its value or its row id reaching a log or a tag.</summary>
    [Fact]
    public async Task A_Revoked_Session_Keeps_The_Credential_Out_Of_Logs_And_Metrics()
    {
        using var host = SessionTestHost.Start(Fixture, logProvider: new CapturingLogProvider());
        var logs = LogsOf(host);
        var metrics = new ConcurrentQueue<Measurement>();
        using var listener = CaptureMetrics(metrics);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);

        await host.RevokeSessionAsync(await host.SessionIdOfAsync(member.SessionCookie))
            ;

        logs.Clear();
        using var response = await SessionTestHost
            .SendAsync(client, HttpMethod.Get, OrgsPath, SessionTestHost.CookieHeaderFor(member.SessionCookie))
            ;

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await SessionTestHost.ErrorCodeAsync(response)).ShouldBe(ApiErrorCode.SessionRevoked);

        await AssertNothingLeaksAsync(host, logs, metrics, member, "session_revoked");
    }

    /// <summary>D9 (D2): a value no session exists for is refused, and the value itself is never echoed.</summary>
    [Fact]
    public async Task An_Unknown_Cookie_Value_Never_Reaches_A_Log_Or_A_Tag()
    {
        const string unknown = "UnknownCookieValueForRedaction_0123456789-abc";

        using var host = SessionTestHost.Start(Fixture, logProvider: new CapturingLogProvider());
        var logs = LogsOf(host);
        var metrics = new ConcurrentQueue<Measurement>();
        using var listener = CaptureMetrics(metrics);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);

        logs.Clear();
        using var response = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Get,
            OrgsPath,
            SessionTestHost.SessionCookieName + "=" + unknown);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await SessionTestHost.ErrorCodeAsync(response)).ShouldBe(ApiErrorCode.Unauthenticated);

        await AssertNothingLeaksAsync(host, logs, metrics, member, "unauthenticated", unknown);
    }

    /// <summary>D9 (D7): the disabled-account refusal carries no credential either.</summary>
    [Fact]
    public async Task A_Disabled_Account_Keeps_The_Credential_Out_Of_Logs_And_Metrics()
    {
        using var host = SessionTestHost.Start(Fixture, logProvider: new CapturingLogProvider());
        var logs = LogsOf(host);
        var metrics = new ConcurrentQueue<Measurement>();
        using var listener = CaptureMetrics(metrics);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);

        await host.DisableUserAsync(member.UserId);

        logs.Clear();
        using var response = await SessionTestHost
            .SendAsync(client, HttpMethod.Get, OrgsPath, SessionTestHost.CookieHeaderFor(member.SessionCookie))
            ;

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SessionTestHost.ErrorCodeAsync(response)).ShouldBe(ApiErrorCode.AccountDisabled);

        await AssertNothingLeaksAsync(host, logs, metrics, member, "account_disabled");
    }

    /// <summary>
    /// D9 (AC5): the antiforgery refusal is about the PAIR, so neither the pair's tokens nor the session credential
    /// may appear in what the request left behind. The auth counter still reports the session as
    /// <c>active</c>: the credential was accepted, the mutation was not.
    /// </summary>
    [Fact]
    public async Task A_Rejected_Antiforgery_Pair_Keeps_Its_Tokens_Out_Of_Logs_And_Metrics()
    {
        using var host = SessionTestHost.Start(Fixture, logProvider: new CapturingLogProvider());
        var logs = LogsOf(host);
        var metrics = new ConcurrentQueue<Measurement>();
        using var listener = CaptureMetrics(metrics);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);

        var organizationsBefore = await host.CountAsync("select count(*) from public.organization");

        logs.Clear();
        using var response = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Post,
            OrgsPath,
            SessionTestHost.CookieHeaderFor(member.SessionCookie, member.Xsrf),
            jsonBody: """{"name":"Never created","slug":"never-created"}""");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SessionTestHost.ErrorCodeAsync(response)).ShouldBe(ApiErrorCode.CsrfFailed);

        // The handler really did not run: the valid body would have created this org had the pair been checked
        // after it, so an unchanged count is the observable proof.
        (await host.CountAsync("select count(*) from public.organization"))
            .ShouldBe(organizationsBefore, "the pair is judged before the handler, so no organization was created");
        host.Statements.Containing("session_authenticate").Count.ShouldBe(1, "one request, one session statement");

        // The session authenticated (the pair was judged afterwards), so the counter's outcome is `active`.
        await AssertNothingLeaksAsync(host, logs, metrics, member, ActiveOutcome);
    }

    /// <summary>The capture the host was started with.</summary>
    /// <param name="host">The running host.</param>
    private static CapturingLogProvider LogsOf(SessionTestHost host)
    {
        ArgumentNullException.ThrowIfNull(host);

        return host.Services.GetServices<ILoggerProvider>().OfType<CapturingLogProvider>().ShouldHaveSingleItem();
    }

    /// <summary>A listener over the platform meter that keeps every session measurement this test produces.</summary>
    /// <param name="sink">Where the measurements go.</param>
    private static MeterListener CaptureMetrics(ConcurrentQueue<Measurement> sink)
    {
        ArgumentNullException.ThrowIfNull(sink);

        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, publisher) =>
            {
                if (instrument.Meter.Name == EvidgateTelemetry.MeterName
                    && SessionInstruments.Contains(instrument.Name, StringComparer.Ordinal))
                {
                    publisher.EnableMeasurementEvents(instrument);
                }
            },
        };

        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
            sink.Enqueue(new Measurement(
                instrument.Name,
                tags.ToArray().ToDictionary(pair => pair.Key, pair => Text(pair.Value)))));

        listener.Start();

        return listener;
    }

    /// <summary>
    /// One tag value as text. A <see langword="bool"/> is a boxed flag (the slide's <c>written</c>), and the
    /// runtime would print it "True"/"False"; normalising it here keeps the vocabulary in the contract's own
    /// spelling instead of widening it to every capitalisation the runtime might choose.
    /// </summary>
    /// <param name="value">The tag value.</param>
    private static string? Text(object? value) => value switch
    {
        bool flag => flag ? "true" : "false",
        null => null,
        _ => value.ToString(),
    };

    /// <summary>
    /// The shared D9 assertion: the capture is alive, the instrument reported the expected outcome, every tag
    /// value stays inside the closed vocabulary, and no canary appears in any log text or any tag value.
    /// </summary>
    /// <param name="host">The running host.</param>
    /// <param name="logs">The capture the host writes to.</param>
    /// <param name="metrics">The measurements this case produced.</param>
    /// <param name="member">The sign-in whose credential must never leak.</param>
    /// <param name="expectedOutcome">The outcome code the auth counter must report for this request.</param>
    /// <param name="extraCanaries">Any other value that must not appear (e.g. a presented unknown value).</param>
    private static async Task AssertNothingLeaksAsync(
        SessionTestHost host,
        CapturingLogProvider logs,
        ConcurrentQueue<Measurement> metrics,
        SignedInMember member,
        string expectedOutcome,
        params string[] extraCanaries)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(logs);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(member);

        var sessionId = (await host.SessionIdOfAsync(member.SessionCookie)).ToString("D");

        // Non-vacuity, logs: a line written NOW through the host's own factory reaches the capture.
        var probeCanary = "probe-" + Guid.CreateVersion7().ToString("N");
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger<LogRedactionTests>();
        Probe(logger, probeCanary, null);

        logs.Entries.ShouldContain(
            entry => entry.Message.Contains(probeCanary, StringComparison.Ordinal),
            "the log capture must be live, or 'no credential was logged' proves nothing");

        // Non-vacuity, metrics: the instrument reported this request's outcome.
        var outcomes = metrics
            .Where(measurement => measurement.Instrument == EvidgateTelemetry.SessionAuthOutcomeMetricName)
            .Select(measurement => measurement.Tags.GetValueOrDefault("outcome"))
            .ToList();
        outcomes.ShouldContain(expectedOutcome, "the auth counter must have counted this request");

        var canaries = new List<string>
        {
            member.SessionCookie,
            member.Xsrf.RequestToken,
            sessionId,
            Guid.Parse(sessionId).ToString("N"),
            Convert.ToBase64String(host.Pepper),
            Convert.ToHexString(host.Pepper),
            Convert.ToHexStringLower(host.Pepper),
            SessionTestHost.UserAgent,
        };
        canaries.AddRange(extraCanaries);

        foreach (var entry in logs.Entries)
        {
            foreach (var canary in canaries)
            {
                entry.Message.ShouldNotContain(canary, Case.Sensitive, "no credential may reach a log line (D9)");
            }
        }

        foreach (var measurement in metrics)
        {
            foreach (var (key, value) in measurement.Tags)
            {
                AllowedTagKeys.ShouldContain(key, "the session instruments carry a closed set of tag keys");
                value.ShouldNotBeNull("a session tag is never null");
                AllowedTagValues.ShouldContain(value, "the tag vocabulary is closed: " + key);

                foreach (var canary in canaries)
                {
                    value.ShouldNotContain(canary, Case.Sensitive, "no credential may reach a metric tag (D9)");
                }
            }
        }
    }

    /// <summary>One captured measurement: the instrument and its tags, as text.</summary>
    /// <param name="Instrument">The instrument name.</param>
    /// <param name="Tags">Its tags, values stringified.</param>
    private sealed record Measurement(string Instrument, IReadOnlyDictionary<string, string?> Tags);
}
