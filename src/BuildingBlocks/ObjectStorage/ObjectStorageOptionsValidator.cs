using Microsoft.Extensions.Options;

namespace Evidgate.BuildingBlocks.ObjectStorage;

/// <summary>
/// Lazy validation of <see cref="ObjectStorageOptions"/> (06.1.2-T1): it runs when
/// <c>IObjectStorage</c> is first resolved, not at host start, so a host that never
/// touches object storage needs no configuration. The endpoint override is checked
/// to close SSRF-by-config: it must be an absolute http(s) URL with no userinfo,
/// query or fragment, so a crafted value cannot redirect the signed request at an
/// arbitrary host or smuggle credentials into the URL.
/// </summary>
/// <remarks>
/// Every failure names only the configuration key — built from
/// <c>nameof</c>, never spelled as a literal (10.1.1-T2) — and never the value
/// (00 §5.7 S3).
/// </remarks>
internal sealed class ObjectStorageOptionsValidator : IValidateOptions<ObjectStorageOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, ObjectStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        Require(failures, nameof(ObjectStorageOptions.Bucket), options.Bucket);
        Require(failures, nameof(ObjectStorageOptions.Region), options.Region);
        Require(failures, nameof(ObjectStorageOptions.AccessKey), options.AccessKey);
        Require(failures, nameof(ObjectStorageOptions.SecretKey), options.SecretKey);

        if (options.PresignExpirySeconds is < 1 or > 900)
        {
            failures.Add(
                $"EVIDGATE:Object:{nameof(ObjectStorageOptions.PresignExpirySeconds)} must be between 1 and 900");
        }

        if (!string.IsNullOrWhiteSpace(options.Endpoint) && !IsValidEndpoint(options.Endpoint))
        {
            failures.Add(
                $"EVIDGATE:Object:{nameof(ObjectStorageOptions.Endpoint)} must be an absolute http(s) URL without userinfo, query or fragment");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void Require(List<string> failures, string property, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            failures.Add($"EVIDGATE:Object:{property} is required");
        }
    }

    private static bool IsValidEndpoint(string endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Query)
        && string.IsNullOrEmpty(uri.Fragment);
}
