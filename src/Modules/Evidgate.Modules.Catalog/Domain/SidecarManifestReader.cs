using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Evidgate.BuildingBlocks.Contracts.Validation;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Evidgate.Modules.Catalog.Domain;

/// <summary>One mapping of a sidecar: a test file and the id it holds.</summary>
/// <param name="FullPath">The repository path of the described file.</param>
/// <param name="Id">The <c>evidgate.id</c>.</param>
/// <param name="Name">The optional display name.</param>
/// <param name="RawKey">The entry as written (<c>file</c>), for the identity trail.</param>
/// <param name="RequirementRef">The optional external requirement, trimmed, or null.</param>
internal sealed record SidecarEntry(string FullPath, Guid Id, string? Name, string RawKey, string? RequirementRef);

/// <summary>A problem found while reading a sidecar.</summary>
/// <param name="Path">The offending path: the described file for an entry, the manifest itself otherwise.</param>
/// <param name="Message">Why.</param>
internal sealed record SidecarProblem(string Path, string Message);

/// <summary>The read of one <c>evidgate.cases.yaml</c>.</summary>
/// <param name="Entries">The usable entries.</param>
/// <param name="Problems">What was refused; each one also holds the sidecar's directory.</param>
/// <param name="Warnings">Entry-level metadata that was dropped; the entry itself is still used and its directory is not held.</param>
internal sealed record SidecarReadResult(
    IReadOnlyList<SidecarEntry> Entries,
    IReadOnlyList<SidecarProblem> Problems,
    IReadOnlyList<SidecarProblem> Warnings);

/// <summary>
/// Reads a per-directory <c>evidgate.cases.yaml</c> (03.1.1-T1.3, TL T55). The file is untrusted input from a
/// customer repository, so it is parsed as <em>data</em>: the YAML event stream is turned into JSON by hand and
/// anchors, aliases, tags and duplicate keys are refused (no alias expansion, no type construction, no
/// billion-laughs), depth and size are capped, and the result is validated against
/// <c>contracts/catalog/sidecar.v1.json</c> before any entry is used. A document that is not valid contributes
/// no entry at all; a valid one contributes one entry per mapping and one problem per entry that names an
/// unusable path.
/// </summary>
internal sealed class SidecarManifestReader(Lazy<ContractSchemaSet> schemaSet)
{
    /// <summary>The file name that marks a sidecar.</summary>
    internal const string FileName = "evidgate.cases.yaml";

    /// <summary>The schema key of <c>contracts/catalog/sidecar.v1.json</c>.</summary>
    internal const string SchemaKey = "sidecar.v1";

    private const int MaxChars = 1024 * 1024;
    private const int MaxDepth = 8;
    private const int MaxNodes = 40_000;
    private const int MaxScalarChars = 4096;

    /// <summary>Reads the sidecar at <paramref name="manifestPath"/>.</summary>
    /// <param name="manifestPath">Repository path of the sidecar; its directory anchors the <c>file</c> values.</param>
    /// <param name="yaml">The text.</param>
    public SidecarReadResult Read(string manifestPath, string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        if (yaml.Length > MaxChars)
        {
            return Refused(manifestPath, "the sidecar is larger than 1 MiB");
        }

        JsonNode? document;
        try
        {
            document = ToJson(yaml);
        }
        catch (Exception exception) when (exception is YamlException or SidecarFormatException)
        {
            return Refused(manifestPath, "the sidecar is not plain YAML data: " + Brief(exception.Message));
        }

        if (document is not JsonObject)
        {
            return Refused(manifestPath, "the sidecar must be a mapping with `version` and `cases`");
        }

        using var parsed = JsonDocument.Parse(document.ToJsonString());
        var verdict = schemaSet.Value.Validate(SchemaKey, parsed.RootElement);
        if (!verdict.IsValid)
        {
            var first = verdict.Errors[0];

            return Refused(manifestPath, $"the sidecar breaks sidecar.v1 at '{first.Path}' ({first.Reason})");
        }

        var directory = RepoPath.DirectoryOf(manifestPath);
        var entries = new List<SidecarEntry>();
        var problems = new List<SidecarProblem>();
        var warnings = new List<SidecarProblem>();

        foreach (var node in document["cases"]!.AsArray())
        {
            var item = node!.AsObject();
            var file = item["file"]!.GetValue<string>();
            var id = item["evidgate.id"]!.GetValue<string>();
            var full = RepoPath.Combine(directory, file);

            if (full is null)
            {
                problems.Add(new SidecarProblem(manifestPath, $"entry '{Brief(file)}' is not a path inside the manifest's directory"));
                continue;
            }

            if (!AnnotationParser.IsCanonicalUuid(id, out var guid))
            {
                problems.Add(new SidecarProblem(full, "the evidgate.id of this entry is not a UUID"));
                continue;
            }

            var name = item["name"]?.GetValue<string>();
            if (name is not null && !TextRules.IsClean(name))
            {
                problems.Add(new SidecarProblem(full, "the name of this entry has control, format or malformed Unicode characters"));
                continue;
            }

            // The pointer is metadata: an unusable value drops that one value and warns, while the entry still
            // identifies its case, so a typo can never freeze the directory's cases or change an id.
            var requirement = item["requirement"]?.GetValue<string>();
            if (requirement is not null && !Taxonomy.TryRequirementRef(requirement, out requirement))
            {
                warnings.Add(new SidecarProblem(
                    full,
                    "the requirement of this entry is not a trimmed 1..200 character value free of control characters"));
            }

            entries.Add(new SidecarEntry(full, guid, name, file, requirement));
        }

        return new SidecarReadResult(entries, problems, warnings);
    }

