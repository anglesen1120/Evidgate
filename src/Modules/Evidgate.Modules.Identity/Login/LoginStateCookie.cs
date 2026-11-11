using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Identity.Login;

/// <summary>
/// The pending login a browser carries between <c>start</c> and <c>callback</c> (02.3.1 §5): which
/// provider the attempt belongs to, its one-time <c>state</c>, the nonce the ID token must echo, the PKCE
/// verifier, the allowlisted <c>returnUrl</c>, when the attempt began, and whether the attempt is a sign-in
/// or an explicit second-provider link (02.3.1-T2, Q4).
/// </summary>
/// <param name="Version">Payload schema version; a value the code does not know is refused.</param>
/// <param name="Provider">Route value of the provider that issued the attempt.</param>
/// <param name="State">One-time anti-CSRF value sent to the provider and compared on the callback.</param>
/// <param name="Nonce">Value an OIDC ID token must echo.</param>
/// <param name="Verifier">PKCE verifier (RFC 7636); only its S256 challenge left the server.</param>
/// <param name="ReturnUrl">Already allowlisted app path (<see cref="ReturnUrlPolicy"/>).</param>
/// <param name="IssuedAt">When the attempt began, for the lifetime check.</param>
/// <param name="Purpose"><see cref="PurposeLogin"/> for a sign-in, <see cref="PurposeLink"/> for an explicit
/// link; any other value is refused with the rest of an unknown payload.</param>
/// <param name="LinkUserId">The account a link was started for; set exactly when <paramref name="Purpose"/> is
/// <see cref="PurposeLink"/>.</param>
internal sealed record LoginStatePayload(
    int Version,
    string Provider,
    string State,
    string Nonce,
    string Verifier,
    string ReturnUrl,
    DateTimeOffset IssuedAt,
    string Purpose,
    Guid? LinkUserId)
{
    /// <summary>Purpose of a state cookie <c>start</c> wrote: the browser is signing in.</summary>
    public const string PurposeLogin = "login";

    /// <summary>Purpose of a state cookie a link start wrote: the browser is adding a provider to an account.</summary>
    public const string PurposeLink = "link";
}

/// <summary>The source-generated JSON of <see cref="LoginStatePayload"/> (never on the wire; inside the protected cookie only).</summary>
[JsonSerializable(typeof(LoginStatePayload))]
internal sealed partial class LoginStateJsonContext : JsonSerializerContext
{
}

