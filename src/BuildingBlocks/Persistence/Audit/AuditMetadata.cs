using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
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
/// <see cref="EnsureNoForbiddenKeys"/> walks a payload of any shape (dictionaries, sequences,
/// public properties and fields, <see cref="ValueTuple"/> items, key/value pairs,
/// <see cref="JsonElement"/>/<see cref="JsonNode"/>) and refuses a key on it before the entry is
/// queued, so no hash, advisory lock or insert ever sees that payload. A key that still holds a
/// non-ASCII letter or digit after <see cref="NormalizeKey"/>'s Unicode form is refused too, so a
/// homoglyph cannot pass for an ASCII name.
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
        // 02.3.1-T3: the own-profile write records which language the account chose (`vi` / `en`), never free text.
        "locale",
        // F55 (02.3.2-T2.1 AC6): the administrative session revoke records whose session it ended — the member's user id, never the session's credential.
        "target_user_id",
        // The append-only version chain records which version the audited save produced; a number, never content.
        "version_no",
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
        // 11.9.2-T2.2: the identifying field names the erasure path tombstones (email, display name, address,
        // phone, account name). Matching is whole-key, so `email_domain` and allowlisted `reason` survive.
        "display_name",
        "email_address",
        "ip_address",
        "full_name",
        "phone",
        "address",
        "username",
    };

    private static readonly HashSet<string> ForbiddenNormalized =
        new(ForbiddenKeys.Select(NormalizeKey), StringComparer.Ordinal);

    /// <summary>How deep <see cref="EnsureNoForbiddenKeys"/> walks before it refuses the payload.</summary>
    public const int MaxPayloadDepth = 16;

    /// <summary>
    /// The comparison form of a key: the key in Unicode compatibility form (KC), then its letters
    /// and digits only, upper-cased, so case, <c>-</c>, <c>_</c>, spaces, camelCase boundaries and a
    /// compatibility variant (<c>ｅｍａｉｌ</c>) do not hide a forbidden key.
    /// </summary>
    /// <param name="key">The key name.</param>
    /// <returns>The normalised form.</returns>
    public static string NormalizeKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        var normalized = key.Normalize(NormalizationForm.FormKC);
        var buffer = new char[normalized.Length];
        var length = 0;
        foreach (var character in normalized)
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
    /// top level, nested object, sequence element, public field, tuple item or JSON node. The
    /// message names the offending key, its normalised form and the nesting depth only: never an
    /// ancestor key and never a value, because either may be personal data. Called by
    /// <see cref="Validate"/> and again by the writer before its advisory lock (11.9.2-T2.2).
    /// </summary>
    /// <param name="payload">The payload, metadata dictionary or any object/array/JSON shape.</param>
    /// <param name="parameterName">
    /// The caller's own name for the payload, for the exception's parameter. Deliberately not a
    /// <see cref="CallerArgumentExpressionAttribute"/>: that would copy the caller's argument text — an inline
    /// dictionary literal included its values — into <see cref="ArgumentException.ParamName"/>.
    /// </param>
    public static void EnsureNoForbiddenKeys(object? payload, string? parameterName = null)
    {
        if (payload is null)
        {
            return;
        }

        new ForbiddenKeyWalker(parameterName ?? "payload").Walk(payload, 0);
    }

    /// <summary>Throws if <paramref name="metadata"/> breaks the allowlist; returns it otherwise.</summary>
    /// <param name="metadata">The candidate metadata.</param>
    public static IReadOnlyDictionary<string, string>? Validate(IReadOnlyDictionary<string, string>? metadata)
    {
        // The bounded checks need no traversal, so they run before the walk (a payload of 17 keys is
        // refused without visiting them). The personal-data deny list then runs before the allowlist
        // so a key like `Email` is reported as what it is, not as "not on the allowlist" (11.9.2-T2.2).
        if (metadata is null || metadata.Count == 0)
        {
            return null;
        }

        if (metadata.Count > MaxKeys)
        {
            throw new ArgumentException($"audit metadata carries more than {MaxKeys} keys", nameof(metadata));
        }

        EnsureNoForbiddenKeys(metadata, nameof(metadata));

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
    /// <see cref="ForbiddenNormalized"/>; the refusal reports the offending key, its normalised form
    /// and the depth, never an ancestor key or a value — either may be personal data
    /// (11.9.2-T2.2: never log or echo personal data). No path string is built: it would only be
    /// allocated to be withheld.
    /// <para>
    /// The walk descends into dictionaries, sequences, public properties, public fields (which is
    /// how a <see cref="ValueTuple"/> item is reached), <see cref="KeyValuePair{TKey,TValue}"/>
    /// pairs whose key is any string-convertible object, <see cref="JsonElement"/> and
    /// <see cref="JsonNode"/>. A reference-type container already open on the current path is a
    /// cycle and is refused instead of recursing forever; the same instance reached twice on
    /// different paths is not one.
    /// </para>
    /// </summary>
    private sealed class ForbiddenKeyWalker(string rootName)
    {
        /// <summary>Containers open on the current walk path; allocated only once a walk needs it.</summary>
        private HashSet<object>? _openPath;

        public void Walk(object? value, int depth)
        {
            if (depth > MaxPayloadDepth)
            {
                throw new ArgumentException(
                    $"audit payload nests deeper than {MaxPayloadDepth} levels (depth {depth})", rootName);
            }

            switch (value)
            {
                case null:
                case string:
                case byte[]:
                    return;
                case JsonElement element:
                    WalkElement(element, depth);
                    return;
                case JsonNode node:
                    Open(node, depth);
                    try
                    {
                        WalkNode(node, depth);
                    }
                    finally
                    {
                        Close(node);
                    }

                    return;
                case IDictionary dictionary:
                    // Dictionary<string, string> and friends: keys are names, values may nest.
                    Open(dictionary, depth);
                    try
                    {
                        foreach (DictionaryEntry entry in dictionary)
                        {
                            WalkEntry(entry.Key, entry.Value, depth);
                        }
                    }
                    finally
                    {
                        Close(dictionary);
                    }

                    return;
            }

            var type = value.GetType();
            if (type.IsPrimitive || type.IsEnum
                || value is decimal or Guid or DateTime or DateTimeOffset or TimeSpan or Uri)
            {
                return;
            }

            if (value is ITuple tuple)
            {
                // A ValueTuple's items are fields with positional names, not a key/value map: descend into
                // the items, so a dictionary nested in a tuple is still walked (a named element is not a key).
                for (var index = 0; index < tuple.Length; index++)
                {
                    Walk(tuple[index], depth + 1);
                }

                return;
            }

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
            {
                // One pair, not a sequence of them (a value held in a dictionary): its Key is the name,
                // whatever the key type, and its Value may nest. The property walk alone would read the
                // harmless name `Key` and never compare the key itself.
                WalkEntry(
                    type.GetProperty("Key")?.GetValue(value),
                    type.GetProperty("Value")?.GetValue(value),
                    depth);

                return;
            }

            if (value is IEnumerable sequence)
            {
                Open(value, depth);
                try
                {
                    if (IsPairSequence(type))
                    {
                        // KeyValuePair<TKey, TValue> with any key type: the key name is whatever the key
                        // renders as, so an enum or an int key is read as a name too, never skipped.
                        WalkPairs(sequence, depth);

                        return;
                    }

                    foreach (var item in sequence)
                    {
                        Walk(item, depth + 1);
                    }
                }
                finally
                {
                    Close(value);
                }

                return;
            }

            Open(value, depth);
            try
            {
                foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!property.CanRead || property.GetIndexParameters().Length != 0)
                    {
                        continue;
                    }

                    ThrowIfForbidden(property.Name, depth + 1);
                    Walk(property.GetValue(value), depth + 1);
                }

                foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    ThrowIfForbidden(field.Name, depth + 1);
                    Walk(field.GetValue(value), depth + 1);
                }
            }
            finally
            {
                Close(value);
            }
        }

        /// <summary>Marks a reference-type container open, and refuses a container already on the path.</summary>
        private void Open(object reference, int depth)
        {
            if (reference.GetType().IsValueType)
            {
                return;
            }

            _openPath ??= new HashSet<object>(ReferenceEqualityComparer.Instance);
            if (!_openPath.Add(reference))
            {
                throw new ArgumentException(
                    $"audit payload contains a self-referencing structure at depth {depth}", rootName);
            }
        }

        private void Close(object reference)
        {
            if (!reference.GetType().IsValueType)
            {
                _openPath?.Remove(reference);
            }
        }

        private static bool IsPairSequence(Type type) =>
            type.GetInterfaces().Any(@interface =>
                @interface.IsGenericType
                && @interface.GetGenericTypeDefinition() == typeof(IEnumerable<>)
                && @interface.GetGenericArguments()[0] is { IsGenericType: true } pair
                && pair.GetGenericTypeDefinition() == typeof(KeyValuePair<,>));

        private void WalkPairs(IEnumerable sequence, int depth)
        {
            foreach (var item in sequence)
            {
                if (item is null)
                {
                    continue;
                }

                var pair = item.GetType();
                var key = pair.GetProperty("Key")?.GetValue(item);
                var value = pair.GetProperty("Value")?.GetValue(item);
                WalkEntry(key, value, depth);
            }
        }

        private void WalkEntry(object? key, object? value, int depth)
        {
            // Any string-convertible key is a key name: a string as it is, an enum or an int by its
            // rendering, so `KeyValuePair<SomeEnum, string>` is read as a name instead of a value.
            var name = key as string ?? key?.ToString();
            if (name is null)
            {
                return;
            }

            ThrowIfForbidden(name, depth + 1);
            Walk(value, depth + 1);
        }

        private void WalkElement(JsonElement element, int depth)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        WalkEntry(property.Name, property.Value, depth);
                    }

                    return;
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                    {
                        Walk(item, depth + 1);
                    }

                    return;
                default:
                    return;
            }
        }

        private void WalkNode(JsonNode node, int depth)
        {
            switch (node)
            {
                case JsonObject jsonObject:
                    foreach (var (name, child) in jsonObject)
                    {
                        WalkEntry(name, child, depth);
                    }

                    return;
                case JsonArray jsonArray:
                    foreach (var item in jsonArray)
                    {
                        Walk(item, depth + 1);
                    }

                    return;
                default:
                    return;
            }
        }

        private void ThrowIfForbidden(string key, int depth)
        {
            var normalized = NormalizeKey(key);

            // Fail closed on a key that still holds a non-ASCII letter or digit after Unicode form KC:
            // `еmail` with a Cyrillic `е` is not `EMAIL`, so it must be refused rather than waved through.
            if (ForbiddenNormalized.Contains(normalized) || ContainsNonAsciiLetterOrDigit(key))
            {
                throw new ArgumentException(
                    $"audit payload key '{key}' (normalised '{normalized}') at depth {depth} is forbidden: an audit row never carries personal data or free text",
                    rootName);
            }
        }

        private static bool ContainsNonAsciiLetterOrDigit(string key)
        {
            foreach (var character in key.Normalize(NormalizationForm.FormKC))
            {
                if (char.IsLetterOrDigit(character) && !char.IsAscii(character))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
