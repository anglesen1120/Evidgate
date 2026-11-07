using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

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
/// returning a partial document; a schema's folded prose is dropped before the
/// parse instead (<see cref="Schema"/>), and path <em>items</em> are intentionally
/// never parsed: several of the spec's path descriptions use folded block scalars,
/// so only the path keys — which the suites diff — are extracted by indentation.
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

    /// <summary>The status keys one operation declares under <c>responses</c>, in document order.</summary>
    /// <exception cref="KeyNotFoundException">The path or the method is absent from the spec.</exception>
    public IReadOnlyList<string> ResponseKeys(string pathKey, string method)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pathKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(method);

        var paths = FindMember(_lines, 0, _lines.Length, 0, "paths")
            ?? throw new InvalidOperationException("the spec has no top-level 'paths' block");
        var path = FindMember(_lines, paths + 1, SectionEnd(_lines, paths + 1, 0), 2, pathKey)
            ?? throw new KeyNotFoundException($"the spec declares no path '{pathKey}'");
        var operation = FindMember(_lines, path + 1, SectionEnd(_lines, path + 1, 2), 4, method)
            ?? throw new KeyNotFoundException($"'{pathKey}' declares no '{method}' operation");
        var responses = FindMember(_lines, operation + 1, SectionEnd(_lines, operation + 1, 4), 6, "responses")
            ?? throw new InvalidOperationException($"'{method} {pathKey}' declares no responses");

        return MemberNames(_lines, responses + 1, SectionEnd(_lines, responses + 1, 6), 8);
    }

    /// <summary>Loads the repository's <c>contracts/openapi.yaml</c>.</summary>
    public static OpenApiSpec Load() => Load(Path.Combine(ContractPaths.ContractsDirectory, "openapi.yaml"));

    /// <summary>
    /// One operation of a path item as a parsed mapping: its <c>parameters</c>, <c>requestBody</c> and
    /// <c>responses</c>. Folded prose is dropped, as it is for a schema, because <see cref="YamlSubset"/> refuses
    /// block scalars by design.
    /// </summary>
    /// <exception cref="KeyNotFoundException">The path or the method is absent from the spec.</exception>
    public JsonObject Operation(string pathKey, string method)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pathKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(method);

        var paths = FindMember(_lines, 0, _lines.Length, 0, "paths")
            ?? throw new InvalidOperationException("the spec has no top-level 'paths' block");
        var path = FindMember(_lines, paths + 1, SectionEnd(_lines, paths + 1, 0), 2, pathKey)
            ?? throw new KeyNotFoundException($"the spec declares no path '{pathKey}'");
        var operation = FindMember(_lines, path + 1, SectionEnd(_lines, path + 1, 2), 4, method)
            ?? throw new KeyNotFoundException($"'{pathKey}' declares no '{method}' operation");

        return ParseEntry(operation, $"'{method} {pathKey}'");
    }

    /// <summary>
    /// The parameters an operation declares, every <c>$ref</c> resolved to the parameter it names, so an inline
    /// parameter and a shared one are read the same way.
    /// </summary>
    /// <param name="pathKey">The spec path key.</param>
    /// <param name="method">The lower-case HTTP method.</param>
    /// <param name="location">The <c>in</c> value to keep (<c>header</c>, <c>path</c>, <c>query</c>), or null for all.</param>
    public IReadOnlyList<JsonObject> OperationParameters(string pathKey, string method, string? location = null)
    {
        var resolved = new List<JsonObject>();
        if (Operation(pathKey, method)["parameters"] is not JsonArray declared)
        {
            return resolved;
        }

        foreach (var entry in declared.OfType<JsonObject>())
        {
            var parameter = entry["$ref"] is JsonValue reference
                ? ComponentEntry("parameters", Leaf(reference.GetValue<string>()))
                : entry;

            if (location is null || string.Equals(parameter["in"]?.GetValue<string>(), location, StringComparison.Ordinal))
            {
                resolved.Add(parameter);
            }
        }

        return resolved;
    }

    /// <summary>
    /// A line block joined into one line. Only for prose the parsed model cannot carry: a folded
    /// <c>description</c> is dropped when the entry is parsed, so a documented sentence is read as text.
    /// </summary>
    /// <param name="operationId">The operation's <c>operationId</c>, unique in the spec.</param>
    public string DocumentedText(string operationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        var operation = Array.FindIndex(_lines, line => line.Trim() == $"operationId: {operationId}");
        if (operation < 0)
        {
            throw new KeyNotFoundException($"the spec declares no operation '{operationId}'");
        }

        var start = operation;
        while (start > 0 && IndentOf(_lines[start]) > 2)
        {
            start--;
        }

        return BlockText(start);
    }

    /// <summary>A <c>components</c> child entry as a parsed mapping.</summary>
    private JsonObject ComponentEntry(string section, string name) =>
        ParseEntry(FindEntryLine(section, name), $"components.{section}.{name}");

    /// <summary>The line a <c>components</c> child entry starts on.</summary>
    private int FindEntryLine(string section, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var components = FindMember(_lines, 0, _lines.Length, 0, "components")
            ?? throw new InvalidOperationException("the spec has no top-level 'components' block");
        var block = FindMember(_lines, components + 1, SectionEnd(_lines, components + 1, 0), 2, section)
            ?? throw new KeyNotFoundException($"the spec has no 'components.{section}' block");

        return FindMember(_lines, block + 1, SectionEnd(_lines, block + 1, 2), 4, name)
            ?? throw new KeyNotFoundException($"'components.{section}' declares no '{name}'");
    }

    /// <summary>The line block starting at <paramref name="start"/>, joined into one line.</summary>
    private string BlockText(int start)
    {
        var indent = IndentOf(_lines[start]);
        var end = SectionEnd(_lines, start + 1, indent);
        return Regex.Replace(string.Join(' ', _lines[start..end].Select(line => line.Trim())), @"\s+", " ");
    }

    /// <summary>The mapping entry starting at <paramref name="start"/>, its members dedented to column zero.</summary>
    private JsonObject ParseEntry(int start, string what)
    {
        var indent = IndentOf(_lines[start]);
        var end = SectionEnd(_lines, start + 1, indent);
        var text = new System.Text.StringBuilder();
        foreach (var line in ShapeLines(start + 1, end))
        {
            text.Append(line.Length >= indent + 2 ? line[(indent + 2)..] : line.Trim()).Append('\n');
        }

        if (YamlSubset.Parse(text.ToString()) is not JsonObject mapping)
        {
            throw new InvalidOperationException($"'{what}' did not parse to a mapping");
        }

        return mapping;
    }

    /// <summary>The last segment of a <c>#/components/…/Name</c> reference.</summary>
    private static string Leaf(string reference) => reference[(reference.LastIndexOf('/') + 1)..];

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

    /// <summary>
    /// The named <c>components.schemas</c> entry as a JSON Schema, with <c>$schema</c>/<c>$id</c> injected. Folded
    /// prose (<c>description: &gt;-</c>) is dropped first; <see cref="ShapeLines"/> says why and what stays.
    /// </summary>
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
        foreach (var line in ShapeLines(start + 1, end))
        {
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

    /// <summary>
    /// The lines of a schema entry without its prose: a <c>description:</c> written as a block scalar (<c>&gt;</c>,
    /// <c>|</c>, with an optional chomping or indentation indicator) and its deeper-indented body are dropped, because
    /// <see cref="YamlSubset"/> refuses block scalars by design and validation only needs the shape keywords. Only
    /// such an entry is dropped, so prose written any other way still fails the reader loudly instead of silently
    /// shrinking the schema.
    /// </summary>
    private IEnumerable<string> ShapeLines(int start, int end)
    {
        var index = start;
        while (index < end)
        {
            var line = _lines[index++];
            if (!IsBlockScalarEntry(line, out var indent))
            {
                yield return line;
                continue;
            }

            while (index < end && (IsBlank(_lines[index]) || IndentOf(_lines[index]) > indent))
            {
                index++;
            }
        }
    }

    /// <summary>True for a <c>description:</c> mapping entry whose value is a block scalar; <paramref name="indent"/> is the entry's.</summary>
    private static bool IsBlockScalarEntry(string line, out int indent)
    {
        indent = IndentOf(line);
        const string Key = "description:";
        if (!line.AsSpan(indent).StartsWith(Key, StringComparison.Ordinal))
        {
            return false;
        }

        var value = line[(indent + Key.Length)..].Trim();
        return value.Length > 0 && value[0] is '>' or '|';
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
            if (IsBlank(line))
            {
                continue;
            }

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

        // A mapping entry: the first colon after the indent that is followed by a space (or ends the
        // line). 'key:value' is a plain scalar, not an entry, but a custom-method path key such as
        // `/projects/{p}/authoring/changes:apply:` carries a colon inside the key itself, so the entry
        // cannot end at the first colon found.
        var colon = -1;
        for (var at = line.IndexOf(':', indent); at >= 0; at = line.IndexOf(':', at + 1))
        {
            if (at + 1 >= line.Length || line[at + 1] == ' ')
            {
                colon = at;
                break;
            }
        }

        if (colon < 0)
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
