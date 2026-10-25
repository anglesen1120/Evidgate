using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Evidgate.Modules.Identity.Invites;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;

namespace Evidgate.Modules.Identity.Login;

/// <summary>
/// The invite token a browser stages before a federated login (02.3.1-T2, Q1): the plaintext token and when it
/// was staged. It exists only inside the protected cookie below, and the token is never written anywhere else.
/// </summary>
/// <param name="Version">Payload schema version; a value the code does not know is refused.</param>
/// <param name="Token">The <c>evgi_&lt;prefix&gt;_&lt;secret&gt;</c> invite token exactly as the admin minted it.</param>
/// <param name="IssuedAt">When the browser staged it, for the lifetime check.</param>
internal sealed record InviteTokenPayload(int Version, string Token, DateTimeOffset IssuedAt);

/// <summary>The source-generated JSON of <see cref="InviteTokenPayload"/> (never on the wire; inside the protected cookie only).</summary>
[JsonSerializable(typeof(InviteTokenPayload))]
internal sealed partial class InviteTokenJsonContext : JsonSerializerContext
{
}

/// <summary>
/// The staged-invite cookie of 02.3.1-T2 Q1: <c>__Host-evidgate_invite</c>, written by
/// <c>POST /v1/auth/invite-token</c> before the browser leaves for the provider, and read and deleted by the
/// login callback (<see cref="Evidgate.Modules.Identity.Endpoints.LoginEndpoints"/>).
/// </summary>
/// <remarks>
/// <para>
/// The token is a bearer secret: whoever holds it can claim the pending membership. It therefore never travels
/// in a URL, a query string, a <c>Location</c> header, the <c>returnUrl</c>, a log line, a metric or a trace
/// tag. The landing page posts it in a request body once and the server answers 204 plus this cookie, so the
/// value the browser keeps is unreadable to scripts (<c>HttpOnly</c>) and to the network (the cookie is
/// <c>Secure</c>).
/// </para>
/// <para>
/// The three <c>__Host-</c> rules apply exactly as on <see cref="LoginStateCookie"/>: <c>Secure</c>,
/// <c>Path=/</c> and no <c>Domain</c>, plus <c>HttpOnly</c> and <c>SameSite=Lax</c>, so a cross-site request
/// does not carry it and no other origin can shadow it. The lifetime is a fixed ten-minute window — long
/// enough to finish a provider round-trip, short enough that a staged token does not linger — and it is a
/// constant rather than a configuration key: this cookie must never need a setting a deployment can widen.
/// </para>
/// <para>
/// The value is encrypted and integrity-protected with ASP.NET Core Data Protection under
/// <see cref="ProtectorPurpose"/> (the same key ring as the pending-login cookie and a different purpose), so a
/// client can neither read the token nor alter the payload, and a tampered, foreign or expired value reads as
/// "no staged invite" instead of failing the login. The callback deletes the cookie on every outcome, so one
/// staging buys one login attempt.
/// </para>
/// </remarks>
internal sealed class InviteTokenCookie(
    IDataProtectionProvider protection,
    TimeProvider clock)
{
    /// <summary>Cookie name; the <c>__Host-</c> prefix is part of the contract (Q1).</summary>
    internal const string Name = "__Host-evidgate_invite";

    /// <summary>Data Protection purpose of the cookie value.</summary>
    internal const string ProtectorPurpose = "Evidgate.Login.InviteTokenCookie.v1";

    /// <summary>Payload schema version this build writes and reads.</summary>
    internal const int PayloadVersion = 1;

    /// <summary>Lifetime of a staged token, in seconds (Q1: at most ten minutes).</summary>
    internal const int LifetimeSeconds = 600;

    /// <summary>Tolerated clock difference for a payload stamped in the future.</summary>
    private static readonly TimeSpan FutureSkew = TimeSpan.FromSeconds(60);

    private readonly IDataProtector _protector = protection.CreateProtector(ProtectorPurpose);

    /// <summary>Lifetime of a written cookie and of the staging it carries.</summary>
    public static TimeSpan Lifetime => TimeSpan.FromSeconds(LifetimeSeconds);

    /// <summary>Writes the protected payload as the staged-invite cookie of this response.</summary>
    /// <param name="context">The request the browser is answering.</param>
    /// <param name="token">The invite token, already validated by <see cref="InviteTokenFormat.TryParse"/>.</param>
    public void Write(HttpContext context, string token)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrEmpty(token) || token.Length > InviteTokenFormat.MaxLength)
        {
            // Never stage something the binder could not parse: the caller validates first, and this is the
            // backstop that keeps a malformed value out of the cookie entirely.
            throw new ArgumentException("an invite token of the parsed shape is required", nameof(token));
        }

        var payload = new InviteTokenPayload(PayloadVersion, token, clock.GetUtcNow());
        context.Response.Cookies.Append(
            Name,
            _protector.Protect(JsonSerializer.Serialize(payload, InviteTokenJsonContext.Default.InviteTokenPayload)),
            Attributes(Lifetime));
    }

    /// <summary>
    /// Marks the cookie for deletion. The callback calls it on every outcome, including the ones that never
    /// read it: a staged token is one use, and a client must not be able to replay a value the server saw.
    /// </summary>
    /// <param name="context">The request the browser is answering.</param>
    public static void Delete(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Response.Cookies.Delete(Name, Attributes(maxAge: null));
    }

    /// <summary>
    /// Reads a cookie value: null when it is absent, unprotected by another key or purpose, unparsable,
    /// unknown in version, empty in its token, or outside its lifetime. Never throws for a client-supplied
    /// value, and never logs it.
    /// </summary>
    /// <param name="value">The raw <c>__Host-evidgate_invite</c> value, or null.</param>
    public string? Read(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        InviteTokenPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize(_protector.Unprotect(value), InviteTokenJsonContext.Default.InviteTokenPayload);
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (FormatException)
        {
            // Not even base64url: the client put that value there, so it is no staged invite, not a fault.
            return null;
        }
        catch (JsonException)
        {
            return null;
        }

        if (payload is null
            || payload.Version != PayloadVersion
            || string.IsNullOrEmpty(payload.Token)
            || payload.Token.Length > InviteTokenFormat.MaxLength)
        {
            return null;
        }

        var age = clock.GetUtcNow() - payload.IssuedAt;

        return age <= Lifetime && age >= -FutureSkew ? payload.Token : null;
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
