using System.Text.Json;
using System.Text.RegularExpressions;
using Evidgate.BuildingBlocks.SharedKernel.Security;

namespace Evidgate.Modules.Catalog.Domain;

/// <summary>
/// Checks rendered template files against a template's <c>conformance_rules</c>. Every rule names a file glob and
/// one kind of requirement; a rule that cannot be read, a kind it does not know, a pattern that does not compile, a
/// rule whose glob matches no file and a file over the cap all count as violated, never as passed.
/// </summary>
/// <remarks>
/// The kinds are <c>evidgate_annotation</c> (exactly one valid <c>evidgate.id</c>), <c>requires_pattern</c> (every
/// pattern matches) and <c>forbids_secret_literal</c> (no string literal is secret-shaped). The annotation kind
/// delegates to <see cref="AnnotationParser"/> and the secret kind to <see cref="SecretShape"/>, so the check is the
/// same one the ingest and variant paths use.
/// </remarks>
public static class TemplateConformance
{
    /// <summary>The id reported when the rules document itself is unusable.</summary>
    internal const string InvalidRulesId = "invalid-conformance-rules";

    /// <summary>Largest rules document parsed.</summary>
    internal const int MaxRulesChars = 65536;

    /// <summary>Largest rendered file a rule scans.</summary>
    internal const int MaxFileChars = 65536;

    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(250);

    private static readonly string[] Kinds = ["evidgate_annotation", "requires_pattern", "forbids_secret_literal"];

    /// <summary>Evaluates every rule against the rendered files.</summary>
    /// <param name="rulesJson">The <c>conformance_rules</c> document.</param>
    /// <param name="renderedFiles">Rendered paths and their text.</param>
    /// <returns>The ids of the violated rules, in document order.</returns>
    public static IReadOnlyList<string> Evaluate(string rulesJson, IReadOnlyDictionary<string, string> renderedFiles)
    {
        ArgumentNullException.ThrowIfNull(rulesJson);
        ArgumentNullException.ThrowIfNull(renderedFiles);

        var rules = ParseRules(rulesJson);
        if (rules is null)
        {
            return [InvalidRulesId];
        }

        var violated = new List<string>();
        foreach (var rule in rules)
        {
            if (rule.Problem is not null || !Passes(rule, renderedFiles))
            {
                violated.Add(rule.Id);
            }
        }

        return violated;
    }

