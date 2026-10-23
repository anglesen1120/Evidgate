using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Contract.Tests;

/// <summary>
/// A deliberately small YAML reader used only by <see cref="OpenApiSpec"/> to turn
/// selected subtrees of <c>contracts/openapi.yaml</c> into <see cref="JsonNode"/>s
/// (01.3.2-T3). The repository pins no YAML library — every package pin needs an
/// ADR-0002 row and a <c>Directory.Packages.props</c> entry — so this reader covers
/// exactly the constructs the spec's schema blocks use:
/// <list type="bullet">
/// <item>block mappings (<c>key:</c> + indented child, or <c>key: value</c>)</item>
/// <item>block sequences (<c>- item</c>, including <c>- key: value</c> item mappings)</item>
/// <item>flow sequences (<c>[a, b]</c>)</item>
/// <item>plain, single- and double-quoted scalars, booleans, integers, floats, null</item>
/// </list>
/// Anything else — anchors, aliases, tags, flow mappings, block scalars
/// (<c>|</c>/<c>&gt;</c>), multi-line plain scalars, a duplicate key in one mapping
/// (YAML forbids it) — throws
/// <see cref="NotSupportedException"/> instead of being guessed at, so a spec edit
/// that outgrows this reader fails the suite loudly rather than silently
/// mis-validating a response. A duplicate key is refused rather than
/// assigned twice, because a silent last-wins would let an edit drop a required
/// member without any suite noticing. It parses a whole input: any unconsumed line is a
/// failure, never a silent truncation.
/// </summary>
internal sealed class YamlSubset
{
    private readonly List<string> _lines;
    private int _index;

    private YamlSubset(List<string> lines) => _lines = lines;

