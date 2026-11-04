namespace Evidgate.BuildingBlocks.SharedKernel.Security;

/// <summary>
/// The one URL rule a stored, caller-supplied address must pass: an absolute <c>http(s)</c> URL whose
/// host, path segments, query names and values and fragment carry nothing that looks like a credential.
/// It was the environment-revision rule; the project AI settings reuse it unchanged so the two features
/// cannot drift on what a legitimate URL is.
/// </summary>
public static class UrlSafety
{
    /// <summary>The reason a credential-shaped value is refused.</summary>
    public const string SecretValueDetected = "secret_value_detected";

    /// <summary>The reason a value that is not an absolute credential-free <c>http(s)</c> URL is refused.</summary>
    public const string InvalidUrl = "invalid_url";

    /// <summary>
    /// Null for an absolute <c>http(s)</c> URL with no credential anywhere in it, else the reason it is
    /// refused. <paramref name="key"/> is the name the URL was stored under; a name that itself looks like
    /// a credential is refused before the address is read.
    /// </summary>
    /// <param name="key">The name the address is stored under.</param>
    /// <param name="url">The caller-supplied address.</param>
    /// <returns>One of <see cref="SecretValueDetected"/>, <see cref="InvalidUrl"/>, or null when accepted.</returns>
    public static string? Problem(string key, string url)
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

        foreach (var label in uri.Host.Split('.'))
        {
            if (SecretShape.ValueLooksSecret(label))
            {
                return SecretValueDetected;
            }
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
}
