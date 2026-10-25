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
/// verifier, the allowlisted <c>returnUrl</c> and when the attempt began.
/// </summary>
/// <param name="Version">Payload schema version; a value the code does not know is refused.</param>
/// <param name="Provider">Route value of the provider that issued the attempt.</param>
/// <param name="State">One-time anti-CSRF value sent to the provider and compared on the callback.</param>
/// <param name="Nonce">Value an OIDC ID token must echo.</param>
/// <param name="Verifier">PKCE verifier (RFC 7636); only its S256 challenge left the server.</param>
/// <param name="ReturnUrl">Already allowlisted app path (<see cref="ReturnUrlPolicy"/>).</param>
/// <param name="IssuedAt">When the attempt began, for the lifetime check.</param>
internal sealed record LoginStatePayload(
    int Version,
    string Provider,
    string State,
    string Nonce,
    string Verifier,
    string ReturnUrl,
    DateTimeOffset IssuedAt);

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
/// <b>Single node:</b> nothing configures a shared ring (<c>PersistKeysTo…</c> or a distributed store), so
/// the default ring stays in each host's own key directory and its keys never reach a sibling replica: a
/// callback served by another replica cannot read a cookie the first one wrote. The default ring is
/// persisted only where that key directory is writable; where it is not (a read-only or otherwise unusable
/// key directory, which a deployment can produce without any code change), the framework keeps the ring in
/// memory instead, and then a restart of the same host also invalidates every pending login (the cookie is
/// refused as an invalid login, never repaired). A persisted, shared ring is therefore a deploy requirement,
/// not a property of this code, tracked as its own item in the project's STATUS (10.x deployment work);
/// until it exists, a login attempt must reach the node that wrote the cookie, which a sticky-session rule
/// provides.
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

    /// <summary>Payload schema version this build writes and reads.</summary>
    internal const int PayloadVersion = 1;

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
    /// unknown in version, or outside its lifetime. Never throws for a client-supplied value.
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

        if (payload is null || payload.Version != PayloadVersion)
        {
            return null;
        }

        var age = clock.GetUtcNow() - payload.IssuedAt;

        return age <= Lifetime && age >= -FutureSkew ? payload : null;
    }

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
