using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Evidgate.BuildingBlocks.Pipeline.Serialization;
using Evidgate.BuildingBlocks.SharedKernel.Serialization;
using Evidgate.Modules.Authoring.Trackers.Application;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Authoring.Trackers.Infrastructure;

/// <summary>
/// The authorization-code flow of Jira Cloud and Linear. It only ever builds a consent URL and posts one token
/// exchange to a fixed host; the HTTP client underneath refuses redirects and every host off the allowlist.
/// </summary>
/// <remarks>
/// Linear is called with PKCE (S256). Jira Cloud is not: Atlassian's 3LO flow is for a confidential client that proves
/// itself with its client secret, and its documentation offers no code challenge, so none is sent.
/// </remarks>
/// <param name="http">The guarded client registered by <see cref="TrackerServiceCollectionExtensions"/>.</param>
/// <param name="jira">The Jira Cloud client settings.</param>
/// <param name="linear">The Linear client settings.</param>
/// <param name="clock">The clock the token expiry is measured on.</param>
internal sealed class TrackerProviderClient(
    HttpClient http,
    IOptions<JiraCloudTrackerOptions> jira,
    IOptions<LinearTrackerOptions> linear,
    TimeProvider clock)
    : ITrackerProviderClient
{
    /// <summary>The largest token response read, in bytes; a longer body is refused unread.</summary>
    internal const int MaxResponseBytes = 64 * 1024;

    /// <summary>
    /// One deadline for the whole exchange, headers and body. The HttpClient timeout stops at the headers when the body
    /// is read afterwards, so a body that stalls would otherwise hold the callback open.
    /// </summary>
    internal static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    /// <inheritdoc />
    public bool IsConfigured(TrackerProvider provider) => Settings(provider).IsConfigured;

    /// <inheritdoc />
    public string RedirectUri(TrackerProvider provider) => Settings(provider).RedirectUri ?? string.Empty;

    /// <inheritdoc />
    public string Authorize(TrackerProvider provider, string state, string? pkceVerifier)
    {
        ArgumentException.ThrowIfNullOrEmpty(state);

        var settings = Settings(provider);
        if (provider == TrackerProvider.Linear)
        {
            ArgumentException.ThrowIfNullOrEmpty(pkceVerifier);

            return QueryHelpers.AddQueryString(TrackerEndpoints.LinearAuthorizeUrl, new Dictionary<string, string?>
            {
                ["client_id"] = settings.ClientId,
                ["redirect_uri"] = settings.RedirectUri,
                ["response_type"] = "code",
                ["scope"] = TrackerProviders.LinearScope,
                ["state"] = state,
                ["prompt"] = "consent",
                ["code_challenge"] = Challenge(pkceVerifier),
                ["code_challenge_method"] = "S256",
            });
        }

        return QueryHelpers.AddQueryString(TrackerEndpoints.JiraAuthorizeUrl, new Dictionary<string, string?>
        {
            ["audience"] = TrackerEndpoints.JiraAudience,
            ["client_id"] = settings.ClientId,
            ["scope"] = TrackerProviders.JiraScope,
            ["redirect_uri"] = settings.RedirectUri,
            ["state"] = state,
            ["response_type"] = "code",
            ["prompt"] = "consent",
        });
    }

    /// <inheritdoc />
    public async Task<TrackerToken?> ExchangeAsync(
        TrackerProvider provider,
        string code,
        string? pkceVerifier,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);

        var settings = Settings(provider);
        if (!settings.IsConfigured)
        {
            return null;
        }

        using var request = BuildExchange(provider, settings, code, pkceVerifier);
        using var deadline = new CancellationTokenSource(Deadline, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token)
                .ConfigureAwait(false);

            // A redirect is not a success: it is never followed, so a token request cannot be replayed at another host.
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await BoundedHttpBody.ReadJsonAsync(response.Content, MaxResponseBytes, requireObject: true, linked.Token)
                .ConfigureAwait(false);
            if (body.Status != BoundedBodyStatus.Ok)
            {
                return null;
            }

            using (body.Document)
            {
                return Parse(provider, body.Document!.RootElement);
            }
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The deadline above, or the client's own timeout: an outage of the provider, not a cancelled request.
            return null;
        }
    }

    private static HttpRequestMessage BuildExchange(TrackerProvider provider, ITrackerClientSettings settings, string code, string? verifier)
    {
        HttpRequestMessage request;
        if (provider == TrackerProvider.Linear)
        {
            request = new HttpRequestMessage(HttpMethod.Post, TrackerEndpoints.LinearTokenUrl)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["code"] = code,
                    ["redirect_uri"] = settings.RedirectUri!,
                    ["client_id"] = settings.ClientId!,
                    ["client_secret"] = settings.ClientSecret!,
                    ["code_verifier"] = verifier ?? string.Empty,
                }),
            };
        }
        else
        {
            request = new HttpRequestMessage(HttpMethod.Post, TrackerEndpoints.JiraTokenUrl)
            {
                Content = JsonContent.Create(new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["client_id"] = settings.ClientId!,
                    ["client_secret"] = settings.ClientSecret!,
                    ["code"] = code,
                    ["redirect_uri"] = settings.RedirectUri!,
                }),
            };
        }

        request.Headers.Accept.ParseAdd("application/json");
        return request;
    }

    private TrackerToken? Parse(TrackerProvider provider, JsonElement root)
    {
        var access = Text(root, "access_token");
        if (string.IsNullOrEmpty(access) || ExceedsTokenCap(access))
        {
            return null;
        }

        var refresh = Text(root, "refresh_token");
        if (refresh is not null && ExceedsTokenCap(refresh))
        {
            return null;
        }

        var scopes = ScopesOf(provider);
        if (!ScopesMatch(root, scopes))
        {
            return null;
        }

        DateTimeOffset? expiresAt = null;
        if (root.TryGetProperty("expires_in", out var expires) && expires.ValueKind == JsonValueKind.Number && expires.TryGetInt64(out var seconds)
            && seconds is > 0 and <= 31_536_000)
        {
            expiresAt = clock.GetUtcNow().AddSeconds(seconds);
        }

        return new TrackerToken(access, string.IsNullOrEmpty(refresh) ? null : refresh, expiresAt, scopes);
    }

    private static bool ExceedsTokenCap(string token) => Encoding.UTF8.GetByteCount(token) > TrackerTokenProtector.MaxPlaintextBytes;

    // The grant must be exactly what was asked for: a wider grant is refused, never stored.
    private static bool ScopesMatch(JsonElement root, IReadOnlyList<string> expected)
    {
        if (!root.TryGetProperty("scope", out var scope))
        {
            return true;
        }

        var granted = new HashSet<string>(StringComparer.Ordinal);
        switch (scope.ValueKind)
        {
            case JsonValueKind.String:
                granted.UnionWith((scope.GetString() ?? string.Empty).Split([' ', ','], StringSplitOptions.RemoveEmptyEntries));
                break;
            case JsonValueKind.Array:
                foreach (var item in scope.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                    {
                        return false;
                    }

                    granted.Add(item.GetString() ?? string.Empty);
                }

                break;
            default:
                return false;
        }

        return granted.SetEquals(expected);
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string[] ScopesOf(TrackerProvider provider) =>
        TrackerProviders.Scope(provider).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static string Challenge(string verifier) =>
        WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private ITrackerClientSettings Settings(TrackerProvider provider) => provider switch
    {
        TrackerProvider.JiraCloud => jira.Value,
        TrackerProvider.Linear => linear.Value,
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };
}
