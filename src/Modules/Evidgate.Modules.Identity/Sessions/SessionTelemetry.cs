using Evidgate.BuildingBlocks.Observability;

namespace Evidgate.Modules.Identity.Sessions;

/// <summary>
/// The session vocabulary's telemetry (02.3.2 §8, D9): four counters and nothing else — an auth outcome per
/// cookie-authenticated request, a rotation, a batch of revocations, and whether the idle slide was actually
/// written. No cookie value, session id, user id, address or user agent may ever travel through this class.
/// </summary>
/// <remarks>
/// The instruments live in <see cref="EvidgateTelemetry"/> (one meter for the whole platform); this type is the
/// module's narrow door onto them, so the label vocabulary stays closed and reviewable in one place.
/// </remarks>
internal static class SessionTelemetry
{
    /// <summary>The outcome token of a served request — the only non-error value of <see cref="AuthOutcome"/>.</summary>
    internal const string ActiveOutcome = "active";

    /// <summary>Counts one cookie-authenticated request by its outcome code (02.3.2 §8).</summary>
    /// <param name="code">
    /// <see cref="ActiveOutcome"/> or one of the 00 §9.2 codes: <c>unauthenticated</c>, <c>session_expired</c>,
    /// <c>session_revoked</c>, <c>account_disabled</c>, <c>csrf_failed</c>. Never a value from the request.
    /// </param>
    public static void AuthOutcome(string code) => EvidgateTelemetry.RecordSessionAuthOutcome(code);

    /// <summary>
    /// Counts the rotation a privilege change caused (AC7). Called by the session store's
    /// <c>RotateAsync</c> — a caller that already invoked it MUST NOT count the same rotation again.
    /// </summary>
    public static void Rotated() => EvidgateTelemetry.RecordSessionRotated("privilege_change");

    /// <summary>Counts the rotation a login callback caused by forwarding a cookie (D5a, AC7).</summary>
    public static void RotatedOnLogin() => EvidgateTelemetry.RecordSessionRotated("login");

    /// <summary>Counts <paramref name="count"/> revoked sessions; zero or less is a no-op.</summary>
    /// <param name="count">The rows one revocation call ended.</param>
    public static void Revoked(int count) => EvidgateTelemetry.RecordSessionRevoked(count);

    /// <summary>Counts one slide decision on the request path: written, or throttled away (02.3.2 §8).</summary>
    /// <param name="written">Whether the statement actually updated the row.</param>
    public static void SlideWrite(bool written) => EvidgateTelemetry.RecordSessionSlideWrite(written);
}
