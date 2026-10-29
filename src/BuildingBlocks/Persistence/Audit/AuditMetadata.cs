using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Evidgate.BuildingBlocks.Persistence.Audit;

/// <summary>
/// The metadata allowlist (02.7.1-T2.2, 00 §5.7 S3): an audit row never carries a secret.
/// Keys must be on the allowlist, values are short plain strings, and anything shaped like a
/// credential is refused when the entry is built — a programming error, not user input.
/// <para>
/// It also holds the personal-data deny list of 11.9.2-T2.2 (AC9/AC10): <see cref="ForbiddenKeys"/>
/// is the one list, mirrored in <c>docs/security/audit-and-deletion-rule.md</c>, and
/// <see cref="EnsureNoForbiddenKeys"/> walks a payload of any shape (dictionaries, objects,
/// arrays, <see cref="JsonElement"/>/<see cref="JsonNode"/>) and refuses a key on it before the
/// entry is queued, so no hash, advisory lock or insert ever sees that payload.
/// </para>
/// </summary>
public static partial class AuditMetadata
{
    /// <summary>Most keys an entry may carry.</summary>
    public const int MaxKeys = 16;

    /// <summary>Longest value, in characters.</summary>
    public const int MaxValueLength = 256;

    /// <summary>The keys handlers may record. Adding one is a review decision.</summary>
    public static readonly IReadOnlySet<string> AllowedKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "email_domain",
        "role_code",
        "role_id",
        "membership_id",
        "slug",
        "status",
        "fields",
        "count",
        "format",
        "range_days",
        "reason",
        "outcome",
        // 07.1.1-T4.1: the activation records which revision it moved the pointer from and the mode it stored.
        "previous_id",
        "mode",
        // 02.3.1-T2: login/link/bind rows record which provider authenticated the identity and how an invite bound (names, never an address or subject).
        "provider",
        "via",
        // F55 (02.3.2-T2.1 AC6): the administrative session revoke records whose session it ended — the member's user id, never the session's credential.
        "target_user_id",
    };

    /// <summary>
    /// The payload keys an audit row may never carry (11.9.2-T2.2, AC10): a person's identifying
    /// fields and free text. One list only — the rule doc mirrors it. A key matches when its
    /// normalised form (see <see cref="NormalizeKey"/>) equals a normalised entry, so
    /// <c>user_agent</c>, <c>User-Agent</c>, <c>userAgent</c> and <c>USER AGENT</c> are the same
    /// key; a key that merely contains a term (<c>email_domain</c>) is not one of them.
    /// </summary>
    public static readonly IReadOnlySet<string> ForbiddenKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "email",
        "name",
        "ip",
        "user_agent",
        "message",
        "reason_text",
        "comment",
        "note",
        "description",
        "free_text",
    };

    private static readonly HashSet<string> ForbiddenNormalized =
        new(ForbiddenKeys.Select(NormalizeKey), StringComparer.Ordinal);

    /// <summary>How deep <see cref="EnsureNoForbiddenKeys"/> walks before it refuses the payload.</summary>
    public const int MaxPayloadDepth = 16;

    /// <summary>
    /// The comparison form of a key: letters and digits only, upper-cased, so case, <c>-</c>,
    /// <c>_</c>, spaces and camelCase boundaries do not hide a forbidden key.
    /// </summary>
    /// <param name="key">The key name.</param>
    /// <returns>The normalised form.</returns>
    public static string NormalizeKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        var buffer = new char[key.Length];
        var length = 0;
        foreach (var character in key)
        {
            if (char.IsLetterOrDigit(character))
            {
                buffer[length++] = char.ToUpperInvariant(character);
            }
        }

        return new string(buffer, 0, length);
    }

    /// <summary>
    /// Throws if <paramref name="payload"/> carries a <see cref="ForbiddenKeys"/> key anywhere —
    /// top level, nested object, array element or JSON node. The message names the key path only,
    /// never a value. Called by <see cref="Validate"/> and again by the writer before its
    /// advisory lock (11.9.2-T2.2).
    /// </summary>
    /// <param name="payload">The payload, metadata dictionary or any object/array/JSON shape.</param>
    /// <param name="parameterName">The caller's expression, for the exception's parameter.</param>
    public static void EnsureNoForbiddenKeys(
        object? payload,
        [CallerArgumentExpression(nameof(payload))] string? parameterName = null)
    {
        if (payload is null)
        {
            return;
        }

        var root = parameterName ?? "payload";
        new ForbiddenKeyWalker(root).Walk(payload, root, 0);
    }

    /// <summary>Throws if <paramref name="metadata"/> breaks the allowlist; returns it otherwise.</summary>
    /// <param name="metadata">The candidate metadata.</param>
    public static IReadOnlyDictionary<string, string>? Validate(IReadOnlyDictionary<string, string>? metadata)
    {
        // The personal-data deny list is checked first so a key like `Email` is reported as what it
        // is, not as "not on the allowlist" (11.9.2-T2.2).
        EnsureNoForbiddenKeys(metadata, nameof(metadata));

        if (metadata is null || metadata.Count == 0)
        {
            return null;
        }

        if (metadata.Count > MaxKeys)
        {
            throw new ArgumentException($"audit metadata carries more than {MaxKeys} keys", nameof(metadata));
        }

        foreach (var (key, value) in metadata)
        {
            if (!AllowedKeys.Contains(key))
            {
                throw new ArgumentException($"audit metadata key '{key}' is not on the allowlist", nameof(metadata));
            }

            if (value is null || value.Length > MaxValueLength)
            {
                throw new ArgumentException($"audit metadata value of '{key}' is missing or too long", nameof(metadata));
            }

            if (SecretShaped().IsMatch(value))
            {
                throw new ArgumentException($"audit metadata value of '{key}' looks like a credential", nameof(metadata));
            }
        }

        return metadata;
    }

    // A long unbroken run of token characters, a JWT, or an authorization scheme prefix.
    [GeneratedRegex(@"(?i)(bearer\s+\S+|eyJ[\w-]{10,}\.|[A-Za-z0-9+/_\-]{40,}={0,2}|(secret|password|token)\s*[=:])", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex SecretShaped();

    /// <summary>
    /// One walk over a payload of unknown shape. Every key it meets is compared with
    /// <see cref="ForbiddenNormalized"/>; the path it reports is built from keys and indexes only,
    /// so a value never appears in the exception (11.9.2-T2.2: never log or echo personal data).
    /// </summary>
    private sealed class ForbiddenKeyWalker(string rootName)
    {
        private readonly HashSet<object> _seen = new(ReferenceEqualityComparer.Instance);

        public void Walk(object? value, string path, int depth)
        {
            if (depth > MaxPayloadDepth)
            {
                throw new ArgumentException(
                    $"audit payload nests deeper than {MaxPayloadDepth} levels at '{path}'", rootName);
            }

            switch (value)
            {
                case null:
                case string:
                case byte[]:
                    return;
                case JsonElement element:
                    WalkElement(element, path, depth);
                    return;
                case JsonNode node:
                    WalkNode(node, path, depth);
                    return;
                case IDictionary dictionary:
                    // Dictionary<string, string> and friends: keys are names, values may nest.
                    foreach (DictionaryEntry entry in dictionary)
                    {
                        WalkEntry(entry.Key, entry.Value, path, depth);
                    }

                    return;
            }

            var type = value.GetType();
            if (type.IsPrimitive || type.IsEnum
                || value is decimal or Guid or DateTime or DateTimeOffset or TimeSpan or Uri)
            {
                return;
            }

            if (AsSequence(value) is { } sequence)
            {
                if (IsStringKeyedPairs(type))
                {
                    // Dictionary<string, T> and IReadOnlyDictionary<string, T> (any value type).
                    WalkPairs(sequence, path, depth);

                    return;
                }

                var index = 0;
                foreach (var item in sequence)
                {
                    Walk(item, $"{path}[{index++}]", depth + 1);
                }

                return;
            }

            if (type.IsClass && !_seen.Add(value))
            {
                return;
            }

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!property.CanRead || property.GetIndexParameters().Length != 0)
                {
                    continue;
                }

                var propertyPath = $"{path}.{property.Name}";
                ThrowIfForbidden(property.Name, propertyPath);
                Walk(property.GetValue(value), propertyPath, depth + 1);
            }
        }

        private static IEnumerable? AsSequence(object value) => value as IEnumerable;

        private static bool IsStringKeyedPairs(Type type) =>
            type.GetInterfaces().Any(@interface =>
                @interface.IsGenericType
                && @interface.GetGenericTypeDefinition() == typeof(IEnumerable<>)
                && @interface.GetGenericArguments()[0] is { IsGenericType: true } pair
                && pair.GetGenericTypeDefinition() == typeof(KeyValuePair<,>)
                && pair.GetGenericArguments()[0] == typeof(string));

        private void WalkPairs(IEnumerable sequence, string path, int depth)
        {
            foreach (var item in sequence)
            {
                if (item is null)
                {
                    continue;
                }

                var pair = item.GetType();
                var key = pair.GetProperty("Key")?.GetValue(item) as string;
                var value = pair.GetProperty("Value")?.GetValue(item);
                WalkEntry(key, value, path, depth);
            }
        }

        private void WalkEntry(object? key, object? value, string path, int depth)
        {
            var name = key as string ?? key?.ToString();
            if (name is null)
            {
                return;
            }

            var entryPath = $"{path}.{name}";
            ThrowIfForbidden(name, entryPath);
            Walk(value, entryPath, depth + 1);
        }

        private void WalkElement(JsonElement element, string path, int depth)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        WalkEntry(property.Name, property.Value, path, depth);
                    }

                    return;
                case JsonValueKind.Array:
                    var index = 0;
                    foreach (var item in element.EnumerateArray())
                    {
                        Walk(item, $"{path}[{index++}]", depth + 1);
                    }

                    return;
                default:
                    return;
            }
        }

        private void WalkNode(JsonNode node, string path, int depth)
        {
            switch (node)
            {
                case JsonObject jsonObject:
                    foreach (var (name, child) in jsonObject)
                    {
                        WalkEntry(name, child, path, depth);
                    }

                    return;
                case JsonArray jsonArray:
                    var index = 0;
                    foreach (var item in jsonArray)
                    {
                        Walk(item, $"{path}[{index++}]", depth + 1);
                    }

                    return;
                default:
                    return;
            }
        }

        private void ThrowIfForbidden(string key, string path)
        {
            if (ForbiddenNormalized.Contains(NormalizeKey(key)))
            {
                throw new ArgumentException(
                    $"audit payload key '{path}' is forbidden: an audit row never carries personal data or free text",
                    rootName);
            }
        }
    }
}
