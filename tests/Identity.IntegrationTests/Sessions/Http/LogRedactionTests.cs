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
/// expired, revoked, unknown, disabled or antiforgery-failed, or answer an administrative revoke (and its 503 when
/// the definer's audit append faults) — it never writes the cookie value, the session id, the pepper, the user
/// agent or the antiforgery token into a log line, nor an <c>audit_log</c> row id, and never puts one of them in a
/// metric tag. The counters stay labelled by the closed outcome vocabulary only.
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
/// session row id in both of its spellings, the pepper in its configuration and hex spellings, the user agent
/// this suite sends, and — for the administrative revoke — the audit row the append wrote, in both spellings.
/// None may appear anywhere in the captured text or in a tag.
/// </para>
/// </remarks>
[Trait("Category", "Docker")]
public sealed class LogRedactionTests : TenantTest
{
    /// <summary>The route the authenticated cases drive (it needs a member and answers their orgs).</summary>
    private const string OrgsPath = "/v1/orgs";

    /// <summary>The single-session route of AC6: a delete ends one addressed row.</summary>
    private const string SessionsPath = "/v1/auth/sessions";

    /// <summary>
    /// The action the <c>0050</c> definer writes for an administrative revoke. Pinned here, as
    /// <c>AdminRevokeAuditTests</c> pins its own copy, so a rename fails this suite instead of passing.
    /// </summary>
    private const string AdminRevokeAction = "auth.session_revoke_admin";

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

    /// <summary>Own database: a case counts every organization row and another installs an <c>audit_log</c> trigger.</summary>
    public LogRedactionTests(DatabaseFixture fixture)
        : base(fixture, DatabaseIsolation.Dedicated)
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

    /// <summary>
    /// D9 (F55, AC6): the administrative revoke — the one route whose PATH carries a session id — is served without
    /// that id, the credentials the request carried, or the audit row it wrote reaching a log line or a tag.
    /// </summary>
    [Fact]
    public async Task An_Admin_Revoke_Keeps_Its_Credential_And_Its_Audit_Row_Out_Of_Logs_And_Metrics()
    {
        using var host = SessionTestHost.Start(Fixture, logProvider: new CapturingLogProvider());
        var logs = LogsOf(host);
        var metrics = new ConcurrentQueue<Measurement>();
        using var listener = CaptureMetrics(metrics);
        using var client = host.CreateSessionClient();
        var victim = await host.SignInMemberAsync(client);
        var victimSessionId = await host.SessionIdOfAsync(victim.SessionCookie);
        var admin = await host.SignInMemberInOrgAsync(client, victim.OrgId, "tenant_admin");
        var adminSessionId = await host.SessionIdOfAsync(admin.SessionCookie);

        logs.Clear();
        using var revoked = await RevokeAsync(client, admin, victimSessionId, victim.OrgId.ToString());

        revoked.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var auditRowId = await AdminAuditRowIdAsync(host, victim.OrgId, victimSessionId);

        await AssertNothingLeaksAsync(
            host,
            logs,
            metrics,
            victim,
            ActiveOutcome,
            admin.SessionCookie,
            admin.Xsrf.RequestToken,
            adminSessionId.ToString("D"),
            adminSessionId.ToString("N"),
            auditRowId.ToString("D"),
            auditRowId.ToString("N"));
    }

    /// <summary>
    /// D9 (F55, AC6): the faulted administrative revoke — the definer's audit append refused, so the call answers
    /// 503 and the revoke rolls back — writes no line that names the session, the credentials, or an audit row, and
    /// the retry with the fault gone records the revoke exactly once without naming its row either.
    /// </summary>
    [Fact]
    public async Task A_Faulted_Admin_Revoke_Keeps_Its_Credential_And_Its_Audit_Row_Out_Of_Logs_And_Metrics()
    {
        using var host = SessionTestHost.Start(Fixture, logProvider: new CapturingLogProvider());
        var logs = LogsOf(host);
        var metrics = new ConcurrentQueue<Measurement>();
        using var listener = CaptureMetrics(metrics);
        using var client = host.CreateSessionClient();
        var victim = await host.SignInMemberAsync(client);
        var victimSessionId = await host.SessionIdOfAsync(victim.SessionCookie);
        var admin = await host.SignInMemberInOrgAsync(client, victim.OrgId, "tenant_admin");
        var adminSessionId = await host.SessionIdOfAsync(admin.SessionCookie);

        logs.Clear();

        var beforeRows = await host.CountAsync(
            "select count(*) from public.audit_log where org_id = @org",
            ("org", victim.OrgId));
        var beforeSeq = await host.CountAsync(
            "select coalesce(max(seq), 0) from public.audit_log where org_id = @org",
            ("org", victim.OrgId));

        HttpStatusCode? status = null;
        string? errorCode = null;

        try
        {
            await InstallAuditFaultAsync(host, victim.OrgId);

            using var failed = await RevokeAsync(client, admin, victimSessionId, victim.OrgId.ToString());

            status = failed.StatusCode;
            errorCode = await SessionTestHost.ErrorCodeAsync(failed);
        }
        finally
        {
            await DropAuditFaultAsync(host);
        }

        status.ShouldBe(HttpStatusCode.ServiceUnavailable);
        errorCode.ShouldBe(ApiErrorCode.Unavailable);
        (await host.SessionRowAsync(victimSessionId)).RevokedAt.ShouldBeNull(
            "the revoke shares the definer's transaction, so a refused append leaves the session live");
        (await host.CountAsync(
            "select count(*) from public.audit_log where org_id = @org",
            ("org", victim.OrgId)))
            .ShouldBe(beforeRows, "the faulted call committed no row whose id could leak");
        (await host.CountAsync(
            "select coalesce(max(seq), 0) from public.audit_log where org_id = @org",
            ("org", victim.OrgId)))
            .ShouldBe(beforeSeq, "and it moved no chain position");

        // The retry, with the fault gone, is an ordinary administrative revoke: the same capture then also spans a
        // served call and the ONE row it wrote, whose id is a real value of the chain.
        using (var retried = await RevokeAsync(client, admin, victimSessionId, victim.OrgId.ToString()))
        {
            retried.StatusCode.ShouldBe(HttpStatusCode.NoContent, "the failed call left nothing behind");
        }

        (await host.CountAsync(
            "select count(*) from public.audit_log where org_id = @org",
            ("org", victim.OrgId)))
            .ShouldBe(beforeRows + 1, "the retry records the revoke exactly once");
        (await host.CountAsync(
            "select coalesce(max(seq), 0) from public.audit_log where org_id = @org",
            ("org", victim.OrgId)))
            .ShouldBe(beforeSeq + 1);

        var auditRowId = await AdminAuditRowIdAsync(host, victim.OrgId, victimSessionId);

        await AssertNothingLeaksAsync(
            host,
            logs,
            metrics,
            victim,
            ActiveOutcome,
            admin.SessionCookie,
            admin.Xsrf.RequestToken,
            adminSessionId.ToString("D"),
            adminSessionId.ToString("N"),
            auditRowId.ToString("D"),
            auditRowId.ToString("N"));
    }

