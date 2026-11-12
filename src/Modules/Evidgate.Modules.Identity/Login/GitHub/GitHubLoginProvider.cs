using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Identity.Login.GitHub;

/// <summary>
/// The GitHub half of federated login (02.3.1-T1.3): an OAuth2 authorization-code flow with PKCE and NO
/// ID token (ruling T6). The start route gets the authorization URL, the callback hands the code here,
/// and this class exchanges it at the token endpoint and then reads the subject from <c>GET /user</c> and
/// the email from <c>GET /user/emails</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every outbound call happens in <see cref="CompleteAsync"/>, which the callback runs BEFORE any
/// database transaction is opened (P5). The class takes no database dependency at all, so it cannot
/// join one by accident.
/// </para>
/// <para>
/// Failures are typed and carry no provider text. The token exchange maps a rejected code (a 4xx, or
/// GitHub's 200-with-<c>error</c> body), a missing or blank <c>access_token</c>, malformed JSON or an
/// unsupported <c>token_type</c> to <see cref="LoginFailure.TokenInvalid"/>, while a timeout, transport
/// error, 5xx, 429 or an oversized body is <see cref="LoginFailure.ProviderUnavailable"/>. The two profile
/// reads map 401 and any other 4xx (403, 408 and 429 excepted) to <see cref="LoginFailure.TokenInvalid"/>
/// and 403/408/429/5xx/timeout/transport to <see cref="LoginFailure.ProviderUnavailable"/>; a document the
/// provider sent that cannot be interpreted at all is an outage, never a dead token, while a user object
/// without a usable numeric <c>id</c> or an account without a verified email is a token failure (AC4).
/// Nothing observed here — code, verifier, nonce, tokens, secret, email or subject — is logged.
/// </para>
/// <para>
/// The token client it is constructed with has a hard timeout, no redirect following and the fixed S4
/// allowlist in front of it, and its authorization code is single-use, so the token POST is never
/// retried. The two profile GETs are idempotent, so they run on a second client with one bounded retry,
/// and each call reads its body under a deadline of its own: the client timeout stops at the headers, so
/// a body that stalls after them is <see cref="LoginFailure.ProviderUnavailable"/>, never a hang. The
/// access token is used for those two calls only and is never stored, logged or returned.
/// </para>
/// </remarks>
internal sealed partial class GitHubLoginProvider(
    HttpClient tokenClient,
    HttpClient apiClient,
    IOptions<GitHubLoginOptions> options,
    ILogger<GitHubLoginProvider> logger) : ILoginProvider
{
    /// <summary>Route value of this provider.</summary>
    internal const string ProviderName = "github";

    /// <summary>
    /// Scopes the flow asks for: the profile (the numeric id and the display name) and the verified
    /// addresses of the account. GitHub grants <c>user:email</c> per address, so <c>GET /user/emails</c>
    /// lists the primary one as well.
    /// </summary>
    private const string Scope = "read:user user:email";

    /// <summary>Fixed API version header GitHub asks every REST caller to send.</summary>
    private const string ApiVersion = "2022-11-28";

    /// <summary>Product token of the fixed <c>User-Agent</c>; GitHub refuses an API call without one.</summary>
    private const string UserAgent = "Evidgate";

    /// <summary>The only <c>token_type</c> this flow can use.</summary>
    private const string TokenTypeBearer = "bearer";

    /// <summary>Largest authorization code accepted from the provider redirect.</summary>
    private const int MaxCodeLength = 2048;

    /// <summary>Largest PKCE verifier accepted (RFC 7636 allows 43..128).</summary>
    private const int MaxCodeVerifierLength = 128;

    /// <summary>Largest access token accepted; a longer value is not a GitHub token and would be a header bomb.</summary>
    private const int MaxAccessTokenLength = 1024;

    /// <summary>Largest display name stored (<c>user.display_name</c> has no check of its own).</summary>
    private const int MaxDisplayNameLength = 200;

    /// <summary>Largest email accepted (<c>user.email</c> is <c>citext</c>; RFC 5321 bounds the address at 320).</summary>
    private const int MaxEmailLength = 320;

    private readonly GitHubLoginOptions _options = options.Value;

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
        && Configured(_options.AuthorizeUrl)
        && Configured(_options.TokenUrl)
        && Configured(_options.ApiBaseUrl);

    private static bool Configured(string? value) => !string.IsNullOrWhiteSpace(value);

    /// <inheritdoc />
    public Uri BuildAuthorizationUrl(LoginAuthorizationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // GitHub is OAuth2, so there is no nonce to echo: only the anti-CSRF state and the PKCE challenge
        // are required here.
        if (string.IsNullOrEmpty(request.State) || string.IsNullOrEmpty(request.CodeChallenge))
        {
            throw new ArgumentException("state and code challenge are required to build an authorization URL", nameof(request));
        }

        var query = new StringBuilder()
            .Append("response_type=code")
            .Append("&client_id=").Append(Uri.EscapeDataString(_options.ClientId ?? string.Empty))
            .Append("&redirect_uri=").Append(Uri.EscapeDataString(request.RedirectUri.AbsoluteUri))
            .Append("&scope=").Append(Uri.EscapeDataString(Scope))
            .Append("&state=").Append(Uri.EscapeDataString(request.State))
            .Append("&code_challenge=").Append(Uri.EscapeDataString(request.CodeChallenge))
            .Append("&code_challenge_method=S256");

        return new UriBuilder(new Uri(_options.AuthorizeUrl!, UriKind.Absolute)) { Query = query.ToString() }.Uri;
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

        var (accessToken, tokenFailure, tokenCategory) = await ExchangeAsync(request, cancellationToken).ConfigureAwait(false);
        if (tokenFailure is { } exchangeFailure)
        {
            LogCompletionFailed(tokenCategory);

            return LoginProviderResult.Fail(exchangeFailure);
        }

        var (identity, identityFailure, identityCategory) = await ReadIdentityAsync(accessToken!, cancellationToken).ConfigureAwait(false);
        if (identityFailure is { } failure)
        {
            LogCompletionFailed(identityCategory);

            return LoginProviderResult.Fail(failure);
        }

        return LoginProviderResult.Ok(identity!);
    }

    /// <summary>
    /// Posts the authorization code with the PKCE verifier and reads the <c>access_token</c> only. The
    /// category returned is one of the closed log categories; it never contains provider text.
    /// </summary>
    private async Task<(string? AccessToken, LoginFailure? Failure, string Category)> ExchangeAsync(
        LoginCallbackRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.Code) || request.Code.Length > MaxCodeLength
            || string.IsNullOrEmpty(request.CodeVerifier) || request.CodeVerifier.Length > MaxCodeVerifierLength
            || !request.RedirectUri.IsAbsoluteUri)
        {
            return (null, LoginFailure.TokenInvalid, CategoryTokenInvalid);
        }

        // The form body, never the query: the code, the verifier and the client secret are all
        // credentials, and a URL travels through logs and proxies that a body does not.
        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["client_id"] = _options.ClientId!,
            ["client_secret"] = _options.ClientSecret!,
            ["code"] = request.Code,
            ["redirect_uri"] = request.RedirectUri.AbsoluteUri,
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
            // GitHub answers form-encoded unless the caller asks for JSON, so the Accept header is what
            // makes the error bodies and the token readable here.
            using var post = new HttpRequestMessage(HttpMethod.Post, new Uri(_options.TokenUrl!, UriKind.Absolute))
            {
                Content = new FormUrlEncodedContent(form),
            };
            post.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            response = await tokenClient.SendAsync(post, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (GitHubProviderFailure.IsUnavailable(ex, cancellationToken))
        {
            return (null, LoginFailure.ProviderUnavailable, CategoryProviderUnavailable);
        }

        using (response)
        {
            try
            {
                return await ReadTokenResponseAsync(response, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (GitHubProviderFailure.IsUnavailable(ex, cancellationToken))
            {
                // The deadline above bounds this read too, so a stalled body lands here as unavailable
                // instead of hanging the callback or surfacing as a 500.
                return (null, LoginFailure.ProviderUnavailable, CategoryProviderUnavailable);
            }
        }
    }

    /// <summary>
    /// Reads the token response: status mapping, the size cap and the <c>access_token</c> extraction. The
    /// caller's deadline bounds the body read.
    /// </summary>
    private async Task<(string? AccessToken, LoginFailure? Failure, string Category)> ReadTokenResponseAsync(
        HttpResponseMessage response,
        CancellationToken bodyToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;

            // 429 is the provider protecting itself and 408 a slow upstream (unavailable, retry later); any
            // other 4xx is the provider refusing this code (bad_verification_code, invalid_client, ...): a
            // token failure.
            if (status is 408 or 429)
            {
                return (null, LoginFailure.ProviderUnavailable, CategoryProviderUnavailable);
            }

            return status is >= 400 and < 500
                ? (null, LoginFailure.TokenInvalid, CategoryTokenEndpointRejected)
                : (null, LoginFailure.ProviderUnavailable, CategoryProviderUnavailable);
        }

        var read = await BoundedHttpBody.ReadJsonAsync(response.Content, _options.MaxResponseBytes, requireObject: true, bodyToken)
            .ConfigureAwait(false);

        return read.Status switch
        {
            BoundedBodyStatus.Ok => ReadAccessToken(read.Document!),
            BoundedBodyStatus.TooLarge => (null, LoginFailure.ProviderUnavailable, CategoryProviderUnavailable),

            // A 200 whose body is not a JSON object is not a token this flow can use, and it is not an outage either.
            _ => (null, LoginFailure.TokenInvalid, CategoryTokenEndpointRejected),
        };
    }

    /// <summary>Reads <c>access_token</c> from the token response; anything else in the body is ignored, never logged.</summary>
    private static (string? AccessToken, LoginFailure? Failure, string Category) ReadAccessToken(JsonDocument document)
    {
        using (document)
        {
            // GitHub answers a rejected code with 200 and {"error": "bad_verification_code"} when the
            // caller asked for JSON, so the body itself has to carry the refusal.
            if (document.RootElement.TryGetProperty("error", out var error) && error.ValueKind is not JsonValueKind.Null)
            {
                return (null, LoginFailure.TokenInvalid, CategoryTokenEndpointRejected);
            }

            if (!document.RootElement.TryGetProperty("access_token", out var token) || token.ValueKind != JsonValueKind.String)
            {
                return (null, LoginFailure.TokenInvalid, CategoryTokenEndpointRejected);
            }

            var accessToken = token.GetString();
            if (string.IsNullOrWhiteSpace(accessToken) || accessToken.Length > MaxAccessTokenLength)
            {
                return (null, LoginFailure.TokenInvalid, CategoryTokenEndpointRejected);
            }

            if (document.RootElement.TryGetProperty("token_type", out var tokenType)
                && (tokenType.ValueKind != JsonValueKind.String
                    || !string.Equals(tokenType.GetString(), TokenTypeBearer, StringComparison.OrdinalIgnoreCase)))
            {
                return (null, LoginFailure.TokenInvalid, CategoryTokenEndpointRejected);
            }

            return (accessToken, null, CategoryVerified);
        }
    }

    /// <summary>
    /// Reads the subject and the display name from <c>GET /user</c> and the verified email from
    /// <c>GET /user/emails</c>, in that order. Both reads are sequential on purpose: the email call is
    /// pointless for an account whose id cannot be read.
    /// </summary>
    private async Task<(VerifiedIdentity? Identity, LoginFailure? Failure, string Category)> ReadIdentityAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        var (userDocument, userFailure, userCategory) = await GetJsonAsync(
            () => ApiRequest("user", accessToken),
            cancellationToken).ConfigureAwait(false);

        if (userFailure is { } userReadFailure)
        {
            return (null, userReadFailure, userCategory);
        }

        string subject;
        string? displayName;
        using (userDocument)
        {
            var user = userDocument!.RootElement;
            if (user.ValueKind != JsonValueKind.Object)
            {
                return (null, LoginFailure.ProviderUnavailable, CategoryProviderUnavailable);
            }

            if (!TryReadSubject(user, out subject))
            {
                return (null, LoginFailure.TokenInvalid, CategoryTokenInvalid);
            }

            displayName = ReadDisplayName(user);
        }

        var (emailsDocument, emailsFailure, emailsCategory) = await GetJsonAsync(
            () => ApiRequest("user/emails", accessToken),
            cancellationToken).ConfigureAwait(false);

        if (emailsFailure is { } emailsReadFailure)
        {
            return (null, emailsReadFailure, emailsCategory);
        }

        using (emailsDocument)
        {
            var emails = emailsDocument!.RootElement;
            if (emails.ValueKind != JsonValueKind.Array)
            {
                return (null, LoginFailure.ProviderUnavailable, CategoryProviderUnavailable);
            }

            // AC4: only a verified address may ever bind, so an account whose every address is unverified
            // is refused here rather than bound to its primary one.
            var email = SelectVerifiedEmail(emails);

            return email is null
                ? (null, LoginFailure.TokenInvalid, CategoryTokenInvalid)
                : (new VerifiedIdentity(ProviderName, subject, email, EmailVerified: true, displayName), null, CategoryVerified);
        }
    }

    /// <summary>
    /// Sends one API GET and parses its body. The failure mapping is the AC8 one: 401 and any other 4xx
    /// (403, 408 and 429 excepted) is a dead token, while 403/408/429/5xx, a redirect, a timeout, a
    /// transport error, an oversized body or a body that is not JSON is the provider being unavailable.
    /// </summary>
    private async Task<(JsonDocument? Document, LoginFailure? Failure, string Category)> GetJsonAsync(
        Func<HttpRequestMessage> createRequest,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(GitHubLoginHttpClients.ApiTotalTimeout(_options));

        HttpResponseMessage response;
        try
        {
            using var request = createRequest();

            response = await apiClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (GitHubProviderFailure.IsUnavailable(ex, cancellationToken))
        {
            return (null, LoginFailure.ProviderUnavailable, CategoryProviderUnavailable);
        }

        using (response)
        {
            try
            {
                return await ReadJsonAsync(response, deadline.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (GitHubProviderFailure.IsUnavailable(ex, cancellationToken))
            {
                return (null, LoginFailure.ProviderUnavailable, CategoryProviderUnavailable);
            }
        }
    }

    /// <summary>Status mapping, the size cap and the parse of one API answer; the caller's deadline bounds the read.</summary>
    private async Task<(JsonDocument? Document, LoginFailure? Failure, string Category)> ReadJsonAsync(
        HttpResponseMessage response,
        CancellationToken bodyToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            return (int)response.StatusCode switch
            {
                // The access token is not accepted: the only 4xx that is a token failure by itself.
                401 => (null, LoginFailure.TokenInvalid, CategoryTokenInvalid),

                // 403 is GitHub's secondary rate limit and abuse detection, 408 a slow upstream and 429 the
                // primary rate limit: all three say "later", not "bad token".
                403 or 408 or 429 => (null, LoginFailure.ProviderUnavailable, CategoryProviderUnavailable),

                // 3xx (a redirect is never followed, S4) and 5xx are outages.
                < 400 or >= 500 => (null, LoginFailure.ProviderUnavailable, CategoryProviderUnavailable),

                _ => (null, LoginFailure.TokenInvalid, CategoryTokenInvalid),
            };
        }

        var read = await BoundedHttpBody.ReadJsonAsync(response.Content, _options.MaxResponseBytes, requireObject: false, bodyToken)
            .ConfigureAwait(false);

        return read.Status == BoundedBodyStatus.Ok
            ? (read.Document, null, CategoryVerified)
            : (null, LoginFailure.ProviderUnavailable, CategoryProviderUnavailable);
    }

    /// <summary>The authenticated API request: bearer token, the fixed JSON media type, the API version and a user agent.</summary>
    private HttpRequestMessage ApiRequest(string relativePath, string accessToken)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(new Uri(_options.ApiBaseUrl!, UriKind.Absolute), relativePath));

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", ApiVersion);
        request.Headers.UserAgent.ParseAdd(UserAgent);

        return request;
    }

    /// <summary>
    /// The subject is the numeric <c>id</c> as a decimal string (T6). A string, a float, zero, a negative
    /// number, an id too large for <see cref="long"/> or a missing member is not a GitHub user id.
    /// </summary>
    private static bool TryReadSubject(JsonElement user, out string subject)
    {
        subject = string.Empty;

        if (!user.TryGetProperty("id", out var id)
            || id.ValueKind != JsonValueKind.Number
            || !id.TryGetInt64(out var value)
            || value <= 0)
        {
            return false;
        }

        subject = value.ToString(CultureInfo.InvariantCulture);

        return true;
    }

    /// <summary>The display name is <c>name</c>, else the <c>login</c>; trimmed, capped and null when blank.</summary>
    private static string? ReadDisplayName(JsonElement user) =>
        ReadDisplayName(user, "name") ?? ReadDisplayName(user, "login");

    private static string? ReadDisplayName(JsonElement user, string property)
    {
        if (!user.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = element.GetString()?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        return value.Length > MaxDisplayNameLength ? value[..MaxDisplayNameLength] : value;
    }

    /// <summary>
    /// The primary verified address, else the first verified one. Only an entry whose <c>verified</c> is
    /// the JSON boolean <c>true</c> counts: a primary-but-unverified address never binds (AC4), and a
    /// string "true" is not a verification either.
    /// </summary>
    private static string? SelectVerifiedEmail(JsonElement emails)
    {
        string? primary = null;
        string? firstVerified = null;

        foreach (var entry in emails.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !IsTrue(entry, "verified")
                || !entry.TryGetProperty("email", out var element)
                || element.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var email = element.GetString();
            if (string.IsNullOrWhiteSpace(email))
            {
                continue;
            }

            var normalized = email.Trim().ToLowerInvariant();
            if (normalized.Length > MaxEmailLength)
            {
                continue;
            }

            firstVerified ??= normalized;

            if (IsTrue(entry, "primary"))
            {
                primary ??= normalized;
            }
        }

        return primary ?? firstVerified;
    }

    private static bool IsTrue(JsonElement entry, string property) =>
        entry.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.True;

    private const string CategoryUnconfigured = "unconfigured";
    private const string CategoryTokenInvalid = "token_invalid";
    private const string CategoryTokenEndpointRejected = "token_endpoint_rejected";
    private const string CategoryProviderUnavailable = "provider_unavailable";
    private const string CategoryVerified = "verified";

    [LoggerMessage(
        EventId = 2432,
        Level = LogLevel.Warning,
        Message = "GitHub login completion failed: {Category}")]
    private partial void LogCompletionFailed(string category);
}
