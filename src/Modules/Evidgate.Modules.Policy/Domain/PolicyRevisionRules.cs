using System.Text.Json;
using System.Text.RegularExpressions;

namespace Evidgate.Modules.Policy.Domain;

/// <summary>
/// Shape rules of a policy revision (07.1.1-T1): the content address is <c>sha256:&lt;64 hex&gt;</c> (00 §7) and the
/// body is a JSON object (00 §8.6). Both are checked before the store touches the database, so a malformed append
/// never reaches a statement.
/// </summary>
internal static partial class PolicyRevisionRules
{
    /// <summary>True when <paramref name="hash"/> is a <c>sha256:&lt;64 lower-case hex&gt;</c> content address.</summary>
    public static bool IsValidHash(string? hash) => hash is not null && HashShape().IsMatch(hash);

    /// <summary>True when <paramref name="body"/> parses as a JSON object.</summary>
    public static bool IsJsonObject(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    [GeneratedRegex("^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex HashShape();
}
