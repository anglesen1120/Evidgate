using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Evidgate.BuildingBlocks.Canonicalization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Security;
using Evidgate.Modules.Orchestrator.Contracts;

namespace Evidgate.Modules.Orchestrator.Domain;

/// <summary>A configuration the rules accepted: what is stored and the hash it is stored under.</summary>
/// <param name="BaseUrlsJson">The base URLs as a JSON object.</param>
/// <param name="DependencyVersionsJson">The dependency versions as a JSON object.</param>
/// <param name="SecretRefs">The references, ordinal-sorted and distinct.</param>
/// <param name="RevisionHash"><c>sha256:</c> over the RFC 8785 canonical configuration.</param>
internal sealed record EnvironmentConfiguration(string BaseUrlsJson, string DependencyVersionsJson, IReadOnlyList<string> SecretRefs, string RevisionHash);

/// <summary>
/// The secret-free rules of an environment revision (05.1.2 AC2/AC3): a revision stores base URLs, dependency versions and
/// <c>secret:</c> references only. A secret-shaped value anywhere is refused with <c>secret_value_detected</c> and its
/// position, never its text. The hash covers exactly what is stored, so two revisions with the same content share a hash
/// whatever their field order or their display name.
/// </summary>
internal static partial class EnvironmentRevisionRules
{
    internal const string SecretValueDetected = "secret_value_detected";
    internal const string InvalidUrl = "invalid_url";
    internal const string InvalidValue = "invalid_value";

    private const int MaxDetails = 50;

    private static readonly JsonSerializerOptions Plain = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The configuration, or the details that refuse it.</summary>
    /// <param name="request">A body the environment-revision schema accepted.</param>
    internal static (EnvironmentConfiguration? Configuration, IReadOnlyList<ErrorDetail> Details) Check(CreateEnvironmentRevisionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // PostgreSQL's jsonb and text cannot hold U+0000; such a string is no configuration value.
        if (request.BaseUrls.Any(pair => HasNul(pair.Key) || HasNul(pair.Value))
            || request.DependencyVersions.Any(pair => HasNul(pair.Key) || HasNul(pair.Value))
            || (request.SecretRefs ?? []).Any(HasNul)
            || HasNul(request.Name))
        {
            return (null, [new ErrorDetail(string.Empty, InvalidValue)]);
        }

        var details = new List<ErrorDetail>();

        var position = 0;
        foreach (var (key, url) in request.BaseUrls)
        {
            position++;
            if (UrlProblem(key, url) is { } reason)
            {
                Add(details, string.Create(CultureInfo.InvariantCulture, $"/base_urls/#{position}"), reason);
            }
        }

        position = 0;
        foreach (var (key, version) in request.DependencyVersions)
        {
            position++;
            if (SecretShape.ValueLooksSecret(key) || SecretShape.KeyLooksSecret(key) || SecretShape.ValueLooksSecret(version))
            {
                Add(details, string.Create(CultureInfo.InvariantCulture, $"/dependency_versions/#{position}"), SecretValueDetected);
            }
        }

        var refs = request.SecretRefs ?? [];
        for (var index = 0; index < refs.Count; index++)
        {
            var reference = refs[index];
            if (reference is null
                || !Reference().IsMatch(reference)
                || SecretShape.SecretNameLooksLikeValue(reference[7..])
                || SecretShape.ValueLooksSecret(reference[7..]))
            {
                Add(details, string.Create(CultureInfo.InvariantCulture, $"/secret_refs/{index}"), SecretValueDetected);
            }
        }

        if (details.Count > 0)
        {
            return (null, details);
        }

        var sortedRefs = refs.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var baseUrls = JsonSerializer.Serialize(request.BaseUrls, Plain);
        var versions = JsonSerializer.Serialize(request.DependencyVersions, Plain);

        string hash;
        try
        {
            hash = CanonicalHash.Compute(JsonSerializer.Serialize(
                new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["base_urls"] = request.BaseUrls,
                    ["dependency_versions"] = request.DependencyVersions,
                    ["secret_refs"] = sortedRefs,
                },
                Plain));
        }
        catch (Exception exception) when (exception is JsonCanonicalizationException or ArgumentException)
        {
            // A lone surrogate or another string the canonical form cannot carry: the value is not a configuration value.
            return (null, [new ErrorDetail(string.Empty, InvalidValue)]);
        }

        return (new EnvironmentConfiguration(baseUrls, versions, sortedRefs, hash), []);
    }

    /// <summary>Null for an absolute http(s) URL with no credential anywhere in it, else the reason it is refused.</summary>
    internal static string? UrlProblem(string key, string url)
    {
        if (SecretShape.KeyLooksSecret(key))
        {
            return SecretValueDetected;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) || string.IsNullOrEmpty(uri.Host))
        {
            // A credential-shaped string that is not even a URL is still reported as the secret it looks like.
            return SecretShape.ValueLooksSecret(url) ? SecretValueDetected : InvalidUrl;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo) || SecretShape.ValueLooksSecret(url))
        {
            return SecretValueDetected;
        }

        foreach (var segment in uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (SecretShape.ValueLooksSecret(Uri.UnescapeDataString(segment)))
            {
                return SecretValueDetected;
            }
        }

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var cut = pair.IndexOf('=', StringComparison.Ordinal);
            var name = Uri.UnescapeDataString(cut < 0 ? pair : pair[..cut]);
            var value = cut < 0 ? string.Empty : Uri.UnescapeDataString(pair[(cut + 1)..]);
            if ((value.Length > 0 && SecretShape.KeyLooksSecret(name)) || SecretShape.ValueLooksSecret(value) || SecretShape.ValueLooksSecret(name))
            {
                return SecretValueDetected;
            }
        }

        return uri.Fragment.Length > 1 && SecretShape.ValueLooksSecret(Uri.UnescapeDataString(uri.Fragment[1..])) ? SecretValueDetected : null;
    }

    private static bool HasNul(string? text) => text is not null && text.Contains('\0', StringComparison.Ordinal);

    private static void Add(List<ErrorDetail> details, string path, string reason)
    {
        if (details.Count < MaxDetails)
        {
            details.Add(new ErrorDetail(path, reason));
        }
    }

    [GeneratedRegex("^secret:[A-Za-z0-9_.-]{1,64}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex Reference();
}
