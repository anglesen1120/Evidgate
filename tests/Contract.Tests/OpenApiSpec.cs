using System.Text.Json.Nodes;

namespace Contract.Tests;

/// <summary>
/// The parts of <c>contracts/openapi.yaml</c> the contract suites assert against
/// (01.3.2-T3, AC4) — the document's <c>openapi</c> version, its path keys, and a
/// selected <c>components.schemas</c> entry as a JSON Schema document (the spec is
/// the only source of wire shapes, so the tests read it, never a copy).
/// </summary>
/// <remarks>
/// The blocks are read with <see cref="YamlSubset"/>, the deliberately small YAML
/// reader documented there. A member the reader cannot parse throws rather than
/// returning a partial document, and path <em>items</em> are intentionally never
/// parsed: several of the spec's path descriptions use folded block scalars, so
/// only the path keys — which the suites diff — are extracted by indentation.
/// </remarks>
internal sealed class OpenApiSpec
{
    /// <summary>JSON Schema dialect OpenAPI 3.1 documents use (the spec declares <c>openapi: 3.1.0</c>).</summary>
    public const string SchemaDialect = "https://json-schema.org/draft/2020-12/schema";

    private readonly string[] _lines;
    private readonly IReadOnlyDictionary<string, int> _schemaStarts;
    private readonly Dictionary<string, JsonObject> _schemaCache = new(StringComparer.Ordinal);

    private OpenApiSpec(
        string[] lines,
        string version,
        IReadOnlyList<string> pathKeys,
        IReadOnlyDictionary<string, int> schemaStarts)
    {
        _lines = lines;
        Version = version;
        PathKeys = pathKeys;
        _schemaStarts = schemaStarts;
        SchemaNames = [.. schemaStarts.Keys];
    }

    /// <summary>The document's <c>openapi</c> field, e.g. <c>3.1.0</c>.</summary>
    public string Version { get; }

    /// <summary>The top-level <c>paths</c> keys, in document order.</summary>
    public IReadOnlyList<string> PathKeys { get; }

    /// <summary>The <c>components.schemas</c> names, in document order.</summary>
    public IReadOnlyList<string> SchemaNames { get; }

    /// <summary>Loads the repository's <c>contracts/openapi.yaml</c>.</summary>
    public static OpenApiSpec Load() => Load(Path.Combine(ContractPaths.ContractsDirectory, "openapi.yaml"));

    /// <summary>Loads a spec file.</summary>
    /// <exception cref="InvalidOperationException">A required member is missing.</exception>
    public static OpenApiSpec Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var lines = File.ReadAllLines(path);

        var version = TopLevelScalar(lines, "openapi")
            ?? throw new InvalidOperationException($"'{path}' has no top-level 'openapi' field");

        var pathsBlock = FindMember(lines, 0, lines.Length, 0, "paths")
            ?? throw new InvalidOperationException($"'{path}' has no top-level 'paths' block");
        var pathKeys = MemberNames(lines, pathsBlock + 1, SectionEnd(lines, pathsBlock + 1, 0), 2);

        var components = FindMember(lines, 0, lines.Length, 0, "components")
            ?? throw new InvalidOperationException($"'{path}' has no top-level 'components' block");
        var componentsEnd = SectionEnd(lines, components + 1, 0);
        var schemas = FindMember(lines, components + 1, componentsEnd, 2, "schemas")
            ?? throw new InvalidOperationException($"'{path}' has no 'components.schemas' block");
        var schemasEnd = SectionEnd(lines, schemas + 1, 2);