/// <summary>
/// The short-lived pending-login cookie of 02.3.1 §5 and §8 (T38): <c>__Host-evidgate_login</c>, written by
/// <c>start</c> and consumed and deleted by <c>callback</c>.
/// </summary>
/// <remarks>
/// <para>
/// The three <c>__Host-</c> rules are mandatory and are what keep the cookie from being shadowed or
/// leaked to another origin: <c>Secure</c>, <c>Path=/</c> and no <c>Domain</c>; the value is
/// <c>HttpOnly</c> and <c>SameSite=Lax</c> (a cross-site POST must not carry it) with a <c>Max-Age</c> equal
/// to <see cref="LoginOptions.StateLifetimeSeconds"/>.
/// </para>
/// <para>
/// The value is integrity-protected and encrypted with ASP.NET Core Data Protection under
/// <see cref="ProtectorPurpose"/>, so a client can neither read the verifier nor alter a field: a tampered
/// or expired value is refused as an invalid login, never repaired. Data Protection is the framework's own
/// key-ring mechanism, needs no new secret in <c>ISecretProvider</c> and is already available through the
/// module's <c>Microsoft.AspNetCore.App</c> framework reference.
/// </para>
/// <para>
/// The ring is shared through PostgreSQL (<c>DataProtectionKeyRepository</c> over
/// <c>public.data_protection_key</c>), so every Api replica protects and unprotects the same keys and a
/// cookie minted by one replica is read by all of them: no sticky routing is needed.
/// </para>
/// </remarks>
internal sealed class LoginStateCookie(
    IDataProtectionProvider protection,
    IOptions<LoginOptions> options,
    TimeProvider clock)
{
    /// <summary>Cookie name; the <c>__Host-</c> prefix is part of the contract (T38).</summary>
    internal const string Name = "__Host-evidgate_login";

    /// <summary>Data Protection purpose of the cookie value.</summary>
    internal const string ProtectorPurpose = "Evidgate.Login.StateCookie.v1";

    /// <summary>
    /// Application name of the key ring: without it every host derives the purpose from its own content
    /// root, so the same deployment could not read its own cookies across processes.
    /// </summary>
    internal const string ApplicationName = "Evidgate";

    /// <summary>
    /// Payload schema version this build writes and reads. Version 2 added <see cref="LoginStatePayload.Purpose"/>
    /// and <see cref="LoginStatePayload.LinkUserId"/>: a version-1 cookie written by an earlier build is refused
    /// (the browser starts the login again), never upgraded, because a payload of unknown shape must not be
    /// interpreted as a sign-in.
    /// </summary>
    internal const int PayloadVersion = 2;

    /// <summary>Tolerated clock difference for a payload stamped in the future.</summary>
    private static readonly TimeSpan FutureSkew = TimeSpan.FromSeconds(60);

    private readonly IDataProtector _protector = protection.CreateProtector(ProtectorPurpose);

    /// <summary>Lifetime of a written cookie and of the attempt it carries.</summary>
    public TimeSpan Lifetime => options.Value.StateLifetime;

    /// <summary>Writes the protected payload as the pending-login cookie of this response.</summary>
    /// <param name="context">The request the browser is answering.</param>
    /// <param name="payload">The pending login to carry.</param>
    public void Write(HttpContext context, LoginStatePayload payload)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(payload);

        context.Response.Cookies.Append(Name, Protect(payload), Attributes(Lifetime));
    }

    /// <summary>
    /// Marks the cookie for deletion. Called on every callback outcome, including the ones that never read
    /// it: an attempt is one use, and a client must not be able to replay a cookie the server already saw.
    /// </summary>
    /// <param name="context">The request the browser is answering.</param>
    public static void Delete(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Response.Cookies.Delete(Name, Attributes(maxAge: null));
    }

    /// <summary>Protects a payload into a cookie value.</summary>
    /// <param name="payload">The pending login.</param>
    public string Protect(LoginStatePayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        return _protector.Protect(JsonSerializer.Serialize(payload, LoginStateJsonContext.Default.LoginStatePayload));
    }

    /// <summary>
    /// Reads a cookie value: null when it is absent, unprotected by another key or purpose, unparsable,
    /// unknown in version or in purpose, inconsistent with its own purpose, or outside its lifetime. Never
    /// throws for a client-supplied value.
    /// </summary>
    /// <param name="value">The raw <c>__Host-evidgate_login</c> value, or null.</param>
    public LoginStatePayload? Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        LoginStatePayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize(_protector.Unprotect(value), LoginStateJsonContext.Default.LoginStatePayload);
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (FormatException)
        {
            // Not even base64url: the client put that value there, so it is an invalid attempt, not a fault.
            return null;
        }
        catch (JsonException)
        {
            return null;
        }

        if (payload is null || payload.Version != PayloadVersion || !ShapeIsKnown(payload))
        {
            return null;
        }

        var age = clock.GetUtcNow() - payload.IssuedAt;

        return age <= Lifetime && age >= -FutureSkew ? payload : null;
    }

    /// <summary>
    /// Whether the payload's own fields agree with each other: exactly the two purposes this build knows, and
    /// a <see cref="LoginStatePayload.LinkUserId"/> set exactly on the link one. A payload that disagrees is
    /// refused whole, so no route can read a link as a sign-in (or the reverse) from a half-valid cookie.
    /// </summary>
    /// <param name="payload">The unprotected payload.</param>
    private static bool ShapeIsKnown(LoginStatePayload payload) => payload.Purpose switch
    {
        LoginStatePayload.PurposeLogin => payload.LinkUserId is null,
        LoginStatePayload.PurposeLink => payload.LinkUserId is not null,
        _ => false,
    };

    /// <summary>The one set of attributes both the write and the deletion use, so a browser replaces the cookie.</summary>
    private static CookieOptions Attributes(TimeSpan? maxAge) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        Path = "/",
        IsEssential = true,
        MaxAge = maxAge,
    };
}
