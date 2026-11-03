using System.Buffers;
using Evidgate.BuildingBlocks.SharedKernel.Security;
using System.Text.Json;
using System.Text.Json.Nodes;
using Evidgate.BuildingBlocks.Canonicalization;

namespace Evidgate.Modules.Catalog.Domain;

/// <summary>A variant's parameters after normalisation.</summary>
/// <param name="Hash"><c>sha256:</c> over the canonical parameter JSON; the variant's identity inside a project.</param>
/// <param name="ParamsJson">The normalised parameters as JSON (sorted keys), the text stored in <c>variant.params</c>.</param>
internal sealed record NormalizedVariant(string Hash, string ParamsJson);

/// <summary>One refusal of a parameter set: where and why.</summary>
/// <param name="Path">JSON-pointer-like path inside the parameters (<c>/params/api_token</c>).</param>
/// <param name="Reason">A short token (<c>secret_key</c>, <c>secret_value</c>, <c>not_scalar</c>, …).</param>
internal sealed record VariantProblem(string Path, string Reason);

/// <summary>
/// Normalises variant parameters into a content-addressed identity (03.2.1-T2.1, D5): identical parameters after
/// normalisation — key order, null defaults, the case of enum-like values, non-identifying keys — map to the same
/// variant, and a parameter set that carries a secret-shaped key or value never becomes one. Parameters are flat:
/// scalars and short arrays of scalars (nested objects are refused so a secret cannot hide one level down).
/// </summary>
internal static class VariantNormalizer
{
    /// <summary>Keys allowed per variant.</summary>
    internal const int MaxKeys = 32;

    /// <summary>The largest integer a double (and so RFC 8785) represents exactly: 2^53 − 1.</summary>
    private const long MaxExactInteger = 9_007_199_254_740_991;

    /// <summary>The database refuses larger parameter sets (<c>variant_params_check</c>); checked here so a row never reaches an error message.</summary>
    private const int MaxParamsBytes = 8000;

    private const int MaxStringChars = 256;
    private const int MaxArrayItems = 32;

    /// <summary>A variant key's first character: an ASCII letter or <c>_</c>.</summary>
    private static readonly SearchValues<char> KeyFirst = SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz_");

    /// <summary>Keys that describe a variant for humans and say nothing about what it runs: dropped before hashing.</summary>
    private static readonly HashSet<string> NonIdentifying = new(StringComparer.OrdinalIgnoreCase)
    {
        "description", "label", "labels", "note", "notes", "comment", "comments", "title", "tags", "display_name", "displayname",
    };

    /// <summary>Keys whose string values are case-insensitive enum-like names: lower-cased and trimmed before hashing.</summary>
    private static readonly HashSet<string> EnumLike = new(StringComparer.OrdinalIgnoreCase)
    {
        "browser", "engine", "platform", "os", "mode", "env", "environment", "channel", "device", "locale", "viewport",
    };

    /// <summary>Normalises <paramref name="parameters"/> (null means no parameters).</summary>
    /// <param name="parameters">The parameter object as sent.</param>
    /// <param name="basePath">The path prefix used in problems, e.g. <c>/members/m0/params</c>.</param>
    /// <param name="problems">Receives every refusal; the result is null when any was added.</param>
    public static NormalizedVariant? Normalize(JsonElement? parameters, string basePath, List<VariantProblem> problems)
    {
        ArgumentNullException.ThrowIfNull(problems);

        var before = problems.Count;
        var result = new SortedDictionary<string, JsonNode?>(StringComparer.Ordinal);

        if (parameters is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } element)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                problems.Add(new VariantProblem(basePath, "not_object"));

