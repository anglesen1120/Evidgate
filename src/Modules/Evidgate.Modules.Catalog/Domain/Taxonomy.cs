using System.Text.Json;

namespace Evidgate.Modules.Catalog.Domain;

/// <summary>
/// The taxonomy a suite is authored under (03.1.4, D-A7 §8.1): what runs it, how wide it is and why it exists. The
/// three values are closed lists, so a fourth platform or a renamed purpose is a contract change and a migration,
/// never a new string (D4). The same lists live in <c>contracts/schemas/taxonomy.v1.json</c>; the integration suite
/// compares them, so the two cannot drift apart silently.
/// </summary>
/// <param name="Platform"><c>web</c>, <c>api</c>, <c>unit</c> or <c>mobile</c>.</param>
/// <param name="Level"><c>unit</c>, <c>integration</c>, <c>e2e</c> or <c>smoke</c>.</param>
/// <param name="Purpose"><c>regression</c>, <c>smoke</c>, <c>release_gate</c> or <c>exploratory</c>.</param>
internal sealed record SuiteTaxonomy(string Platform, string Level, string Purpose);

/// <summary>What the platform rule decided for one requested platform.</summary>
/// <param name="Stored">The platform a revision stores, or null when the request cannot be accepted.</param>
/// <param name="Emulated">True when a <c>mobile</c> request was stored as <c>web</c> because every variant runs a browser.</param>
/// <param name="NeedsCapability">True when <c>mobile</c> was requested but no member declares a native mobile platform.</param>
internal sealed record PlatformDecision(string? Stored, bool Emulated, bool NeedsCapability);

/// <summary>
/// The closed catalogue, the defaults and the two text rules of the catalog taxonomy. Kept in one place so the API
/// validator, the revision planner and the sidecar reader cannot disagree about what a legal value is.
/// </summary>
internal static class Taxonomy
{
    /// <summary>The longest requirement pointer a case may carry (D-A6 §8.1).</summary>
    internal const int MaxRequirementRefLength = 200;

    /// <summary>The suite behaviour the product has today: a browser suite, run end to end, for regressions.</summary>
    internal static SuiteTaxonomy Default { get; } = new("web", "e2e", "regression");

    /// <summary>The platforms a suite may declare.</summary>
    internal static IReadOnlyList<string> Platforms { get; } = ["web", "api", "unit", "mobile"];

    /// <summary>The levels a suite may declare.</summary>
    internal static IReadOnlyList<string> Levels { get; } = ["unit", "integration", "e2e", "smoke"];

    /// <summary>The purposes a suite may declare.</summary>
    internal static IReadOnlyList<string> Purposes { get; } = ["regression", "smoke", "release_gate", "exploratory"];

    /// <summary>Platform tokens that name a device rather than a browser engine; a variant carrying one runs natively.</summary>
    private static readonly HashSet<string> NativeMobilePlatforms = new(StringComparer.OrdinalIgnoreCase)
    {
        "android", "ios", "ipados", "mobile",
    };

    /// <summary>Variant parameter keys that name a browser; a variant carrying one is a run inside a browser engine.</summary>
    private static readonly string[] BrowserKeys = ["browser", "engine"];

    /// <summary>Variant parameter keys that can name the platform a case runs on.</summary>
    private static readonly string[] PlatformKeys = ["platform", "os", "device_platform"];

    /// <summary>
    /// Canonicalises one taxonomy value: trimmed and lower-cased (the contract's values are lower_snake, 00 §7), and
    /// null when the result is not in <paramref name="catalogue"/>.
    /// </summary>
    /// <param name="value">The value as sent.</param>
    /// <param name="catalogue">The closed list it must belong to.</param>
    public static string? Canonical(string? value, IReadOnlyList<string> catalogue)
    {
        if (value is null)
        {
            return null;
        }

        var trimmed = value.Trim();
        foreach (var candidate in catalogue)
        {
            if (string.Equals(candidate, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>The level a platform gets when the request names the platform but no level: an API suite is an integration suite, a unit suite runs units.</summary>
    /// <param name="platform">A canonical platform.</param>
    public static string DefaultLevelFor(string platform) => platform switch
    {
        "api" => "integration",
        "unit" => "unit",
        _ => "e2e",
    };

    /// <summary>
    /// Accepts a requirement pointer: trimmed, 1..200 characters, clean text (no control or format characters). The
    /// empty string is not a pointer — a caller that wants the value cleared sends <c>null</c>.
    /// </summary>
    /// <param name="raw">The value as sent.</param>
    /// <param name="value">The trimmed value, or null when it was not accepted.</param>
    public static bool TryRequirementRef(string? raw, out string? value)
    {
        value = null;
        if (raw is null)
        {
            return false;
        }

        var trimmed = raw.Trim();
        if (trimmed.Length is 0 or > MaxRequirementRefLength || !TextRules.IsClean(trimmed))
        {
            return false;
        }

        value = trimmed;

        return true;
    }

    /// <summary>
    /// The platform rule of AC4: <c>web</c>, <c>api</c> and <c>unit</c> are stored as asked, and <c>mobile</c> is
    /// stored only when a variant declares a native mobile platform. A <c>mobile</c> suite whose variants all run a
    /// browser engine is device emulation — the same case in a mobile viewport — so it is stored as <c>web</c>;
    /// without either evidence there is no runner that could execute it and the request is refused.
    /// </summary>
    /// <param name="requested">A canonical requested platform.</param>
    /// <param name="browserDriven">True when at least one member's variant names a browser engine.</param>
    /// <param name="nativeMobile">True when at least one member's variant names a mobile platform.</param>
    public static PlatformDecision ResolvePlatform(string requested, bool browserDriven, bool nativeMobile)
    {
        if (requested != "mobile")
        {
            return new PlatformDecision(requested, false, false);
        }

        if (browserDriven)
        {
            return new PlatformDecision("web", true, false);
        }

        return nativeMobile
            ? new PlatformDecision("mobile", false, false)
            : new PlatformDecision(null, false, true);
    }

    /// <summary>True when the variant parameters name a browser engine (a run inside a browser, emulated or not).</summary>
    /// <param name="parameters">The member's authored parameters; null when it has none.</param>
    public static bool IsBrowserDriven(JsonElement? parameters)
    {
        foreach (var key in BrowserKeys)
        {
            if (HasText(parameters, key))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when the variant parameters name a mobile platform a runner would have to be able to execute natively.</summary>
    /// <param name="parameters">The member's authored parameters; null when it has none.</param>
    public static bool DeclaresNativeMobile(JsonElement? parameters)
    {
        foreach (var key in PlatformKeys)
        {
            if (Text(parameters, key) is { } value && NativeMobilePlatforms.Contains(value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasText(JsonElement? parameters, string key) => Text(parameters, key) is not null;

    private static string? Text(JsonElement? parameters, string key)
    {
        if (parameters is not { ValueKind: JsonValueKind.Object } element)
        {
            return null;
        }

        foreach (var member in element.EnumerateObject())
        {
            if (string.Equals(member.Name, key, StringComparison.OrdinalIgnoreCase) && member.Value.ValueKind == JsonValueKind.String)
            {
                var value = member.Value.GetString()?.Trim();

                return string.IsNullOrEmpty(value) ? null : value;
            }
        }

        return null;
    }
}