    private static bool Passes(Rule rule, IReadOnlyDictionary<string, string> files)
    {
        var glob = rule.Glob;
        if (glob is null)
        {
            return false;
        }

        var matched = false;
        foreach (var file in files)
        {
            if (!glob.IsMatch(file.Key))
            {
                continue;
            }

            matched = true;
            if (file.Value.Length > MaxFileChars)
            {
                return false;
            }

            try
            {
                if (!PassesFile(rule, file.Value))
                {
                    return false;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }

        return matched;
    }

    private static bool PassesFile(Rule rule, string content) => rule.Kind switch
    {
        "evidgate_annotation" => AnnotationParser.Parse(content).Outcome == AnnotationOutcome.Valid,
        "requires_pattern" => rule.Patterns!.All(pattern => pattern.IsMatch(content)),
        _ => HasNoSecretLiteral(content),
    };

    private static bool HasNoSecretLiteral(string content)
    {
        var scan = TemplateLiteralScanner.Scan(content);
        if (!scan.Complete)
        {
            return false;
        }

        foreach (var literal in scan.Literals)
        {
            if (literal.Value.Length == 0)
            {
                continue;
            }

            if (SecretShape.ValueLooksSecret(literal.Value))
            {
                return false;
            }

            if (SecretShape.IsReference(literal.Value.AsSpan()))
            {
                continue;
            }

            if (FollowsSecretKey(content, literal.Offset))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>True when a literal at <paramref name="offset"/> follows <c>key: '…'</c> or <c>key = '…'</c> and the key names a secret.</summary>
    private static bool FollowsSecretKey(string content, int offset)
    {
        var index = offset - 1;
        while (index >= 0 && char.IsWhiteSpace(content[index]))
        {
            index--;
        }

        if (index < 0 || (content[index] != ':' && content[index] != '='))
        {
            return false;
        }

        if (content[index] == '=' && index > 0 && content[index - 1] is '=' or '!' or '<' or '>')
        {
            return false;
        }

        index--;
        while (index >= 0 && char.IsWhiteSpace(content[index]))
        {
            index--;
        }

        var end = index + 1;
        while (index >= 0 && (char.IsLetterOrDigit(content[index]) || content[index] is '_' or '$'))
        {
            index--;
        }

        return end > index + 1 && SecretShape.KeyLooksSecret(content[(index + 1)..end]);
    }

    private static List<Rule>? ParseRules(string rulesJson)
    {
        if (rulesJson.Length == 0 || rulesJson.Length > MaxRulesChars)
        {
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(rulesJson);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
            {
                return null;
            }

            var rules = new List<Rule>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                var rule = ReadRule(element);
                if (rule is null)
                {
                    return null;
                }

                rules.Add(rule);
            }

            return rules;
        }
    }

    private static Rule? ReadRule(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("id", out var idElement)
            || idElement.ValueKind != JsonValueKind.String
            || idElement.GetString() is not { Length: > 0 and <= 64 } id)
        {
            return null;
        }

        if (!element.TryGetProperty("kind", out var kindElement)
            || kindElement.ValueKind != JsonValueKind.String
            || kindElement.GetString() is not { } kind
            || !Kinds.Contains(kind, StringComparer.Ordinal))
        {
            return new Rule(id, string.Empty, null, null, "unknown kind");
        }

        if (!element.TryGetProperty("file", out var fileElement)
            || fileElement.ValueKind != JsonValueKind.String
            || fileElement.GetString() is not { Length: > 0 and <= 256 } glob)
        {
            return new Rule(id, kind, null, null, "missing glob");
        }

        Regex globPattern;
        try
        {
            globPattern = GlobPattern(glob);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return new Rule(id, kind, null, null, "invalid glob");
        }

        if (kind != "requires_pattern")
        {
            return new Rule(id, kind, globPattern, null, null);
        }

        var patterns = ReadPatterns(element);
        return patterns is null
            ? new Rule(id, kind, globPattern, null, "invalid pattern")
            : new Rule(id, kind, globPattern, patterns, null);
    }

    private static List<Regex>? ReadPatterns(JsonElement element)
    {
        if (!element.TryGetProperty("pattern", out var patternElement))
        {
            return null;
        }

        var texts = new List<string>();
        if (patternElement.ValueKind == JsonValueKind.String)
        {
            texts.Add(patternElement.GetString()!);
        }
        else if (patternElement.ValueKind == JsonValueKind.Array && patternElement.GetArrayLength() is > 0 and <= 8)
        {
            foreach (var item in patternElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || item.GetString() is not { Length: > 0 and <= 256 } text)
                {
                    return null;
                }

                texts.Add(text);
            }
        }
        else
        {
            return null;
        }

        var patterns = new List<Regex>(texts.Count);
        foreach (var text in texts)
        {
            try
            {
                patterns.Add(new Regex(text, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, Timeout));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                return null;
            }
        }

        return patterns;
    }

    /// <summary>Anchored glob: <c>**/</c> is any directory prefix, <c>**</c> any text and <c>*</c> within one segment.</summary>
    private static Regex GlobPattern(string glob)
    {
        var builder = new System.Text.StringBuilder(@"\A");
        for (var index = 0; index < glob.Length; index++)
        {
            var character = glob[index];
            if (character == '*' && index + 1 < glob.Length && glob[index + 1] == '*')
            {
                index++;
                if (index + 1 < glob.Length && glob[index + 1] == '/')
                {
                    index++;
                    builder.Append("(?:.*/)?");
                }
                else
                {
                    builder.Append(".*");
                }

                continue;
            }

            builder.Append(character switch
            {
                '*' => "[^/]*",
                '?' => "[^/]",
                _ => Regex.Escape(character.ToString()),
            });
        }

        builder.Append(@"\z");
        return new Regex(builder.ToString(), RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, Timeout);
    }

    private sealed record Rule(string Id, string Kind, Regex? Glob, IReadOnlyList<Regex>? Patterns, string? Problem);
}
