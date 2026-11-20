using System.Diagnostics.Metrics;

namespace Evidgate.BuildingBlocks.Observability;

/// <summary>
/// The login and session instruments, kept apart from the pipeline and ingest ones so each file stays
/// readable and under the size limit.
/// </summary>
public static partial class EvidgateTelemetry
{
    /// <summary>
    /// Counter of federated login attempts on the <c>start</c>/<c>callback</c> routes, tagged by provider and
    /// outcome code (02.3.1-T1.1). The vocabulary is closed: provider ∈ the registered names (or
    /// <c>unknown</c>), outcome ∈ <c>started</c>, <c>completed</c> and the 00 §9.2 codes the routes answer
    /// (<c>token_invalid</c>, <c>unavailable</c>, <c>not_found</c>, <c>no_membership</c>,
    /// <c>account_disabled</c>). Never
    /// labelled with an email, a subject, a state, a nonce, a verifier or an authorization code.
    /// </summary>
    public const string LoginAttemptMetricName = "evidgate_login_attempts_total";

    /// <summary>
    /// Counter of cookie-authenticated requests by session-auth outcome (02.3.2-T1.1, §8): <c>active</c>, or one
    /// of the closed 00 §9.2 codes the request path answers (<c>unauthenticated</c>, <c>session_expired</c>,
    /// <c>session_revoked</c>, <c>account_disabled</c>, <c>csrf_failed</c>). Never labelled with a cookie value,
    /// a session id, an address or a user agent (D9).
    /// </summary>
    public const string SessionAuthOutcomeMetricName = "evidgate_session_auth_total";

    /// <summary>Counter of session rotations, tagged by trigger (login, privilege change). Never labelled with an id.</summary>
    public const string SessionRotatedMetricName = "evidgate_session_rotated_total";

    /// <summary>Counter of revoked sessions by count: one add per revoke-all call, labelled with nothing (D9).</summary>
    public const string SessionRevokedMetricName = "evidgate_session_revoked_total";

    /// <summary>
    /// Counter of session-path writes, tagged <c>written=true|false</c>: the idle-slide throttle's hit rate
    /// (02.3.2 §8). A request inside the 60 s window counts once with <c>written=false</c>.
    /// </summary>
    public const string SessionSlideMetricName = "evidgate_session_slide_total";

    private static readonly Counter<long> LoginAttempts = Meter.CreateCounter<long>(
        LoginAttemptMetricName,
        unit: "{attempt}",
        description: "Federated login attempts by provider and outcome code (02.3.1). Never labelled with an email, a subject, a state, a nonce, a verifier or a code.");

    private static readonly Counter<long> SessionAuthOutcomes = Meter.CreateCounter<long>(
        SessionAuthOutcomeMetricName,
        unit: "{request}",
        description: "Cookie-authenticated requests by session-auth outcome code (02.3.2). Never labelled with a cookie value, a session id, an address or a user agent.");

    private static readonly Counter<long> SessionRotations = Meter.CreateCounter<long>(
        SessionRotatedMetricName,
        unit: "{rotation}",
        description: "Session rotations by trigger (02.3.2). No ids: a rotation is counted, never attributed.");

    private static readonly Counter<long> SessionRevocations = Meter.CreateCounter<long>(
        SessionRevokedMetricName,
        unit: "{session}",
        description: "Sessions revoked, added in the batch each revoke-all call revoked (02.3.2). No labels.");

    private static readonly Counter<long> SessionSlides = Meter.CreateCounter<long>(
        SessionSlideMetricName,
        unit: "{request}",
        description: "Session-path writes by whether the idle slide was actually written (02.3.2's 60 s throttle hit rate). No labels beyond written.");

    /// <summary>
    /// Counts one federated login attempt (02.3.1-T1.1) under the closed vocabulary of
    /// <see cref="LoginAttemptMetricName"/>: the provider's registered name (or <c>unknown</c> for a route
    /// value nothing answers to) and an outcome code — never a value from the request or the provider.
    /// </summary>
    /// <param name="provider">A registered provider name, or <c>unknown</c>.</param>
    /// <param name="outcome">One of <c>started</c>, <c>completed</c>, or the 00 §9.2 code the route answered.</param>
    public static void RecordLoginAttempt(string provider, string outcome) =>
        LoginAttempts.Add(
            1,
            new KeyValuePair<string, object?>("provider", provider),
            new KeyValuePair<string, object?>("outcome", outcome));

    /// <summary>
    /// Counts one cookie-authenticated request by its session-auth <paramref name="outcome"/> (02.3.2 §8): the
    /// closed vocabulary of <see cref="SessionAuthOutcomeMetricName"/> only — never a cookie value, a session
    /// id, an address or a user agent (D9).
    /// </summary>
    /// <param name="outcome">One of <c>active</c>, <c>unauthenticated</c>, <c>session_expired</c>, <c>session_revoked</c>, <c>account_disabled</c>, <c>csrf_failed</c>.</param>
    public static void RecordSessionAuthOutcome(string outcome) =>
        SessionAuthOutcomes.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    /// <summary>Counts one session rotation (02.3.2 AC7), tagged with its trigger — never with an id.</summary>
    /// <param name="trigger">One of <c>login</c> (the callback rotated a forwarded cookie) or <c>privilege_change</c>.</param>
    public static void RecordSessionRotated(string trigger) =>
        SessionRotations.Add(1, new KeyValuePair<string, object?>("trigger", trigger));

    /// <summary>Counts <paramref name="count"/> revoked sessions (02.3.2 AC4); zero or less is a no-op. No labels.</summary>
    /// <param name="count">The rows revoked by one call.</param>
    public static void RecordSessionRevoked(long count)
    {
        if (count <= 0)
        {
            return;
        }

        SessionRevocations.Add(count);
    }

    /// <summary>
    /// Counts one session-path write decision (02.3.2 §8's slide throttle): <c>written=true</c> when the idle
    /// deadline moved, <c>false</c> when the request was inside the 60 s window. No labels beyond that flag.
    /// </summary>
    /// <param name="written">Whether the statement actually wrote the row.</param>
    public static void RecordSessionSlideWrite(bool written) =>
        SessionSlides.Add(1, new KeyValuePair<string, object?>("written", written));
}
