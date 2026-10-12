namespace Architecture.Tests.Rules;

/// <summary>
/// 06.3.1-T1.1 / plan §11: every parse of untrusted artifact bytes goes through the one enforced profile, so the
/// construction of an XML or archive reader is a build-time fact rather than a review habit. The rule reads
/// source text (the same way the secret-literal rule does), because a reader is constructed in a method body,
/// where reflection cannot see it.
/// <para>
/// Two scopes, because the two families have different reach: <c>XmlReader</c>/<c>XDocument</c>/<c>ZipArchive</c>
/// parse nothing in this repository today, so the rule forbids them everywhere outside the profile;
/// <c>JsonDocument.Parse</c> is also the normal way to read a request body or a stored row (Policy, Orchestrator,
/// Catalog, Contracts.Validation all do it), so the rule scopes it to the module that owns artifact bytes.
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
        "XDocument.Load(",
        "XDocument.Parse(",
        "new XmlDocument(",
        "new XmlTextReader(",
        "new ZipArchive(",
        "ZipArchive.Create(",
        "ZipFile.Open(",
        "ZipFile.OpenRead(",
    ];

    /// <summary>Constructs forbidden outside the profile inside the artifact module: the JSON reader.</summary>
    public static readonly IReadOnlyList<string> ArtifactModuleConstructs =
    [
        "JsonDocument.Parse(",
        "JsonNode.Parse(",
    ];

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
                if (text.Contains(construct, StringComparison.Ordinal))
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
