using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;

namespace Evidgate.Modules.Identity.Login.Linking;

/// <summary>
/// The verified identity a link callback staged and the account it is waiting to be attached to (02.3.1-T2.2,
/// Q4). It exists only inside the protected <c>__Host-evidgate_link</c> cookie: the confirm route reads it,
/// the explicit <c>POST /v1/auth/link/confirm</c> is what writes the <c>user_identity</c> row, and nothing
/// crosses a URL.
/// </summary>
/// <param name="Version">Payload schema version; a value this build does not know is refused.</param>
/// <param name="UserId">The account the link was started for and confirmed by.</param>
/// <param name="Provider">The provider's registered name (<c>github</c>, <c>google</c>).</param>
/// <param name="Subject">The provider's subject for this identity — personal data, and never written anywhere else.</param>
/// <param name="Email">The address the provider verified; personal data, never logged.</param>
/// <param name="DisplayName">The provider's profile name, or null.</param>
/// <param name="IssuedAt">When the callback staged it, for the lifetime check and the <c>expires_at</c> of the pending answer.</param>
/// <param name="LinkId">A random id for this staging, so two stagings are distinguishable without naming the subject.</param>
internal sealed record LinkConfirmPayload(
    int Version,
    Guid UserId,
    string Provider,
    string Subject,
    string Email,
    string? DisplayName,
    DateTimeOffset IssuedAt,
    string LinkId);

/// <summary>The source-generated JSON of <see cref="LinkConfirmPayload"/> (never on the wire; inside the protected cookie only).</summary>
[JsonSerializable(typeof(LinkConfirmPayload))]
internal sealed partial class LinkConfirmJsonContext : JsonSerializerContext
{
}

