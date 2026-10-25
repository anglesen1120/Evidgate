using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Identity.Login.Google;

/// <summary>
/// The Google half of federated login (02.3.1-T1.2): an OIDC authorization-code flow with PKCE. The
/// start route gets the authorization URL, the callback hands the code here, and this class exchanges it
/// at the token endpoint and verifies the ID token against the issuer's JWKS.
/// </summary>
/// <remarks>
/// <para>
/// Every outbound call happens in <see cref="CompleteAsync"/>, which the callback runs BEFORE any
/// database transaction is opened (P5). The class takes no database dependency at all, so it cannot
/// join one by accident.
/// </para>
/// <para>
/// Failures are typed and carry no provider text: any 4xx of the token endpoint (a rejected or reused
/// code) is <see cref="LoginFailure.TokenInvalid"/>, while a timeout, transport error, 5xx, 429, an
/// oversized or non-JSON body or an unusable JWKS is <see cref="LoginFailure.ProviderUnavailable"/>.
/// Nothing observed here — code, verifier, nonce, tokens, secret, email or subject — is logged.
/// </para>
/// <para>
/// The typed client it is constructed with has a hard timeout, no redirect following and the fixed S4
/// allowlist in front of it, and its authorization code is single-use, so the token POST is never
/// retried (the JWKS GET is the only retried call). The exchange also bounds the response body itself:
/// the client timeout stops at the headers, so the code reads the body under a deadline of its own and a
/// body that stalls after the headers is <see cref="LoginFailure.ProviderUnavailable"/>, never a hang.
/// </para>
/// </remarks>
internal sealed partial class GoogleLoginProvider(
    HttpClient http,
    GoogleIdTokenValidator validator,
    IOptions<GoogleLoginOptions> options,
    ILogger<GoogleLoginProvider> logger) : ILoginProvider
{
    /// <summary>Route value of this provider.</summary>
    internal const string ProviderName = "google";

    /// <summary>Scopes the flow asks for: the subject, a verified email and a display name.</summary>
    private const string Scope = "openid email profile";

    /// <summary>Largest authorization code accepted from the provider redirect.</summary>
    private const int MaxCodeLength = 2048;

    /// <summary>Largest PKCE verifier accepted (RFC 7636 allows 43..128).</summary>
    private const int MaxCodeVerifierLength = 128;

    private readonly GoogleLoginOptions _options = options.Value;

    /// <inheritdoc />
    public string Name => ProviderName;

    /// <inheritdoc />
    /// <remarks>
    /// Every value must be present and non-blank: a blank deployment setting counts as absent, and a blank
    /// endpoint would make the URLs built from it invalid, so the provider reports itself unconfigured and
    /// the routes answer 503 <c>unavailable</c> instead of failing the request with a 500.
    /// </remarks>
    public bool IsConfigured =>
        Configured(_options.ClientId)
        && Configured(_options.ClientSecret)
        && Configured(_options.Issuer)
        && Configured(_options.AuthorizationEndpoint)
        && Configured(_options.TokenEndpoint)
        && Configured(_options.JwksUri);

    private static bool Configured(string? value) => !string.IsNullOrWhiteSpace(value);

    /// <inheritdoc />
    public Uri BuildAuthorizationUrl(LoginAuthorizationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrEmpty(request.State) || string.IsNullOrEmpty(request.Nonce) || string.IsNullOrEmpty(request.CodeChallenge))
        {
            throw new ArgumentException("state, nonce and code challenge are required to build an authorization URL", nameof(request));
        }

        var query = new StringBuilder()
            .Append("response_type=code")
            .Append("&client_id=").Append(Uri.EscapeDataString(_options.ClientId ?? string.Empty))
            .Append("&redirect_uri=").Append(Uri.EscapeDataString(request.RedirectUri.AbsoluteUri))
            .Append("&scope=").Append(Uri.EscapeDataString(Scope))
            .Append("&state=").Append(Uri.EscapeDataString(request.State))
            .Append("&nonce=").Append(Uri.EscapeDataString(request.Nonce))
            .Append("&code_challenge=").Append(Uri.EscapeDataString(request.CodeChallenge))
            .Append("&code_challenge_method=S256");

        return new UriBuilder(new Uri(_options.AuthorizationEndpoint, UriKind.Absolute)) { Query = query.ToString() }.Uri;
    }

    /// <inheritdoc />
    public async Task<LoginProviderResult> CompleteAsync(LoginCallbackRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!IsConfigured)
        {
            LogCompletionFailed(CategoryUnconfigured);

            return LoginProviderResult.Fail(LoginFailure.ProviderUnavailable);
        }

        var (idToken, failure, category) = await ExchangeAsync(request, cancellationToken).ConfigureAwait(false);
        if (failure is { } exchangeFailure)
        {
            LogCompletionFailed(category);

            return LoginProviderResult.Fail(exchangeFailure);
        }

        var result = await validator.ValidateAsync(idToken!, request.Nonce, cancellationToken).ConfigureAwait(false);
        if (result.Failure is { } verificationFailure)
        {
            LogCompletionFailed(verificationFailure == LoginFailure.ProviderUnavailable ? CategoryProviderUnavailable : CategoryTokenInvalid);
        }

        return result;
    }

    /// <summary>
    /// Posts the authorization code with the PKCE verifier and reads the <c>id_token</c> only. The
    /// category returned is one of the closed log categories; it never contains provider text.
    /// </summary>
    private async Task<(string? IdToken, LoginFailure? Failure, string Category)> ExchangeAsync(
        LoginCallbackRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.Code) || request.Code.Length > MaxCodeLength
            || string.IsNullOrEmpty(request.CodeVerifier) || request.CodeVerifier.Length > MaxCodeVerifierLength
            || !request.RedirectUri.IsAbsoluteUri)
        {
            return (null, LoginFailure.TokenInvalid, CategoryTokenInvalid);
        }

        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "authorization_code",
            ["code"] = request.Code,
            ["redirect_uri"] = request.RedirectUri.AbsoluteUri,
            ["client_id"] = _options.ClientId!,
            ["client_secret"] = _options.ClientSecret!,
            ["code_verifier"] = request.CodeVerifier,
        };

        // One linked deadline for the send AND the body read: with ResponseHeadersRead the client's own
        // timeout stops at the headers, so a body that stalls after them would otherwise hold the request,
        // the pooled connection and the caller's slot until the client disconnects.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.HttpTimeoutSeconds));

        HttpResponseMessage response;
        try
        {
            // ResponseHeadersRead, not PostAsync: with PostAsync the whole provider body would be buffered
            // before MaxResponseBytes could be checked, so a huge or unbounded body would win. Here the body
            // is only read below, under the cap and under the same deadline.
            using var post = new HttpRequestMessage(HttpMethod.Post, new Uri(_options.TokenEndpoint, UriKind.Absolute))
            {
                Content = new FormUrlEncodedContent(form),
            };

            response = await http.SendAsync(post, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (GoogleProviderFailure.IsUnavailable(ex, cancellationToken))
        {
            return (null, LoginFailure.ProviderUnavailable, CategoryProviderUnavailable);
        }

        using (response)
        {
            try
            {
                return await ReadTokenResponseAsync(response, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (GoogleProviderFailure.IsUnavailable(ex, cancellationToken))
            {
                // The deadline above bounds this read too, so a stalled body lands here as unavailable
                // instead of hanging the callback or surfacing as a 500.
                return (null, LoginFailure.ProviderUnavailable, CategoryProviderUnavailable);
            }
        }
    }

    /// <summary>
    /// Reads the token response: status mapping, the size cap and the <c>id_token</c> extraction. The
    /// caller's deadline bounds the body read.
    /// </summary>
    private async Task<(string? IdToken, LoginFailure? Failure, string Category)> ReadTokenResponseAsync(
        HttpResponseMessage response,
        CancellationToken bodyToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;

            // 429 is the provider protecting itself (unavailable, retry later); any other 4xx is the
            // provider refusing this code (invalid_grant, invalid_client, ...): a token failure.
            if (status == 429)
            {
                return (null, LoginFailure.ProviderUnavailable, CategoryProviderUnavailable);
            }

            return status is >= 400 and < 500
                ? (null, LoginFailure.TokenInvalid, CategoryTokenEndpointRejected)
                : (null, LoginFailure.ProviderUnavailable, CategoryProviderUnavailable);
        }

        if (response.Content.Headers.ContentLength is { } declared && declared > _options.MaxResponseBytes)
        {
            return (null, LoginFailure.ProviderUnavailable, CategoryProviderUnavailable);
        }

        var json = await ReadCappedAsync(response.Content, _options.MaxResponseBytes, bodyToken).ConfigureAwait(false);

        return json is null
            ? (null, LoginFailure.ProviderUnavailable, CategoryProviderUnavailable)
            : ReadIdToken(json);
    }

    /// <summary>Reads <c>id_token</c> from the token response; anything else in the body is ignored, never logged.</summary>
    private static (string? IdToken, LoginFailure? Failure, string Category) ReadIdToken(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return (null, LoginFailure.ProviderUnavailable, CategoryProviderUnavailable);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("id_token", out var element)
                || element.ValueKind != JsonValueKind.String)
            {
                return (null, LoginFailure.ProviderUnavailable, CategoryProviderUnavailable);
            }

            var idToken = element.GetString();
            return string.IsNullOrEmpty(idToken) || idToken.Length > GoogleIdTokenValidator.MaxTokenLength
                ? (null, LoginFailure.ProviderUnavailable, CategoryProviderUnavailable)
                : (idToken, null, CategoryVerified);
        }
    }

    /// <summary>Reads at most <paramref name="maxBytes"/>; returns null instead of buffering a longer body.</summary>
    private static async Task<string?> ReadCappedAsync(HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private const string CategoryUnconfigured = "unconfigured";
    private const string CategoryTokenInvalid = "token_invalid";
    private const string CategoryTokenEndpointRejected = "token_endpoint_rejected";
    private const string CategoryProviderUnavailable = "provider_unavailable";
    private const string CategoryVerified = "verified";

    [LoggerMessage(
        EventId = 2431,
        Level = LogLevel.Warning,
        Message = "Google login completion failed: {Category}")]
    private partial void LogCompletionFailed(string category);
}
