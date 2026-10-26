using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Identity.Sessions.Http;

/// <summary>
/// The antiforgery half of the session contract (02.3.2-T1, D6; TL T38, 00 §9.1): ASP.NET Core's own
/// double-submit pair — the <c>__Host-evidgate_af</c> cookie-token and the script-readable
/// <c>XSRF-TOKEN</c> request token, echoed by the page in <c>X-XSRF-TOKEN</c> — with one addition the
/// framework cannot make by itself: <b>the token is bound to the session id</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the binding.</b> The framework validates that the header matches the cookie, and nothing more. Two
/// browser tabs signed in as two different accounts therefore hold interchangeable pairs, and a token minted
/// before a rotation stays valid after it. The additional data is the session id, so a pair issued for session A
/// (or for a session that has since been rotated away) fails validation on session B — which is exactly the
/// window a forced rotation closes.
/// </para>
/// <para>
/// <b>Two slots, because one request can rotate.</b> VALIDATION is judged against the session the presented pair
/// was minted under (<see cref="PresentedSessionIdKey"/>, published by the authenticator and never overwritten);
/// MINTING puts the session the response is issuing into the token (<see cref="IssuanceSessionIdKey"/>, published
/// by <see cref="SessionCookieWriter"/>). A single slot cannot express both: a mutation on a rotation-required
/// session authenticates on the old row, rotates, and must still validate the pair the client sent for the OLD
/// session — comparing it against the new id would refuse the very request that performed the rotation.
/// </para>
/// <para>
/// <b>Fail-closed.</b> Validation compares against the id this request authenticated as and answers false for
/// anything else — including an empty supplemental payload, which is a pair minted without a published session
/// (a token from a build that predates this binding). A route that validates without having authenticated never
/// passes.
/// </para>
/// </remarks>
internal sealed class SessionAntiforgeryDataProvider : IAntiforgeryAdditionalDataProvider
{
    /// <summary>The request slot of the session the presented pair was minted under (validation, never overwritten).</summary>
    internal static readonly object PresentedSessionIdKey = new();

    /// <summary>The request slot of the session a pair is being minted for (issuance).</summary>
    internal static readonly object IssuanceSessionIdKey = new();

    /// <inheritdoc />
    public string GetAdditionalData(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return (SessionIdOf(context, IssuanceSessionIdKey) ?? SessionIdOf(context, PresentedSessionIdKey))?.ToString("D") ?? string.Empty;
    }

    /// <inheritdoc />
    public bool ValidateAdditionalData(HttpContext context, string additionalData)
    {
        ArgumentNullException.ThrowIfNull(context);

        var presented = SessionIdOf(context, PresentedSessionIdKey);

        // Fail closed: an unbound pair (a token minted without a session published, e.g. by a build that predates
        // this binding) validates against nothing, so it never passes. The enforcement layer is the only caller and
        // it validates only requests a session authenticated, so "no presented session" here means the pair is not
        // one this deployment issued for a session — refuse it rather than accept an unbound token.
        return presented is { } sessionId
            && !string.IsNullOrEmpty(additionalData)
            && string.Equals(additionalData, sessionId.ToString("D"), StringComparison.Ordinal);
    }

    /// <summary>Publishes the session this request authenticated as, for validation of the pair it presented.</summary>
    /// <param name="context">The request.</param>
    /// <param name="sessionId">The session the presented cookies were minted under (the PRE-rotation one).</param>
    internal static void PublishPresented(HttpContext context, Guid sessionId)
    {
        ArgumentNullException.ThrowIfNull(context);

        // First publisher wins: a rotation later in the same request must not move the validation target.
        _ = context.Items.TryAdd(PresentedSessionIdKey, sessionId);
    }

