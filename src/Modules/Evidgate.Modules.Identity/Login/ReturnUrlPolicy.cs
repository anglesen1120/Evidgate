namespace Evidgate.Modules.Identity.Login;

/// <summary>
/// The post-login <c>returnUrl</c> allowlist (02.3.1 §5, §8): only an app-origin PATH survives, everything
/// else is replaced by the app root. A refused value is not an error: the browser still lands signed in,
/// just on <c>/</c>.
/// </summary>
/// <remarks>
/// <para>
/// A value is an app path when it starts with exactly one <c>/</c> — never <c>//</c> or <c>/\</c>, which a
/// browser resolves as an authority — carries no backslash (some clients normalise it to one), no control
/// character and no whitespace, and is at most <see cref="MaxLength"/> characters. Percent-decoding is
/// applied repeatedly (a bounded number of times, so an encoding chain cannot hide a value), and every
/// intermediate form must still be an app path: <c>/%252f%252fevil</c> decodes through <c>/%2f%2fevil</c>
/// to <c>//evil</c>, which is refused. The value returned is the one the client sent — it revalidates
/// identically and is what a same-origin browser resolves.
/// </para>
/// <para>
/// A scheme (<c>https://…</c>, <c>javascript:…</c>) never starts with <c>/</c>, so no separate scheme test
/// is needed; the single-slash rule is what keeps the redirect same-origin.
/// </para>
/// </remarks>
internal static class ReturnUrlPolicy
{
    /// <summary>The value every refusal falls back to: the app root.</summary>
    internal const string Root = "/";

    /// <summary>Longest accepted value, in characters.</summary>
    internal const int MaxLength = 512;

    /// <summary>Percent-decoding passes applied before the value is refused as an encoding chain.</summary>
    internal const int MaxDecodePasses = 4;

    /// <summary>
    /// Resolves <paramref name="candidate"/> to a redirect target: the candidate itself when it passes the
    /// allowlist in every decoding form, otherwise <see cref="Root"/>.
    /// </summary>
    /// <param name="candidate">The raw <c>returnUrl</c> query value, or null/empty.</param>
    public static string Resolve(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate) || candidate.Length > MaxLength)
        {
            return Root;
        }

        var current = candidate;

        for (var pass = 0; pass < MaxDecodePasses; pass++)
        {
            if (!IsAppPath(current))
            {
                return Root;
            }

            string decoded;
            try
            {
                decoded = Uri.UnescapeDataString(current);
            }
            catch (UriFormatException)
            {
                return Root;
            }

            if (string.Equals(decoded, current, StringComparison.Ordinal))
            {
                return candidate;
            }

            if (decoded.Length > MaxLength)
            {
                return Root;
            }

            current = decoded;
        }

        // Still changing after the bound: an encoding chain, refused rather than chased.
        return Root;
    }

    /// <summary>Whether <paramref name="value"/> is an app-origin path by the rules above (one decoding form).</summary>
    /// <param name="value">The value to classify.</param>
    public static bool IsAppPath(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxLength || value[0] != '/')
        {
            return false;
        }

        // `//host` and `/\host` are network-path references: a browser leaves the origin for them.
        if (value.Length > 1 && (value[1] == '/' || value[1] == '\\'))
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character == '\\' || char.IsControl(character) || char.IsWhiteSpace(character))
            {
                return false;
            }
        }

        return true;
    }
}