    /// <summary>Parses a document whose root is a mapping or sequence; null for empty input.</summary>
    /// <exception cref="NotSupportedException">A construct outside the supported subset, or trailing content.</exception>
    public static JsonNode? Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var parser = new YamlSubset([.. text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')]);
        var indent = parser.PeekIndent();
        if (indent < 0)
        {
            return null;
        }

        var node = parser.ParseBlock(indent);
        if (parser.PeekIndent() >= 0)
        {
            throw new NotSupportedException($"line {parser._index + 1}: unconsumed content after the document: '{parser._lines[parser._index]}'");
        }

        return node;
    }

    private static int IndentOf(string line)
    {
        var indent = 0;
        while (indent < line.Length && line[indent] == ' ')
        {
            indent++;
        }

        return indent < line.Length && line[indent] == '\t'
            ? throw new NotSupportedException("tabs are not valid YAML indentation")
            : indent;
    }

    private static bool IsBlank(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.Length == 0 || trimmed[0] == '#';
    }

    /// <summary>Advances past blank/comment lines; returns the next content indent or -1 at EOF.</summary>
    private int PeekIndent()
    {
        while (_index < _lines.Count && IsBlank(_lines[_index]))
        {
            _index++;
        }

        return _index >= _lines.Count ? -1 : IndentOf(_lines[_index]);
    }

    private static bool IsSequenceItem(string line, int indent)
    {
        var trimmed = line.TrimStart();
        return IndentOf(line) == indent && trimmed.StartsWith('-') && (trimmed.Length == 1 || trimmed[1] == ' ');
    }

    private JsonNode? ParseBlock(int indent)
        => IsSequenceItem(_lines[_index], indent) ? ParseSequence(indent) : ParseMapping(indent);

    private JsonObject ParseMapping(int indent)
    {
        var result = new JsonObject();
        while (PeekIndent() == indent)
        {
            var line = _lines[_index];
            if (IsSequenceItem(line, indent))
            {
                break;
            }

            var entryLine = _index + 1;
            var (key, rest) = SplitEntry(line, entryLine);
            _index++;
            AddMember(result, key, rest.Length == 0 ? ParseChild(indent) : ParseScalar(rest, _index), entryLine);
        }

        return result;
    }

    /// <summary>
    /// Adds one member to a mapping, refusing a key the mapping already holds:
    /// YAML forbids duplicate keys, and a silent last-wins would let a spec edit
    /// replace a required member without any suite noticing. Every mapping —
    /// the document root, a nested map, and the map a <c>- key: value</c>
    /// sequence item degrades into — is built here, so the check cannot be
    /// bypassed by nesting.
    /// </summary>
    private static void AddMember(JsonObject mapping, string key, JsonNode? value, int lineNumber)
    {
        if (!mapping.TryAdd(key, value))
        {
            throw new NotSupportedException($"line {lineNumber}: duplicate mapping key '{key}'");
        }
    }

    /// <summary>
    /// The value of a key that has no inline value: an indented block, a same-indent
    /// sequence (YAML allows <c>key:</c> then <c>- x</c> at the key's own indent), or null.
    /// </summary>
    private JsonNode? ParseChild(int parentIndent)
    {
        var indent = PeekIndent();
        if (indent < 0)
        {
            return null;
        }

        if (indent > parentIndent)
        {
            return ParseBlock(indent);
        }

        return indent == parentIndent && IsSequenceItem(_lines[_index], indent) ? ParseSequence(indent) : null;
    }

    private JsonArray ParseSequence(int indent)
    {
        var result = new JsonArray();
        while (PeekIndent() == indent)
        {
            var line = _lines[_index];
            if (!IsSequenceItem(line, indent))
            {
                break;
            }

            var content = ContentStart(line);
            if (content < 0)
            {
                _index++;
                result.Add(ParseChild(indent));
                continue;
            }

            var item = line[content..];
            if (TryFindKeyEnd(item, _index + 1, out _))
            {
                // "- key: value": YAML treats the item as a mapping whose keys align
                // at the content column. Blank the dash and let ParseMapping walk it
                // (plus any sibling keys that follow at the same indent).
                _lines[_index] = new string(' ', content) + item;
                result.Add(ParseMapping(content));
                continue;
            }

            result.Add(ParseScalar(item, _index + 1));
            _index++;
        }

        return result;
    }

    /// <summary>Column where a sequence item's content starts, or -1 for an empty item.</summary>
    private static int ContentStart(string line)
    {
        var position = line.IndexOf('-') + 1;
        while (position < line.Length && line[position] == ' ')
        {
            position++;
        }

        return position >= line.Length ? -1 : position;
    }

    private JsonNode? ParseScalar(string text, int lineNumber)
    {
        var value = StripComment(text, lineNumber).Trim();
        if (value.Length == 0)
        {
            return null;
        }

        switch (value[0])
        {
            case '\'':
                return JsonValue.Create(SingleQuoted(value, lineNumber));
            case '"':
                return JsonValue.Create(DoubleQuoted(value, lineNumber));
            case '[':
                return ParseFlowSequence(value, lineNumber);
            case '{':
            case '|':
            case '>':
            case '&':
            case '*':
            case '!':
            case '%':
            case '@':
            case '`':
                throw new NotSupportedException(
                    $"line {lineNumber}: YAML construct '{value[0]}' is outside the reader's subset ('{value}')");
            default:
                break;
        }

        return value switch
        {
            "null" or "~" => null,
            "true" => JsonValue.Create(true),
            "false" => JsonValue.Create(false),
            _ when long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)
                => JsonValue.Create(integer),
            _ when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                => JsonValue.Create(number),
            _ => JsonValue.Create(value),
        };
    }

    private JsonArray ParseFlowSequence(string value, int lineNumber)
    {
        var close = ClosingBracket(value, lineNumber);
        if (StripComment(value[(close + 1)..], lineNumber).Trim().Length != 0)
        {
            throw new NotSupportedException($"line {lineNumber}: trailing text after a flow sequence: '{value}'");
        }

        var array = new JsonArray();
        var inner = value[1..close].Trim();
        if (inner.Length == 0)
        {
            return array;
        }

        foreach (var part in SplitTopLevel(inner, lineNumber))
        {
            array.Add(ParseScalar(part, lineNumber));
        }

        return array;
    }

    /// <summary>Index of the ']' matching the opening '[' of <paramref name="value"/>, skipping quoted text.</summary>
    private static int ClosingBracket(string value, int lineNumber)
    {
        var depth = 0;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c is '\'' or '"')
            {
                i = SkipQuoted(value, i, lineNumber);
                continue;
            }

