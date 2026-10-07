using System.Globalization;

namespace Isolation.Tests.Api;

/// <summary>
/// Every operation <c>contracts/openapi.yaml</c> declares, as <see cref="ApiRoute"/>s
/// (02.9.1 AC2): the spec side of the route-coverage gate.
/// </summary>
/// <remarks>
/// The spec is read by indentation, the approach of
/// <c>tests/Contract.Tests/OpenApiSpec.cs</c> (path keys at indent 2 under the
/// top-level <c>paths:</c>), extended one level down to the operation keys at
/// indent 4. Path items are never parsed as YAML: several use folded block
/// scalars. A block scalar's body is indented deeper than its key, and every key
/// under a path item sits at indent 4, so a line inside a folded description can
/// never be read as an operation. Spec paths are relative to the single
/// <c>servers[0].url</c> (<c>/v1</c>), which is prefixed to each template.
/// Anything the reader does not expect in the <c>paths</c> block throws with the
/// file and line, never returning a partial inventory.
/// </remarks>
public static class OpenApiRouteInventory
{
    private const int PathIndent = 2;
    private const int OperationIndent = 4;

    private static readonly HashSet<string> OperationKeys = new(StringComparer.Ordinal)
    {
        "get", "put", "post", "patch", "delete", "head", "options", "trace",
    };

    private static readonly Lazy<string> RepoRoot = new(LocateRepoRoot);

    /// <summary>The repository's <c>contracts/openapi.yaml</c>.</summary>
    public static string SpecPath => Path.Combine(RepoRoot.Value, "contracts", "openapi.yaml");

    /// <summary>Loads the repository's spec.</summary>
    /// <exception cref="InvalidOperationException">The spec's <c>servers</c> or <c>paths</c> block is malformed.</exception>
    public static IReadOnlyList<ApiRoute> Load() => Load(SpecPath);

