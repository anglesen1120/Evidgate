using System.ComponentModel.DataAnnotations;

namespace Evidgate.Modules.Identity.Sessions;

/// <summary>
/// Configuration of session creation (02.3.1-T1.4), bound from <c>EVIDGATE:SESSION</c>: the pepper the
/// session cookie is hashed with and the two lifetimes a session row carries.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Pepper"/> has an internal setter, so the configuration binder skips it; it is resolved
/// through <c>ISecretProvider</c> (<c>SecretNames.SessionPepper</c>) by
/// <see cref="SessionOptionsSecretPostConfigure"/> (10.1.1-T2, the <c>CiAuthOptions</c> /
/// <c>GoogleLoginOptions</c> form). A missing pepper is allowed at boot — the host serves every other
/// route and login answers 503 <c>unavailable</c> until the deployment provisions it, exactly like the
/// CI key material. A present value must be base64 of at least 32 bytes.
/// </para>
/// <para>
/// The two lifetimes are provisional defaults chosen here (8 h idle, 7 d absolute — a [DECISION] value,
/// not a contract figure: 02.3.2 fixes the column and sliding semantics, not durations, and may refine
/// these). The idle lifetime may never exceed the absolute one, which is also the <c>session_expiry_check</c>
/// of 0041. Validation names the key only, never a value.
/// </para>
/// </remarks>
internal sealed class SessionOptions
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "EVIDGATE:SESSION";

    /// <summary>
    /// Base64 pepper (at least 32 bytes) behind the HMAC of the session cookie value. Resolved through
    /// <c>ISecretProvider</c>, never bound from configuration and never logged or put in an exception message.
    /// </summary>
    public string? Pepper { get; internal set; }

    /// <summary>
    /// Version stored beside every HMAC made with <see cref="Pepper"/>; bump it when the pepper rotates.
    /// The lower bound is enforced by <see cref="SessionOptionsValidator"/> rather than a <c>[Range]</c>: no
    /// upper bound exists, and the config-matrix lint derives constraints only from literal bounds.
    /// </summary>
    public int PepperVersion { get; init; } = 1;

    /// <summary>
    /// Sliding lifetime of a session in seconds: the idle deadline written at creation and extended by
    /// activity in 02.3.2-T1. Provisional [DECISION] default, 8 hours.
    /// </summary>
    [Range(60, 31_536_000)]
    public int IdleSeconds { get; init; } = 28_800;

    /// <summary>
    /// Hard lifetime of a session in seconds, never extended by activity. Provisional [DECISION] default,
    /// 7 days.
    /// </summary>
    [Range(60, 31_536_000)]
    public int AbsoluteSeconds { get; init; } = 604_800;

    /// <summary><see cref="IdleSeconds"/> as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan IdleLifetime => TimeSpan.FromSeconds(IdleSeconds);

    /// <summary><see cref="AbsoluteSeconds"/> as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan AbsoluteLifetime => TimeSpan.FromSeconds(AbsoluteSeconds);
}