            if (c == '[')
            {
                depth++;
            }
            else if (c == ']' && --depth == 0)
            {
                return i;
            }
        }

        throw new NotSupportedException($"line {lineNumber}: unterminated flow sequence '{value}'");
    }

    /// <summary>Index of the closing quote of the quoted scalar starting at <paramref name="start"/>.</summary>
    private static int SkipQuoted(string value, int start, int lineNumber)
    {
        var quote = value[start];
        for (var i = start + 1; i < value.Length; i++)
        {
            if (value[i] == '\\' && quote == '"')
            {
                i++;
                continue;
            }

            if (value[i] == quote)
            {
                if (quote == '\'' && i + 1 < value.Length && value[i + 1] == '\'')
                {
                    i++; // '' is an escaped quote inside a single-quoted scalar
                    continue;
                }

                return i;
            }
        }

        throw new NotSupportedException($"line {lineNumber}: unterminated quoted scalar in '{value}'");
    }

    /// <summary>Splits a flow sequence's inner text on top-level commas, respecting quotes and nesting.</summary>
    private static List<string> SplitTopLevel(string inner, int lineNumber)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < inner.Length; i++)
        {
            var c = inner[i];
            if (c is '\'' or '"')
            {
                i = SkipQuoted(inner, i, lineNumber);
                continue;
            }

            if (c is '[' or '{')
            {
                depth++;
            }
            else if (c is ']' or '}')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                parts.Add(inner[start..i]);
                start = i + 1;
            }
        }

        parts.Add(inner[start..]);
        return parts;
    }

    /// <summary>Removes a trailing " #"-comment; a '#' inside quotes is never a comment.</summary>
    private static string StripComment(string text, int lineNumber)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '\'' or '"')
            {
                i = SkipQuoted(text, i, lineNumber);
                continue;
            }

            if (c == '#' && (i == 0 || text[i - 1] == ' '))
            {
                return i == 0 ? string.Empty : text[..i];
            }
        }

        return text;
    }

    /// <summary>Splits a line into (key, inline-value-or-empty) at the mapping colon.</summary>
    private static (string Key, string Value) SplitEntry(string line, int lineNumber)
    {
        if (!TryFindKeyEnd(line, lineNumber, out var end))
        {
            throw new NotSupportedException($"line {lineNumber}: not a mapping entry: '{line}'");
        }

        var key = line[..end].Trim();
        if (key.Length == 0)
        {
            throw new NotSupportedException($"line {lineNumber}: empty key in '{line}'");
        }

        if (key[0] is '\'' or '"')
        {
            key = key[0] == '\'' ? SingleQuoted(key, lineNumber) : DoubleQuoted(key, lineNumber);
        }

        return (key, line[(end + 1)..].Trim());
    }

    /// <summary>True when the text starts with a mapping entry (key followed by ':' + space/EOL).</summary>
    private static bool TryFindKeyEnd(string line, int lineNumber, out int end)
    {
        end = -1;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c is '\'' or '"')
            {
                i = SkipQuoted(line, i, lineNumber);
                continue;
            }

            if (c == ':' && (i + 1 == line.Length || line[i + 1] == ' '))
            {
                end = i;
                return true;
            }
        }

        return false;
    }

    private static string SingleQuoted(string value, int lineNumber)
        => value.Length >= 2 && value[^1] == '\''
            ? value[1..^1].Replace("''", "'", StringComparison.Ordinal)
            : throw new NotSupportedException($"line {lineNumber}: unterminated single-quoted scalar '{value}'");

    private static string DoubleQuoted(string value, int lineNumber)
    {
        if (value.Length < 2 || value[^1] != '"')
        {
            throw new NotSupportedException($"line {lineNumber}: unterminated double-quoted scalar '{value}'");
        }

        var inner = value[1..^1];
        var builder = new StringBuilder(inner.Length);
        for (var i = 0; i < inner.Length; i++)
        {
            if (inner[i] != '\\')
            {
                builder.Append(inner[i]);
                continue;
            }

            if (++i >= inner.Length)
            {
                throw new NotSupportedException($"line {lineNumber}: dangling escape in '{value}'");
            }

            if (inner[i] == 'u')
            {
                if (i + 4 >= inner.Length)
                {
                    throw new NotSupportedException($"line {lineNumber}: truncated \\uXXXX escape in '{value}'");
                }

                builder.Append((char)ushort.Parse(inner.AsSpan(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                i += 4;
                continue;
            }

            builder.Append(inner[i] switch
            {
                'n' => '\n',
                't' => '\t',
                'r' => '\r',
                'b' => '\b',
                'f' => '\f',
                '"' => '"',
                '\\' => '\\',
                '/' => '/',
                '0' => '\0',
                _ => throw new NotSupportedException($"line {lineNumber}: unsupported escape '\\{inner[i]}' in '{value}'"),
            });
        }

        return builder.ToString();
    }
}
