using System.Text.Json;
using Evidgate.BuildingBlocks.Contracts.Validation;

namespace Evidgate.Modules.Orchestrator.Domain;

/// <summary>
/// Body rules shared by the manifest and environment-revision commands (05.1.1/05.1.2 review): the strings a body may carry,
/// and the error paths it may be described by.
/// </summary>
internal static class JsonBody
{
    /// <summary>Map members whose names are caller data: a path never names them (they may be secrets).</summary>
    private static readonly HashSet<string> MapMembers = new(StringComparer.Ordinal) { "params", "base_urls", "dependency_versions" };

    /// <summary>
    /// True when a member name or string anywhere in <paramref name="element"/> is no text PostgreSQL and the RFC 8785 form can
    /// carry: an unpaired UTF-16 surrogate (<c>"\ud800"</c>, which also makes the schema engine throw) or U+0000 (refused by
    /// jsonb). Checked before the schema runs, so such a body is a 422, never a 400 or a 500.
    /// </summary>
    internal static bool HasUnstorableText(JsonElement element)
    {
        try
        {
            return Walk(element);
        }
        catch (InvalidOperationException)
        {
            // System.Text.Json refuses to transcode an invalid escape sequence.
            return true;
        }

        static bool Walk(JsonElement element) => element.ValueKind switch
        {
            JsonValueKind.String => Unstorable(element.GetString()!),
            JsonValueKind.Array => element.EnumerateArray().Any(Walk),
            JsonValueKind.Object => element.EnumerateObject().Any(member => Unstorable(member.Name) || Walk(member.Value)),
            _ => false,
        };
    }

    /// <summary>NUL, or a surrogate without its partner.</summary>
    internal static bool Unstorable(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\0')
            {
                return true;
            }

            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1]))
                {
                    return true;
                }

                i++;
            }
            else if (char.IsLowSurrogate(c))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The path of a schema error that names no caller data: a member of <c>params</c>, <c>base_urls</c> or
    /// <c>dependency_versions</c> becomes <c>*</c>, the unknown member of an <c>additional_properties</c> error is cut, and
    /// the path is capped at 200 characters.
    /// </summary>
    internal static string SafePath(ContractValidationError error)
    {
        var segments = error.Path.Split('/');
        for (var i = 1; i < segments.Length; i++)
        {
            if (MapMembers.Contains(segments[i - 1]))
            {
                segments[i] = "*";
            }
        }

        if (string.Equals(error.Reason, "additional_properties", StringComparison.Ordinal) && segments.Length > 1)
        {
            segments = segments[..^1];
        }

        var path = string.Join('/', segments);

        return path.Length > 200 ? path[..200] : path;
    }
}