        var starts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (name, index) in MemberEntries(lines, schemas + 1, schemasEnd, 4))
        {
            starts[name] = index;
        }

        return new OpenApiSpec(lines, version, pathKeys, starts);
    }

    /// <summary>The named <c>components.schemas</c> entry as a JSON Schema, with <c>$schema</c>/<c>$id</c> injected.</summary>
    /// <exception cref="KeyNotFoundException">The spec declares no such schema.</exception>
    public JsonObject Schema(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (_schemaCache.TryGetValue(name, out var cached))
        {
            return cached;
        }

        if (!_schemaStarts.TryGetValue(name, out var start))
        {
            throw new KeyNotFoundException($"'{name}' is not in components.schemas (declared: {string.Join(", ", SchemaNames)})");
        }

        var end = SectionEnd(_lines, start + 1, 4);
        var text = new System.Text.StringBuilder();
        for (var i = start + 1; i < end; i++)
        {
            var line = _lines[i];
            text.Append(line.Length >= 4 ? line[4..] : line).Append('\n');
        }

        if (YamlSubset.Parse(text.ToString()) is not JsonObject schema)
        {
            throw new InvalidOperationException($"'components.schemas.{name}' did not parse to a mapping");
        }

        schema["$schema"] = SchemaDialect;
        schema["$id"] = $"urn:evidgate:contract-spec:{name}";
        _schemaCache[name] = schema;
        return schema;
    }

    /// <summary>The value of a top-level <c>key: value</c> scalar, or null when the key is absent.</summary>
    private static string? TopLevelScalar(string[] lines, string key)
    {
        foreach (var line in lines)
        {
            if (TryKey(line, 0, out var name) && string.Equals(name, key, StringComparison.Ordinal))
            {
                return line[(line.LastIndexOf(':') + 1)..].Trim().Trim('\'', '"');
            }
        }

        return null;
    }

    private static int? FindMember(string[] lines, int start, int end, int indent, string name)
    {
        for (var i = start; i < end; i++)
        {
            var line = lines[i];
            var lineIndent = IndentOf(line);
            if (lineIndent < indent)
            {
                return null;
            }

            if (lineIndent == indent && TryKey(line, indent, out var key) && string.Equals(key, name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return null;
    }

    private static IReadOnlyList<string> MemberNames(string[] lines, int start, int end, int indent)
        => [.. MemberEntries(lines, start, end, indent).Select(entry => entry.Name)];

    private static IEnumerable<(string Name, int Index)> MemberEntries(string[] lines, int start, int end, int indent)
    {
        for (var i = start; i < end; i++)
        {
            if (IndentOf(lines[i]) == indent && TryKey(lines[i], indent, out var name))
            {
                yield return (name, i);
            }
        }
    }

    /// <summary>Index one past the last line of a section whose members sit at <paramref name="indent"/>.</summary>
    private static int SectionEnd(string[] lines, int start, int indent)
    {
        for (var i = start; i < lines.Length; i++)
        {
            var line = lines[i];
            if (IsBlank(line))
            {
                continue;
            }

            if (IndentOf(line) <= indent)
            {
                return i;
            }
        }

        return lines.Length;
    }

    /// <summary>Reads the key of a mapping line, false for anything else (sequences, prose, blank).</summary>
    private static bool TryKey(string line, int indent, out string key)
    {
        key = string.Empty;
        var trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] == '#' || trimmed[0] == '-' || IndentOf(line) != indent)
        {
            return false;
        }

        // A mapping entry: the first colon after the indent, followed by a space
        // (or ending the line). 'key:value' is a plain scalar, not an entry.
        var colon = line.IndexOf(':', indent);
        if (colon < 0 || (colon + 1 < line.Length && line[colon + 1] != ' '))
        {
            return false;
        }

        key = line[indent..colon].Trim();
        if (key.Length >= 2 && key[0] is '\'' or '"' && key[^1] == key[0])
        {
            key = key[1..^1];
        }

        return key.Length > 0;
    }

    private static int IndentOf(string line)
    {
        var indent = 0;
        while (indent < line.Length && line[indent] == ' ')
        {
            indent++;
        }

        return indent;
    }

    private static bool IsBlank(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.Length == 0 || trimmed[0] == '#';
    }
}