    /// <summary>Loads a spec file.</summary>
    /// <exception cref="InvalidOperationException">The spec's <c>servers</c> or <c>paths</c> block is malformed.</exception>
    public static IReadOnlyList<ApiRoute> Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Parse(File.ReadAllLines(path), path);
    }

    /// <summary>Reads the operations of spec text, in document order, each once.</summary>
    /// <param name="lines">The spec's lines.</param>
    /// <param name="source">The name used in error messages (a file path).</param>
    /// <exception cref="InvalidOperationException">The <c>servers</c> or <c>paths</c> block is malformed; the message names the line.</exception>
    public static IReadOnlyList<ApiRoute> Parse(IReadOnlyList<string> lines, string source)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        var prefix = ServerPrefix(lines, source);
        var pathsLine = TopLevelKey(lines, "paths")
            ?? throw Malformed(source, null, "no top-level 'paths:' block");
        if (InlineValue(lines[pathsLine]).Length > 0)
        {
            throw Malformed(source, pathsLine, "'paths:' must be a block mapping");
        }

        var routes = new List<ApiRoute>();
        var seenRoutes = new HashSet<ApiRoute>();
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);
        string? currentPath = null;
        var currentPathLine = 0;
        var currentOperations = 0;
        var previousKeyIsOperation = false;

        var end = SectionEnd(lines, pathsLine + 1);
        for (var i = pathsLine + 1; i < end; i++)
        {
            var line = lines[i];
            if (IsBlank(line))
            {
                continue;
            }

            if (line.TrimStart(' ').StartsWith('\t'))
            {
                throw Malformed(source, i, "tab indentation");
            }

            var indent = IndentOf(line);
            if (indent > OperationIndent)
            {
                // Inside an operation, a path-level parameter list or a block scalar body.
                continue;
            }

            if (indent == PathIndent)
            {
                if (!TryKey(line, indent, out var pathKey) || !pathKey.StartsWith('/'))
                {
                    throw Malformed(source, i, "expected a path key starting with '/' at indent 2");
                }

                if (InlineValue(line).Length > 0)
                {
                    throw Malformed(source, i, $"path item '{pathKey}' must be a block mapping");
                }

                if (!seenPaths.Add(pathKey))
                {
                    throw Malformed(source, i, $"duplicate path key '{pathKey}'");
                }

                RequireOperations(source, currentPath, currentPathLine, currentOperations);
                currentPath = pathKey;
                currentPathLine = i;
                currentOperations = 0;
                previousKeyIsOperation = false;
                continue;
            }

            if (indent != OperationIndent || currentPath is null)
            {
                throw Malformed(source, i, currentPath is null
                    ? "content before the first path key"
                    : string.Create(CultureInfo.InvariantCulture, $"unexpected indent {indent} (path keys sit at 2, operations at 4)"));
            }

            if (line.TrimStart(' ').StartsWith('-'))
            {
                // A compact sequence item of a path-level 'parameters:' / 'servers:' key.
                if (previousKeyIsOperation)
                {
                    throw Malformed(source, i, $"sequence item directly under an operation of '{currentPath}'");
                }

                continue;
            }

            if (!TryKey(line, indent, out var key))
            {
                throw Malformed(source, i, $"expected a mapping key under '{currentPath}'");
            }

            previousKeyIsOperation = OperationKeys.Contains(key);
            if (!previousKeyIsOperation)
            {
                if (IsOperationIgnoringCase(key))
                {
                    throw Malformed(source, i, $"operation key '{key}' must be lower-case");
                }

                // parameters, summary, description, servers, $ref, x-*: not operations.
                continue;
            }

            if (InlineValue(line).Length > 0)
            {
                throw Malformed(source, i, $"operation '{key} {currentPath}' must be a block mapping");
            }

            ApiRoute route;
            try
            {
                route = new ApiRoute(key, prefix + currentPath);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidOperationException(Malformed(source, i, exception.Message).Message, exception);
            }

            if (!seenRoutes.Add(route))
            {
                throw Malformed(source, i, $"operation '{route}' is declared twice");
            }

            routes.Add(route);
            currentOperations++;
        }

        RequireOperations(source, currentPath, currentPathLine, currentOperations);
        if (routes.Count == 0)
        {
            throw Malformed(source, pathsLine, "the 'paths' block declares no operation");
        }

        return routes;
    }

    /// <summary>The single <c>servers[0].url</c>; spec paths are relative to it.</summary>
    private static string ServerPrefix(IReadOnlyList<string> lines, string source)
    {
        var serversLine = TopLevelKey(lines, "servers")
            ?? throw Malformed(source, null, "no top-level 'servers:' block (paths are relative to servers[0].url)");

        var end = SectionEnd(lines, serversLine + 1);
        for (var i = serversLine + 1; i < end; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                trimmed = trimmed[2..].TrimStart();
            }

            if (!trimmed.StartsWith("url:", StringComparison.Ordinal))
            {
                continue;
            }

            var url = trimmed["url:".Length..].Trim().Trim('\'', '"');
            if (url.Contains("://", StringComparison.Ordinal) && Uri.TryCreate(url, UriKind.Absolute, out var absolute))
            {
                url = absolute.AbsolutePath;
            }

            var prefix = ApiRoute.NormaliseTemplate(url);
            return string.Equals(prefix, ApiRoute.VersionPrefix, StringComparison.Ordinal)
                ? prefix
                : throw Malformed(source, i, $"servers[0].url is '{url}'; the isolation suite expects '{ApiRoute.VersionPrefix}'");
        }

        throw Malformed(source, serversLine, "'servers:' has no url");
    }

    private static void RequireOperations(string source, string? path, int pathLine, int operations)
    {
        if (path is not null && operations == 0)
        {
            throw Malformed(source, pathLine, $"path '{path}' declares no operation");
        }
    }

    private static bool IsOperationIgnoringCase(string key)
        => OperationKeys.Any(operation => string.Equals(operation, key, StringComparison.OrdinalIgnoreCase));

    private static int? TopLevelKey(IReadOnlyList<string> lines, string name)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (TryKey(lines[i], 0, out var key) && string.Equals(key, name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>Index one past the last line of a top-level section (the next non-blank line at indent 0).</summary>
    private static int SectionEnd(IReadOnlyList<string> lines, int start)
    {
        for (var i = start; i < lines.Count; i++)
        {
            if (!IsBlank(lines[i]) && IndentOf(lines[i]) == 0)
            {
                return i;
            }
        }

        return lines.Count;
    }

    /// <summary>Reads the key of a mapping line at exactly <paramref name="indent"/>, as OpenApiSpec.TryKey does.</summary>
    private static bool TryKey(string line, int indent, out string key)
    {
        key = string.Empty;
        var trimmed = line.TrimStart(' ');
        if (trimmed.Length == 0 || trimmed[0] is '#' or '-' || IndentOf(line) != indent)
        {
            return false;
        }

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

    /// <summary>The text after a key's colon, without a trailing comment.</summary>
    private static string InlineValue(string line)
    {
        var colon = line.IndexOf(':', IndentOf(line));
        var value = colon < 0 ? string.Empty : line[(colon + 1)..].Trim();
        return value.StartsWith('#') ? string.Empty : value;
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

    private static InvalidOperationException Malformed(string source, int? lineIndex, string problem)
        => new(lineIndex is { } index
            ? string.Create(CultureInfo.InvariantCulture, $"{source}:{index + 1}: malformed OpenAPI spec: {problem}")
            : $"{source}: malformed OpenAPI spec: {problem}");

    /// <summary>The directory holding <c>Evidgate.slnx</c>, found as <c>tests/Contract.Tests/ContractPaths.cs</c> does.</summary>
    private static string LocateRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Evidgate.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException($"Evidgate.slnx not found above '{AppContext.BaseDirectory}'");
    }
}