/// <summary>
/// The staged second-provider link (02.3.1-T2.2, Q4): <c>__Host-evidgate_link</c>, written by the login
/// callback when the state cookie's purpose is <c>link</c>, read and deleted by the three
/// <c>/v1/auth/link/pending</c> and <c>/v1/auth/link/confirm</c> routes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a cookie and not a row.</b> The callback has verified the identity of a provider the signed-in user
/// is adding, and the story requires an EXPLICIT confirm before anything is written (AC6): staged state that
/// expires in ten minutes, is readable by no script and is bound to one user is exactly that promise, and a
/// row would need a table, a sweep and a status machine for the same ten minutes.
/// </para>
/// <para>
/// <b>Attributes.</b> The three <c>__Host-</c> rules (<c>Secure</c>, <c>Path=/</c>, no <c>Domain</c>) plus
/// <c>HttpOnly</c> and <c>SameSite=Lax</c>, so a cross-site request does not carry it and no other origin can
/// shadow it. The lifetime is a fixed ten-minute window — a constant, not a configuration key: nothing may
/// widen how long a verified identity waits for a confirmation.
/// </para>
/// <para>
/// <b>Confidentiality.</b> The payload names an email and a subject, so the value is encrypted and
/// integrity-protected with ASP.NET Core Data Protection under <see cref="ProtectorPurpose"/> (the login's own
/// key ring, a purpose of its own), and a tampered, foreign or expired value reads as "nothing staged"
/// instead of failing the route. A test asserts the plaintext address and subject are absent from the cookie
/// value.
/// </para>
/// <para>
/// <b>Binding.</b> The payload carries <see cref="LinkConfirmPayload.UserId"/> and every route compares it
/// with the session's user, so a confirm cookie a browser holds for account A can never attach an identity to
/// account B, even if the two are both signed in on the same host.
/// </para>
/// </remarks>
/// <param name="protection">The host's Data Protection provider (the login's shared ring).</param>
/// <param name="clock">The clock the payload is stamped with and its lifetime checked against.</param>
internal sealed class LinkConfirmCookie(
    IDataProtectionProvider protection,
    TimeProvider clock)
{
    /// <summary>Cookie name; the <c>__Host-</c> prefix is part of the contract.</summary>
    internal const string Name = "__Host-evidgate_link";

    /// <summary>Data Protection purpose of the cookie value (its own, so no other cookie's value reads as this one).</summary>
    internal const string ProtectorPurpose = "Evidgate.Login.LinkConfirmCookie.v1";

    /// <summary>Payload schema version this build writes and reads.</summary>
    internal const int PayloadVersion = 1;

    /// <summary>Lifetime of a staged link, in seconds (Q4: at most ten minutes).</summary>
    internal const int LifetimeSeconds = 600;

    /// <summary>Bounds of the fields the provider fills, so a hostile provider cannot inflate the cookie.</summary>
    internal const int MaxProviderLength = 64;

    internal const int MaxSubjectLength = 512;

    internal const int MaxEmailLength = 512;

    internal const int MaxDisplayNameLength = 200;

    /// <summary>Bounds of the staging id this class mints.</summary>
    internal const int LinkIdBytes = 16;

    /// <summary>Tolerated clock difference for a payload stamped in the future.</summary>
    private static readonly TimeSpan FutureSkew = TimeSpan.FromSeconds(60);

    private readonly IDataProtector _protector = protection.CreateProtector(ProtectorPurpose);

    /// <summary>Lifetime of a written cookie and of the staging it carries.</summary>
    public static TimeSpan Lifetime => TimeSpan.FromSeconds(LifetimeSeconds);

    /// <summary>
    /// Writes the protected payload as this response's staged-link cookie.
    /// </summary>
    /// <param name="context">The request the browser is answering.</param>
    /// <param name="userId">The account the link was started for and whose session the callback verified.</param>
    /// <param name="identity">The provider-verified identity the callback received.</param>
    /// <exception cref="ArgumentException">
    /// The account is the empty id, or a provider field is empty or longer than its bound: the callback
    /// verified this identity and a value that cannot be staged is a caller bug, refused before a cookie the
    /// confirm route would have to guess about exists.
    /// </exception>
    public void Write(HttpContext context, Guid userId, VerifiedIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(identity);

        if (userId == Guid.Empty)
        {
            throw new ArgumentException("a staged link names an account, never the empty id", nameof(userId));
        }

        if (!IsBounded(identity.Provider, MaxProviderLength)
            || !IsBounded(identity.Subject, MaxSubjectLength)
            || !IsBounded(identity.Email, MaxEmailLength)
            || (identity.DisplayName is { } name && name.Length > MaxDisplayNameLength))
        {
            throw new ArgumentException("the verified identity carries a field this cookie cannot stage", nameof(identity));
        }

        var payload = new LinkConfirmPayload(
            PayloadVersion,
            userId,
            identity.Provider,
            identity.Subject,
            identity.Email,
            string.IsNullOrWhiteSpace(identity.DisplayName) ? null : identity.DisplayName,
            clock.GetUtcNow(),
            NewLinkId());

        context.Response.Cookies.Append(
            Name,
            _protector.Protect(JsonSerializer.Serialize(payload, LinkConfirmJsonContext.Default.LinkConfirmPayload)),
            Attributes(Lifetime));
    }

    /// <summary>
    /// Reads a cookie value: null when it is absent, unprotected by another key or purpose, unparsable,
    /// unknown in version, missing a field, or outside its lifetime. Never throws for a client-supplied value,
    /// and never logs it.
    /// </summary>
    /// <param name="value">The raw <c>__Host-evidgate_link</c> value, or null.</param>
    public LinkConfirmPayload? Read(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        LinkConfirmPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize(_protector.Unprotect(value), LinkConfirmJsonContext.Default.LinkConfirmPayload);
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (FormatException)
        {
            // Not even base64url: the client put that value there, so it is no staged link, not a fault.
            return null;
        }
        catch (JsonException)
        {
            return null;
        }

        if (payload is null
            || payload.Version != PayloadVersion
            || payload.UserId == Guid.Empty
            || !IsBounded(payload.LinkId, 64)
            || !IsBounded(payload.Provider, MaxProviderLength)
            || !IsBounded(payload.Subject, MaxSubjectLength)
            || !IsBounded(payload.Email, MaxEmailLength)
            || payload.DisplayName?.Length > MaxDisplayNameLength)
        {
            return null;
        }

        var age = clock.GetUtcNow() - payload.IssuedAt;

        return age <= Lifetime && age >= -FutureSkew ? payload : null;
    }

    /// <summary>
    /// Marks the cookie for deletion. Every route that reads it calls this on every outcome it does not
    /// retry: the staging is one confirmed link, and a client must not be able to replay a value the server
    /// already acted on.
    /// </summary>
    /// <param name="context">The request the browser is answering.</param>
    public static void Delete(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Response.Cookies.Delete(Name, Attributes(maxAge: null));
    }

    /// <summary>Whether <paramref name="value"/> is present (a protected payload can carry an omitted field) and within <paramref name="max"/> characters.</summary>
    private static bool IsBounded(string? value, int max) => value is { Length: > 0 } && value.Length <= max;

    /// <summary>A fresh 128-bit staging id as base64url (22 characters): random, and names nothing.</summary>
    private static string NewLinkId() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(LinkIdBytes))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

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
