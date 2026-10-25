namespace Evidgate.Modules.Identity.Invites;

/// <summary>
/// Configuration of invite minting (02.1.2-T2), bound from <c>EVIDGATE:INVITE</c>: the pepper the
/// invite link's secret half is HMACed with and the version stored beside every hash.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Pepper"/> has an internal setter, so the configuration binder skips it; it is
/// resolved through <c>ISecretProvider</c> (<c>SecretNames.InvitePepper</c>) by
/// <see cref="InviteOptionsSecretPostConfigure"/> (10.1.1-T2, the <c>SessionOptions</c> form). A
/// missing pepper is allowed at boot — the host serves every other route and invite minting
/// answers 503 <c>unavailable</c> until the deployment provisions it. A present value must be
/// base64 of at least 32 bytes.
/// </para>
/// <para>
/// The pepper never leaves the process: only <c>HMAC-SHA256(pepper_vN, secret)</c> reaches the
/// database, beside <c>membership.invite_pepper_version</c>. Exactly one pepper is configured, so
/// bumping <see cref="PepperVersion"/> invalidates every pending invite —
/// <see cref="InviteSecrets.Verify"/> accepts that stored version only and fails closed — and an
/// admin must resend them; a resend re-mints under the current pepper. Validation names the key
/// only, never a value.
/// </para>
/// </remarks>
internal sealed class InviteOptions
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "EVIDGATE:INVITE";

    /// <summary>
    /// Base64 pepper (at least 32 bytes) behind the HMAC of an invite token's secret half. Resolved
    /// through <c>ISecretProvider</c>, never bound from configuration and never logged or put in an
    /// exception message.
    /// </summary>
    public string? Pepper { get; internal set; }

    /// <summary>
    /// Version stored beside every HMAC made with <see cref="Pepper">the pepper</see>; bump it when
    /// the pepper rotates. The lower bound is enforced by <see cref="InviteOptionsValidator"/> rather
    /// than a <c>[Range]</c>: no upper bound exists, and the config-matrix lint derives constraints
    /// only from literal bounds (the <c>SessionOptions</c> rule).
    /// </summary>
    public int PepperVersion { get; init; } = 1;
}
