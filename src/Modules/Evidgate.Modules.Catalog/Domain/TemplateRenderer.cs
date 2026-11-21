using System.Text;
using System.Text.Json;

namespace Evidgate.Modules.Catalog.Domain;

/// <summary>
/// Renders one packaged template version: substitutes <c>{{param}}</c> in the member paths and contents, stamps a
/// fresh <c>evidgateId</c>, drops the <c>.tmpl</c> suffix and refuses a parameter that could break out of the
/// TypeScript literal it lands in.
/// </summary>
/// <remarks>
/// Values are refused rather than escaped: a quote, a newline, a backslash or a <c>..</c> segment means the caller
/// is inventing a value the template never expected, and the safe answer is an error. Every placeholder must be a
/// declared parameter or <c>evidgateId</c>, so a typo cannot silently ship as <c>{{typo}}</c>.
/// </remarks>
public static class TemplateRenderer
{
    /// <summary>Largest manifest parsed.</summary>
    internal const int MaxManifestChars = 65536;

    /// <summary>Largest number of packaged files rendered.</summary>
    internal const int MaxFiles = 256;

    /// <summary>Renders every packaged file of a template version.</summary>
    /// <param name="manifestJson">The template's <c>template.json</c>.</param>
    /// <param name="files">Packaged member paths and their contents, before substitution.</param>
    /// <param name="parameters">Values overriding the manifest's defaults.</param>
    /// <returns>Rendered paths and contents.</returns>
    /// <exception cref="ArgumentException">The manifest is unusable, a parameter is unsafe or unknown, a placeholder is undeclared, or two members render to the same path.</exception>
    public static Dictionary<string, string> Render(
        string manifestJson,
        IReadOnlyDictionary<string, string> files,
        IReadOnlyDictionary<string, string>? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(manifestJson);
        ArgumentNullException.ThrowIfNull(files);

        if (files.Count > MaxFiles)
        {
            throw new ArgumentException($"A template version carries at most {MaxFiles} files.", nameof(files));
        }

        var values = ReadParameters(manifestJson);

        if (parameters is not null)
        {
            foreach (var (name, value) in parameters)
            {
                if (!values.ContainsKey(name))
                {
                    throw new ArgumentException($"'{name}' is not a parameter of this template.", nameof(parameters));
                }

                Validate(name, value);
                values[name] = value;
            }
        }

        values["evidgateId"] = Guid.CreateVersion7().ToString("D");

        var rendered = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, content) in files)
        {
            var target = Substitute(path, values);
            if (target.EndsWith(".tmpl", StringComparison.Ordinal))
            {
                target = target[..^".tmpl".Length];
            }

            if (!rendered.TryAdd(target, Substitute(content, values)))
            {
                throw new ArgumentException($"'{target}' is rendered twice.", nameof(files));
            }
        }

        return rendered;
    }

    private static Dictionary<string, string> ReadParameters(string manifestJson)
    {
        if (manifestJson.Length == 0 || manifestJson.Length > MaxManifestChars)
        {
            throw new ArgumentException("The manifest is empty or too large.", nameof(manifestJson));
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(manifestJson);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("The manifest is not valid JSON.", nameof(manifestJson), exception);
        }

        using (document)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!document.RootElement.TryGetProperty("params", out var parameters) || parameters.ValueKind != JsonValueKind.Object)
            {
                return values;
            }

            foreach (var parameter in parameters.EnumerateObject())
            {
                if (parameter.Value.ValueKind != JsonValueKind.Object
                    || !parameter.Value.TryGetProperty("default", out var value)
                    || value.ValueKind != JsonValueKind.String
                    || value.GetString() is not { } text)
                {
                    throw new ArgumentException($"Parameter '{parameter.Name}' has no string default.", nameof(manifestJson));
                }

                Validate(parameter.Name, text);
                values[parameter.Name] = text;
            }

            return values;
        }
    }

    private static void Validate(string name, string value)
    {
        if (name.Length > 64 || value.Length is 0 or > 256
            || value.Contains("{{", StringComparison.Ordinal) || value.Contains("}}", StringComparison.Ordinal)
            || value.Contains('\\') || value.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Parameter '{name}' holds a value a template cannot carry.");
        }

        if (name.Equals("url", StringComparison.OrdinalIgnoreCase) || name.EndsWith("Url", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                || value.Contains('\'') || value.Contains('`')
                || value.Any(char.IsWhiteSpace))
            {
                throw new ArgumentException($"Parameter '{name}' is not a plain http(s) URL.");
            }

            return;
        }

        // `pageName` names a TypeScript class, so a value that only satisfies the character set below
        // (`1Home`, `Home Page`, `home-page`) would still render source that cannot compile.
        if (name.Equals("pageName", StringComparison.Ordinal) && !IsIdentifier(value))
        {
            throw new ArgumentException($"Parameter '{name}' is not a TypeScript identifier.");
        }

        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not (' ' or '_' or '-' or ','))
            {
                throw new ArgumentException($"Parameter '{name}' holds a character a template cannot carry.");
            }
        }
    }

    private static bool IsIdentifier(string value) =>
        value.Length > 0
        && (char.IsAsciiLetter(value[0]) || value[0] == '_')
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');

    private static string Substitute(string text, Dictionary<string, string> values)
    {
        var open = text.IndexOf("{{", StringComparison.Ordinal);
        if (open < 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length + 64);
        var cursor = 0;
        while (open >= 0)
        {
            builder.Append(text, cursor, open - cursor);
            var close = text.IndexOf("}}", open + 2, StringComparison.Ordinal);
            if (close < 0)
            {
                throw new ArgumentException("A placeholder is never closed.");
            }

            var name = text[(open + 2)..close];
            if (!values.TryGetValue(name, out var value))
            {
                throw new ArgumentException($"Placeholder '{{{{{name}}}}}' is not a declared parameter.");
            }

            builder.Append(value);
            cursor = close + 2;
            open = text.IndexOf("{{", cursor, StringComparison.Ordinal);
        }

        builder.Append(text, cursor, text.Length - cursor);
        return builder.ToString();
    }
}