    /// <summary>The capture the host was started with.</summary>
    /// <param name="host">The running host.</param>
    private static CapturingLogProvider LogsOf(SessionTestHost host)
    {
        ArgumentNullException.ThrowIfNull(host);

        return host.Services.GetServices<ILoggerProvider>().OfType<CapturingLogProvider>().ShouldHaveSingleItem();
    }

    /// <summary>One <c>DELETE /v1/auth/sessions/{s}</c> as <paramref name="caller"/>'s browser sends it.</summary>
    /// <param name="client">A client of the host.</param>
    /// <param name="caller">The signed-in account whose cookie proves the request.</param>
    /// <param name="sessionId">The addressed row.</param>
    /// <param name="orgHeader">The <c>X-Evidgate-Org</c> value the administrative path names.</param>
    private static Task<HttpResponseMessage> RevokeAsync(
        HttpClient client,
        SignedInMember caller,
        Guid sessionId,
        string? orgHeader = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(caller);

        return SessionTestHost.SendAsync(
            client,
            HttpMethod.Delete,
            SessionsPath + "/" + sessionId,
            SessionTestHost.CookieHeaderFor(caller.SessionCookie, caller.Xsrf),
            xsrfToken: caller.Xsrf.RequestToken,
            orgHeader: orgHeader);
    }

    /// <summary>The id of the one <c>auth.session_revoke_admin</c> row a served revoke wrote for one session.</summary>
    /// <param name="host">The running host.</param>
    /// <param name="orgId">The org whose chain was extended.</param>
    /// <param name="sessionId">The revoked row the audit row names.</param>
    private static async Task<Guid> AdminAuditRowIdAsync(SessionTestHost host, Guid orgId, Guid sessionId)
    {
        ArgumentNullException.ThrowIfNull(host);

        var id = await host.ScalarAsync(
            "select id from public.audit_log where org_id = @org and action = @action and object_id = @oid",
            [
                ("org", orgId),
                ("action", AdminRevokeAction),
                ("oid", sessionId.ToString("D")),
            ]).ConfigureAwait(false);

        // ShouldBeOfType fails with the same message for a missing row and for a row whose id is not a uuid.
        return id.ShouldBeOfType<Guid>("the served revoke must have written its audit row, or there is no id to leak");
    }

    /// <summary>
    /// Installs the throwaway fault of the 503 case: a trigger on <c>audit_log</c> that refuses the
    /// administrative-revoke action of this org only, so no other write of the harness is disturbed. Created as the
    /// owner (the table's owner), under names no production object uses — the shape <c>AdminRevokeAuditTests</c> uses
    /// for its own faulted-append case.
    /// </summary>
    /// <param name="host">The running host (its owner seam runs the DDL).</param>
    /// <param name="orgId">The org whose row must be refused.</param>
    private static async Task InstallAuditFaultAsync(SessionTestHost host, Guid orgId)
    {
        ArgumentNullException.ThrowIfNull(host);

        await host.ArrangeAsync(
            """
            create or replace function public.f55_http_log_redaction_fault() returns trigger
              language plpgsql
              set search_path = ''
              as $$
              begin
                raise exception 'f55 http log redaction fault' using errcode = '58000';
              end
              $$;
            """).ConfigureAwait(false);

        // A utility statement takes no bind parameters, so the org travels as a literal uuid (a Guid's "D" text can
        // carry no SQL) and the action is the pinned constant.
        await host.ArrangeAsync(
            "create trigger f55_http_log_redaction_fault before insert on public.audit_log "
            + "for each row when (new.action = '" + AdminRevokeAction + "' and new.org_id = '" + orgId.ToString("D") + "'::uuid) "
            + "execute function public.f55_http_log_redaction_fault()").ConfigureAwait(false);
    }

    /// <summary>Drops the throwaway trigger and its function, whatever the case's outcome was.</summary>
    /// <param name="host">The running host (its owner seam runs the DDL).</param>
    private static async Task DropAuditFaultAsync(SessionTestHost host)
    {
        ArgumentNullException.ThrowIfNull(host);

        await host.ArrangeAsync("drop trigger if exists f55_http_log_redaction_fault on public.audit_log")
            .ConfigureAwait(false);
        await host.ArrangeAsync("drop function if exists public.f55_http_log_redaction_fault()")
            .ConfigureAwait(false);
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