    private static SidecarReadResult Refused(string manifestPath, string message) =>
        new([], [new SidecarProblem(manifestPath, message)], []);

    private static string Brief(string text) => TextRules.Brief(text);

    private static JsonNode? ToJson(string yaml)
    {
        var parser = new Parser(new StringReader(yaml));
        var state = new BuildState();

        parser.Consume<StreamStart>();
        if (parser.Accept<StreamEnd>(out _))
        {
            return null;
        }

        parser.Consume<DocumentStart>();
        var root = Node(parser, state, 0);
        parser.Consume<DocumentEnd>();

        if (!parser.Accept<StreamEnd>(out _))
        {
            throw new SidecarFormatException("more than one YAML document");
        }

        return root;
    }

    private static JsonNode? Node(Parser parser, BuildState state, int depth)
    {
        if (depth > MaxDepth || ++state.Nodes > MaxNodes)
        {
            throw new SidecarFormatException("the document is too deeply nested or too large");
        }

        var current = parser.Current ?? throw new SidecarFormatException("unexpected end of document");

        switch (current)
        {
            case AnchorAlias:
                throw new SidecarFormatException("aliases are not allowed");
            case Scalar scalar:
                RefuseAnchorAndTag(scalar.Anchor, scalar.Tag);
                parser.MoveNext();
                return ScalarValue(scalar);
            case SequenceStart sequence:
                {
                    RefuseAnchorAndTag(sequence.Anchor, sequence.Tag);
                    parser.MoveNext();
                    var array = new JsonArray();
                    while (!parser.Accept<SequenceEnd>(out _))
                    {
                        array.Add(Node(parser, state, depth + 1));
                    }

                    parser.Consume<SequenceEnd>();
                    return array;
                }

            case MappingStart mapping:
                {
                    RefuseAnchorAndTag(mapping.Anchor, mapping.Tag);
                    parser.MoveNext();
                    var obj = new JsonObject();
                    while (!parser.Accept<MappingEnd>(out _))
                    {
                        if (parser.Current is not Scalar key)
                        {
                            throw new SidecarFormatException("mapping keys must be plain text");
                        }

                        RefuseAnchorAndTag(key.Anchor, key.Tag);
                        parser.MoveNext();
                        if (obj.ContainsKey(key.Value))
                        {
                            throw new SidecarFormatException("duplicate key '" + Brief(key.Value) + "'");
                        }

                        obj[key.Value] = Node(parser, state, depth + 1);
                    }

                    parser.Consume<MappingEnd>();
                    return obj;
                }

            default:
                throw new SidecarFormatException("unsupported YAML construct");
        }
    }

    private static void RefuseAnchorAndTag(AnchorName anchor, TagName tag)
    {
        if (!anchor.IsEmpty)
        {
            throw new SidecarFormatException("anchors are not allowed");
        }

        if (!tag.IsEmpty)
        {
            throw new SidecarFormatException("tags are not allowed");
        }
    }

    private static JsonValue? ScalarValue(Scalar scalar)
    {
        if (scalar.Value.Length > MaxScalarChars)
        {
            throw new SidecarFormatException("a value is longer than 4096 characters");
        }

        // A quoted scalar is always text; a plain one is read the way JSON would: null, a bool, an integer, else text.
        if (scalar.Style != ScalarStyle.Plain)
        {
            return JsonValue.Create(scalar.Value);
        }

        switch (scalar.Value)
        {
            case "" or "~" or "null" or "Null" or "NULL":
                return null;
            case "true" or "True" or "TRUE":
                return JsonValue.Create(true);
            case "false" or "False" or "FALSE":
                return JsonValue.Create(false);
        }

        return scalar.Value.Length <= 9
            && scalar.Value.All(char.IsAsciiDigit)
            && int.TryParse(scalar.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? JsonValue.Create(number)
            : JsonValue.Create(scalar.Value);
    }

    private sealed class BuildState
    {
        public int Nodes { get; set; }
    }

    private sealed class SidecarFormatException(string message) : Exception(message);
}
