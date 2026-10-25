namespace Architecture.Tests.Rules;

/// <summary>
/// 06.3.1-T1.1 / plan §11: every parse of untrusted artifact bytes goes through the one enforced profile, so the
/// construction of an XML or archive reader is a build-time fact rather than a review habit. The rule reads
/// source text (the same way the secret-literal rule does), because a reader is constructed in a method body,
/// where reflection cannot see it.
/// <para>
/// Two scopes, because the two families have different reach: the untrusted XML and archive readers
/// (<c>XmlReader</c>, <c>XmlDictionaryReader</c>, <c>XDocument</c>, <c>XElement</c>, <c>XPathDocument</c>,
/// <c>XmlSerializer</c>, <c>ZipArchive</c>, <c>TarReader</c>, and the profile's own report handle) parse
/// nothing in this repository today, so the rule forbids them everywhere outside the profile;
/// <c>JsonDocument.Parse</c> (and the other <c>JsonDocument</c>/<c>JsonNode</c>/<c>JsonSerializer</c>/
/// <c>Utf8JsonReader</c> entry points) is also the normal way to read a request body or a stored row
/// (Policy, Orchestrator, Catalog, Contracts.Validation all do it), so the rule scopes it to the module
/// that owns artifact bytes.
/// </para>
/// </summary>
public static class ParserConstructionRules
{
    /// <summary>The one directory allowed to construct a reader for untrusted artifact bytes.</summary>
    public const string ProfileDirectory = "src/Modules/Evidgate.Modules.Evidence/Infrastructure/Parsing/";

    /// <summary>The module that owns artifacts: inside it, the JSON reader is the profile's too.</summary>
    public const string ArtifactModuleDirectory = "src/Modules/Evidgate.Modules.Evidence/";

    /// <summary>Constructs forbidden everywhere outside the profile: the untrusted XML and archive readers.</summary>
    public static readonly IReadOnlyList<string> RepositoryWideConstructs =
    [
        "XmlReader.Create(",
        "XmlReaderSettings",
        "XmlDictionaryReader.Create(",
        "XDocument.Load(",
        "XDocument.Parse(",
        "XElement.Load(",
        "XElement.Parse(",
        "XPathDocument",
        "XmlSerializer",
        "new XmlDocument(",
        "new XmlTextReader(",
        "new ZipArchive(",
        "ZipFile.Open(",
        "ZipFile.OpenRead(",
        "ZipFile.ExtractToDirectory(",
        "TarReader",
        "new ParsedXmlReport(",
    ];

    /// <summary>Constructs forbidden outside the profile inside the artifact module: the JSON reader.</summary>
    public static readonly IReadOnlyList<string> ArtifactModuleConstructs =
    [
        "JsonDocument.Parse(",
        "JsonDocument.ParseAsync(",
        "JsonDocument.TryParseValue(",
        "JsonNode.Parse(",
        "JsonObject.Parse(",
        "JsonArray.Parse(",
        "JsonSerializer.Deserialize",
        "new Utf8JsonReader(",
    ];

    /// <summary>
    /// The write direction of an archive is not a parse: <c>new ZipArchive(output, ZipArchiveMode.Create)</c> builds an
    /// export (13.1.1-T5 XLSX writer) and reads no untrusted bytes, so a construction whose statement names
    /// <c>ZipArchiveMode.Create</c> is not a violation. Every other construct, and every read/update-mode archive,
    /// still is.
    /// </summary>
    private static bool ContainsParse(string text, string construct)
    {
        var from = 0;
        while (true)
        {
            var at = text.IndexOf(construct, from, StringComparison.Ordinal);
            if (at < 0)
            {
                return false;
            }

            if (!string.Equals(construct, "new ZipArchive(", StringComparison.Ordinal))
            {
                return true;
            }

            var end = text.IndexOf(';', at);
            var statement = end < 0 ? text[at..] : text[at..end];
            if (!statement.Contains("ZipArchiveMode.Create", StringComparison.Ordinal))
            {
                return true;
            }

            from = at + construct.Length;
        }
    }

    /// <summary>
    /// One violation per construct found outside the profile, naming the file and the construct. Paths are
    /// repository-relative with '/'.
    /// </summary>
    /// <param name="files">The source files to scan (path + text).</param>
    /// <param name="profileDirectory">The directory allowed to construct a reader.</param>
    /// <param name="artifactModuleDirectory">The directory whose JSON reads must use the profile.</param>
    /// <returns>The violations, ordered by path.</returns>
    public static IReadOnlyList<string> FindViolations(
        IEnumerable<(string Path, string Text)> files,
        string profileDirectory,
        string artifactModuleDirectory)
    {
        var violations = new List<string>();

        foreach (var (path, text) in files.OrderBy(file => file.Path, StringComparer.Ordinal))
        {
            var inProfile = path.StartsWith(profileDirectory, StringComparison.Ordinal);
            if (inProfile)
            {
                continue;
            }

            foreach (var construct in RepositoryWideConstructs)
            {
                if (ContainsParse(text, construct))
                {
                    violations.Add(
                        $"{path} constructs {construct}: the untrusted-parser profile in {profileDirectory} is the "
                        + "only place an XML or archive reader may be constructed (06.3.1 §11, 00 §5.7 S4/S5)");
                }
            }

            if (!path.StartsWith(artifactModuleDirectory, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var construct in ArtifactModuleConstructs)
            {
                if (text.Contains(construct, StringComparison.Ordinal))
                {
                    violations.Add(
                        $"{path} constructs {construct}: inside the artifact module, JSON bytes are parsed by the "
                        + $"profile in {profileDirectory} (06.3.1 §11, 00 §5.7 S5)");
                }
            }
        }

        return violations;
    }
}
