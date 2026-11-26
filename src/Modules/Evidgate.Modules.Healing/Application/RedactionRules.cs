using System.Text.RegularExpressions;
using Evidgate.BuildingBlocks.Observability.Redaction;

namespace Evidgate.Modules.Healing.Application;

/// <summary>
/// The versioned rule catalogue of the egress redaction pipeline. Credentials, cookies, bearer headers and token shapes
/// come from <see cref="SecretRedactor"/> (the one list of secret-shaped text the log path already uses); the classes a
/// log never needed — personal data and typed input — are added here. Masking is a fixed marker per rule, so the same
/// text always masks to the same bytes.
/// </summary>
internal static class RedactionRules
{
    /// <summary>
    /// Changes whenever a rule is added, removed or changed, so a stored report says which catalogue produced it.
    /// </summary>
    internal const string Version = "evg-redact-v1";

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    private static readonly SecretRedactor Secrets = new();

    // Rules without look-around use the linear-time engine, so a long run of rule-shaped characters cannot cost
    // more than its length; the rest carry a match timeout, and a timeout masks the whole window.
    private static readonly Rule[] Catalogue =
    [
        new("private_key", @"-----BEGIN [A-Z ]*PRIVATE KEY-----[A-Za-z0-9+/=\s]*", Linear: true, Prefilter: "PRIVATE KEY"),
        new("api_key_shape", @"\b(?:(?:AKIA|ASIA)[0-9A-Z]{16}|sk-[A-Za-z0-9_\-]{20,}|xox[baprs]-[A-Za-z0-9\-]{10,}|AIza[0-9A-Za-z_\-]{35})", Linear: true),
        new("bearer_token", @"\bbearer\s+[A-Za-z0-9._~+/=\-]{8,}", Linear: true, Replacement: "Bearer [redacted:bearer_token]", Prefilter: "earer"),
        new("email", @"[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)+", Linear: true, Prefilter: "@"),
        new("phone", @"(?<!\d)(?:\+84|0)[\s.\-]?[2-9](?:[\s.\-]?\d){8,9}(?!\d)"),
        new("phone", @"(?<![\w+])\+\d[\d\s.\-()]{8,16}\d"),
        new("input_value", @"(\bvalue\s*=\s*)""(?!\[redacted:)[^""]*""", Replacement: "$1\"[redacted:input_value]\"", Prefilter: "value"),
        new("input_value", @"(\bvalue\s*=\s*)'(?!\[redacted:)[^']*'", Replacement: "$1'[redacted:input_value]'", Prefilter: "value"),
        new("input_value", @"(""value""\s*:\s*)""(?!\[redacted:)(?:\\.|[^""\\])*""", Replacement: "$1\"[redacted:input_value]\"", Prefilter: "\"value\""),
        new(
            "input_value",
            @"(\.(?:fill|type|pressSequentially)\(\s*(?:'[^']*'|""[^""]*""|`[^`]*`)\s*,\s*)(?:'(?!\[redacted:)[^']*'|""(?!\[redacted:)[^""]*""|`(?!\[redacted:)[^`]*`)",
            Replacement: "$1\"[redacted:input_value]\"",
            Prefilter: "("),
    ];

    /// <summary>Masks <paramref name="text"/> with every rule and counts the replacements per rule id.</summary>
    /// <param name="text">One window of the payload.</param>
    /// <param name="counts">Receives the replacements per rule id.</param>
    internal static string Apply(string text, Dictionary<string, int> counts)
    {
        var masked = Secrets.RedactTextCounted(text, counts, int.MaxValue);
        foreach (var rule in Catalogue)
        {
            masked = rule.Apply(masked, counts);
        }

        return masked;
    }

    private sealed record Rule(string Id, string Pattern, bool Linear = false, string? Replacement = null, string? Prefilter = null)
    {
        private readonly Regex _regex = new(
            Pattern,
            Linear
                ? RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking
                : RegexOptions.Compiled | RegexOptions.CultureInvariant,
            Linear ? Regex.InfiniteMatchTimeout : MatchTimeout);

        private readonly string _marker = Replacement ?? $"[redacted:{Id}]";

        internal string Apply(string text, Dictionary<string, int> counts)
        {
            if (Prefilter is not null && !text.Contains(Prefilter, StringComparison.OrdinalIgnoreCase))
            {
                return text;
            }

            try
            {
                var matches = 0;
                var result = _regex.Replace(text, match =>
                {
                    matches++;
                    return match.Result(_marker);
                });
                if (matches > 0)
                {
                    counts[Id] = counts.GetValueOrDefault(Id) + matches;
                }

                return result;
            }
            catch (RegexMatchTimeoutException)
            {
                counts["regex_timeout"] = counts.GetValueOrDefault("regex_timeout") + 1;
                return "[redacted:regex_timeout]";
            }
        }
    }
}