                return null;
            }

            var count = 0;
            foreach (var member in element.EnumerateObject())
            {
                if (++count > MaxKeys)
                {
                    problems.Add(new VariantProblem(basePath, "too_many_keys"));

                    break;
                }

                // A rejected key is named by its position, never echoed: it may itself be the secret.
                var path = basePath + "/#" + count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (!IsValidKey(member.Name) || !TextRules.IsClean(member.Name))
                {
                    problems.Add(new VariantProblem(path, "invalid_key"));
                    continue;
                }

                if (SecretShape.KeyLooksSecret(member.Name) || SecretShape.ValueLooksSecret(member.Name))
                {
                    problems.Add(new VariantProblem(path, "secret_key"));
                    continue;
                }

                path = basePath + "/" + member.Name;

                if (member.Name.StartsWith('_') || NonIdentifying.Contains(member.Name))
                {
                    continue;
                }

                var value = Scalar(member.Value, member.Name, path, problems);
                if (value is not null)
                {
                    result[member.Name] = value;
                }
            }
        }

        if (problems.Count > before)
        {
            return null;
        }

        var json = JsonSerializer.Serialize(result);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxParamsBytes)
        {
            problems.Add(new VariantProblem(basePath, "too_large"));

            return null;
        }

        try
        {
            return new NormalizedVariant(CanonicalHash.Compute(json), json);
        }
        catch (JsonCanonicalizationException)
        {
            problems.Add(new VariantProblem(basePath, "invalid_value"));

            return null;
        }
    }

    private static JsonNode? Scalar(JsonElement value, string key, string path, List<VariantProblem> problems)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Null:
                return null; // null is the default: dropped.
            case JsonValueKind.True:
                return JsonValue.Create(true);
            case JsonValueKind.False:
                return JsonValue.Create(false);
            case JsonValueKind.Number:
                {
                    // Canonical JSON hashes numbers as IEEE doubles: refuse what a double cannot hold exactly, so two
                    // different integers can never share a variant hash.
                    var raw = value.GetRawText();
                    if (!value.TryGetDouble(out var number)
                        || !double.IsFinite(number)
                        || (raw.AsSpan().IndexOfAny('.', 'e', 'E') < 0 && (!long.TryParse(raw, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var whole) || Math.Abs(whole) > MaxExactInteger)))
                    {
                        problems.Add(new VariantProblem(path, "invalid_value"));

                        return null;
                    }

                    // Stored as the number the hash sees: an exact integer or the double itself, never the author's spelling.
                    return raw.AsSpan().IndexOfAny('.', 'e', 'E') < 0 ? JsonValue.Create(long.Parse(raw, System.Globalization.CultureInfo.InvariantCulture)) : JsonValue.Create(number);
                }

            case JsonValueKind.String:
                {
                    string text;
                    try
                    {
                        text = value.GetString()!;
                    }
                    catch (InvalidOperationException)
                    {
                        // An escaped lone surrogate cannot be decoded.
                        problems.Add(new VariantProblem(path, "invalid_value"));

                        return null;
                    }

                    return StringValue(text, key, path, problems);
                }

            case JsonValueKind.Array:
                {
                    if (value.GetArrayLength() > MaxArrayItems)
                    {
                        problems.Add(new VariantProblem(path, "too_many_items"));

                        return null;
                    }

                    var array = new JsonArray();
                    var index = 0;
                    foreach (var item in value.EnumerateArray())
                    {
                        if (item.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                        {
                            problems.Add(new VariantProblem($"{path}/{index}", "not_scalar"));
                        }
                        else if (item.ValueKind != JsonValueKind.Null)
                        {
                            array.Add(Scalar(item, key, $"{path}/{index}", problems));
                        }

                        index++;
                    }

                    return array;
                }

            default:
                problems.Add(new VariantProblem(path, "not_scalar"));

                return null;
        }
    }

    private static JsonValue? StringValue(string text, string key, string path, List<VariantProblem> problems)
    {
        if (text.Length > MaxStringChars || !TextRules.IsClean(text))
        {
            problems.Add(new VariantProblem(path, "invalid_value"));

            return null;
        }

        if (SecretShape.ValueLooksSecret(text))
        {
            problems.Add(new VariantProblem(path, "secret_value"));

            return null;
        }

        return JsonValue.Create(EnumLike.Contains(key) ? text.Trim().ToLowerInvariant() : text);
    }

    /// <summary>Variant keys: 1-64 characters, the first an ASCII letter or <c>_</c>, the rest also digits, <c>.</c> and <c>-</c>.</summary>
    internal static bool IsValidKey(string key) => AsciiShape.IsMatch(key, 1, 64, KeyFirst, AsciiShape.AlnumDotDash);
}
