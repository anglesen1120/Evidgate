using System.Text.RegularExpressions;

namespace Evidgate.Modules.Ingestion.Domain;

/// <summary>
/// The adapter-name grammar of 04.7.1-T2.2/T2.3: the shape the <c>adapter_capability_name_check</c> column
/// CHECK enforces (migration 0026) and the <c>ext.schema</c> prefix carries on the first-send path. One
/// definition, so the direct registration command and the first-send extractor cannot drift.
/// </summary>
internal static partial class AdapterName
{
    /// <summary>The name body: a lowercase letter or digit, then up to 63 of <c>[a-z0-9._-]</c>.</summary>
    internal const string Pattern = "[a-z0-9][a-z0-9._-]{0,63}";

    /// <summary>True when <paramref name="name"/> is a storable adapter name.</summary>
    /// <param name="name">The candidate name.</param>
    internal static bool IsValid(string? name) => name is not null && Shape().IsMatch(name);

    [GeneratedRegex("^" + Pattern + "$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex Shape();
}