    /// <summary>Publishes the session a pair is being minted for.</summary>
    /// <param name="context">The request.</param>
    /// <param name="sessionId">The session the response issues the pair for (the NEW one, after a rotation).</param>
    internal static void PublishIssuance(HttpContext context, Guid sessionId)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Items[IssuanceSessionIdKey] = sessionId;
    }

    /// <summary>
    /// Issues the pair for the session published in this request: the request token the framework mints — bound to
    /// that session through <see cref="GetAdditionalData"/> — is echoed in the <c>XSRF-TOKEN</c> cookie a page
    /// script reads, and <c>__Host-evidgate_af</c> is written unless the request already carried one.
    /// </summary>
    /// <remarks>
    /// ASP.NET Core reuses a VALID antiforgery cookie token from the request and stores a new one only when the
    /// request had none. That is not a gap in D6: the cookie token is a random value the framework pairs with the
    /// request token, and the half this contract binds to a session is the request token, which is always minted
    /// afresh here — so a rotated pair rejects the predecessor's token while the browser keeps the cookie token it
    /// already holds (a rotation response therefore refreshes <c>XSRF-TOKEN</c> alone).
    /// </remarks>
    /// <param name="context">The request whose response carries both cookies.</param>
    /// <param name="antiforgery">The antiforgery service.</param>
    internal static void Issue(HttpContext context, IAntiforgery antiforgery)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(antiforgery);

        var tokens = antiforgery.GetAndStoreTokens(context);

        if (string.IsNullOrEmpty(tokens.RequestToken))
        {
            return;
        }

        context.Response.Cookies.Append(
            SessionCookieNames.Xsrf,
            tokens.RequestToken,
            new CookieOptions
            {
                // The page script MUST read this one to echo it in the header, so it is not HttpOnly, and it is not
                // __Host- because Angular's withXsrfConfiguration names exactly these two cookies. It carries the
                // same transport and site rules as the session cookie and the same lifetime: a pair that dies with
                // the browser session while the session cookie survives for days leaves every mutation answering
                // 403 `csrf_failed` with nothing to re-issue it (D6).
                HttpOnly = false,
                Secure = true,
                SameSite = SameSiteMode.Lax,
                Path = SessionCookieNames.HostCookiePath,
                IsEssential = true,
                MaxAge = context.RequestServices
                    .GetRequiredService<IOptions<SessionOptions>>()
                    .Value
                    .AbsoluteLifetime,
            });
    }

    private static Guid? SessionIdOf(HttpContext context, object key) =>
        context.Items.TryGetValue(key, out var value) && value is Guid sessionId ? sessionId : null;
}

/// <summary>Composition of the antiforgery pair and its session binding (02.3.2-T1, D6).</summary>
internal static class SessionAntiforgeryServiceCollectionExtensions
{
    /// <summary>
    /// Registers ASP.NET Core's antiforgery services with this contract's names and attributes, and replaces the
    /// framework's no-op additional-data provider with <see cref="SessionAntiforgeryDataProvider"/>.
    /// </summary>
    /// <param name="services">The host's service collection.</param>
    /// <remarks>
    /// <c>Replace</c>, not <c>TryAdd</c>, mirroring the module's own evaluator seam: <c>AddAntiforgery</c> always
    /// registers the framework's default provider, so the session binding must be an explicit swap rather than
    /// a registration-order accident. <c>SuppressXFrameOptionsHeader</c> keeps the API's response headers exactly
    /// as they were: this host serves JSON, never an HTML frame.
    /// </remarks>
    internal static IServiceCollection AddSessionAntiforgery(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddAntiforgery(options =>
        {
            options.HeaderName = SessionCookieNames.XsrfHeader;
            options.Cookie.Name = SessionCookieNames.Antiforgery;
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.Path = SessionCookieNames.HostCookiePath;
            options.Cookie.IsEssential = true;
            options.SuppressXFrameOptionsHeader = true;
        });

        // The pair must live exactly as long as the session cookie, or a browser reopened inside the window
        // authenticates by cookie and then fails every mutation with 403 `csrf_failed` — with no route that
        // re-issues a pair except login and rotation (02.3.2-T1, D6). Both the framework's cookie and the
        // script-readable `XSRF-TOKEN` copy take the absolute lifetime of the session.
        services.AddOptions<AntiforgeryOptions>()
            .PostConfigure<IOptions<SessionOptions>>((options, session) =>
            {
                options.Cookie.MaxAge = session.Value.AbsoluteLifetime;
            });

        services.Replace(
            ServiceDescriptor.Singleton<IAntiforgeryAdditionalDataProvider, SessionAntiforgeryDataProvider>());

        return services;
    }
}
