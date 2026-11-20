using System.Text.Json;
using System.Text.Json.Nodes;

namespace Evidgate.Modules.Authoring.Domain;

internal static partial class AuthoringContent
{
    private static string? Steps(JsonElement content, List<(string Path, string Reason)> problems)
    {
        if (!content.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Object)
        {
            problems.Add(("/content/steps", "object_required"));
            return null;
        }

        string raw;
        try
        {
            raw = steps.GetRawText();
        }
        catch (InvalidOperationException)
        {
            // The document holds bytes that are not valid UTF-16 (a lone surrogate written as a value or a name), so even
            // its raw text cannot be read. Same shape as the value guard below: a caller error, never a 500 (OI-13-116).
            problems.Add(("/content/steps", "invalid_text"));
            return null;
        }

        // A duplicate member name is a caller error the node model refuses to build, so it is refused here rather than
        // surfacing as a 500 from JsonNode: the document never reaches the validator or the column.
        try
        {
            using var _ = JsonDocument.Parse(
                raw,
                new JsonDocumentOptions { AllowDuplicateProperties = false });
        }
        catch (JsonException)
        {
            problems.Add(("/content/steps", "invalid_json"));
            return null;
        }
        catch (InvalidOperationException)
        {
            // Duplicate detection forces the reader to materialise every member name, and a name that is not valid
            // UTF-16 throws there instead of at the point a value would have been read (OI-13-116).
            problems.Add(("/content/steps", "invalid_text"));
            return null;
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(raw);
        }
        catch (JsonException)
        {
            problems.Add(("/content/steps", "invalid_json"));
            return null;
        }
        catch (InvalidOperationException)
        {
            // The node model materialises member names and string values, so an unpaired surrogate fails here too.
            problems.Add(("/content/steps", "invalid_text"));
            return null;
        }

        if (node is null)
        {
            return raw;
        }

        if (ContainsNul(node))
        {
            // A NUL inside a step (its text, a `data` member or a member name) is refused here for the same reason as a
            // value of the case itself: the `jsonb` column cannot store it (22P05), so it must be a caller error now
            // rather than a 500 at the insert (security S-1/S-2).
            problems.Add(("/content/steps", "invalid_text"));
            return null;
        }

        // Every string leaf is NFC-normalised before the document is stored, so the composed and the decomposed spelling
        // of one step are one stored document, one content_hash and one version (AC14). The tree is normalised in place,
        // which keeps member order, so the serialised text is stable.
        //
        // The catch is this narrow on purpose: the only two exceptions a leaf of caller JSON can raise here are the
        // normaliser refusing text that is not valid UTF-16 (ArgumentException) and the reader's transcode of an
        // unpaired surrogate failing the same way (InvalidOperationException). Nothing in this method touches the
        // service graph, so an infrastructure failure is never turned into a caller error here.
        try
        {
            NormaliseLeaves(node);
            return node.ToJsonString();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            problems.Add(("/content/steps", "invalid_text"));
            return null;
        }
    }

    /// <summary>
    /// NFC-normalises every string leaf of a JSON document in place; non-string leaves are left alone. Nested objects and
    /// arrays are walked, never reassigned: a node that is already attached to its parent cannot be assigned back into it
    /// (<c>JsonNode</c> refuses a node that already has a parent), and only a leaf value is replaced.
    /// </summary>
    /// <param name="node">The document or subtree.</param>
    private static void NormaliseLeaves(JsonNode node)
    {
        switch (node)
        {
            case JsonObject jsonObject:
                foreach (var property in jsonObject.ToList())
                {
                    if (property.Value is { } child)
                    {
                        if (child is JsonObject or JsonArray)
                        {
                            NormaliseLeaves(child);
                        }
                        else if (Composed(child) is { } composed)
                        {
                            jsonObject[property.Key] = composed;
                        }
                    }
                }

                break;

            case JsonArray jsonArray:
                for (var index = 0; index < jsonArray.Count; index++)
                {
                    if (jsonArray[index] is { } child)
                    {
                        if (child is JsonObject or JsonArray)
                        {
                            NormaliseLeaves(child);
                        }
                        else if (Composed(child) is { } composed)
                        {
                            jsonArray[index] = composed;
                        }
                    }
                }

                break;

            default:
                break;
        }
    }

    /// <summary>
    /// True when any member name or string leaf of <paramref name="node"/> carries U+0000 — the character a
    /// <c>text</c> column (22021) and a <c>jsonb</c> document (22P05) both refuse, so a document holding one cannot be
    /// stored (see <see cref="Compose"/>). Non-string leaves and numbers are not inspected.
    /// </summary>
    /// <param name="node">The document or subtree.</param>
    /// <returns><c>true</c> when the subtree is unstorable.</returns>
    private static bool ContainsNul(JsonNode node)
    {
        switch (node)
        {
            case JsonObject jsonObject:
                foreach (var property in jsonObject)
                {
                    if (AuthoringText.ContainsNul(property.Key)
                        || (property.Value is { } value && ContainsNul(value)))
                    {
                        return true;
                    }
                }

                return false;

            case JsonArray jsonArray:
                foreach (var item in jsonArray)
                {
                    if (item is { } element && ContainsNul(element))
                    {
                        return true;
                    }
                }

                return false;

            case JsonValue jsonValue:
                try
                {
                    return jsonValue.TryGetValue<string>(out var text) && AuthoringText.ContainsNul(text);
                }
                catch (InvalidOperationException)
                {
                    // An unpaired surrogate makes the reader throw when the leaf is read. That leaf is not a NUL
                    // finding: the NFC pass that follows answers it as the same invalid_text problem row it always did.
                    return false;
                }

            default:
                return false;
        }
    }

    /// <summary>The NFC-composed replacement of one leaf value, or null when the leaf is not a string to compose.</summary>
    /// <param name="node">The leaf.</param>
    /// <returns>A detached <see cref="JsonValue"/> for a string leaf, null for every other value.</returns>
    private static JsonValue? Composed(JsonNode node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text)
            ? JsonValue.Create(AuthoringText.Normalize(text))
            : null;

    private static void Length(string? text, string path, int maximum, List<(string Path, string Reason)> problems)
    {
        if (text is null)
        {
            return;
        }

        if (text.Length == 0)
        {
            problems.Add(("/content/" + path, "required"));
        }
        else if (!AuthoringText.Fits(text, maximum))
        {
            problems.Add(("/content/" + path, "too_long"));
        }
    }
}
