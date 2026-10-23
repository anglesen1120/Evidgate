using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Evidgate.Testing;

/// <summary>
/// The <c>meta.json</c> contract of 01.6.1-T3.1: exactly the four fields of
/// 01.6.1 §4, all non-empty strings. An extra or missing key is an unknown shape
/// and refused by <see cref="FixtureLoader"/>.
/// </summary>
public sealed record FixtureMeta(
    string Domain,
    string Kind,
    string Source,
    string AddedBy);

/// <summary>
/// A loaded fixture directory (<c>fixtures/&lt;domain&gt;/&lt;name&gt;/</c>, where
/// <c>&lt;name&gt;</c> may nest, e.g. <c>reports/junit/sample</c>).
/// <see cref="Expected"/> is null when the fixture carries no
/// <c>expected.json</c> (or when that file is the JSON literal <c>null</c>);
/// <see cref="ExpectedText"/> distinguishes the absent-file case.
/// </summary>
public sealed record Fixture(
    string Domain,
    string Name,
    string DirectoryPath,
    string InputPath,
    string InputText,
    string? ExpectedText,
    JsonNode? Expected,
    FixtureMeta Meta)
{
    /// <summary>Shortcut for <see cref="FixtureMeta.Kind"/> — the producer/format the fixture pins.</summary>
    public string Kind => Meta.Kind;
}

/// <summary>
/// A fixture directory does not satisfy the 01.6.1-T3.1 shape contract. The
/// refusal is deliberate, not a parse accident: unknown shapes are rejected so a
/// malformed corpus entry can never be silently skipped by a test run.
/// </summary>
public sealed class FixtureShapeException : Exception
{
    public FixtureShapeException(string fixtureDirectory, string problem)
        : base(fixtureDirectory + ": " + problem)
    {
        FixtureDirectory = fixtureDirectory;
    }

    public FixtureShapeException(string fixtureDirectory, string problem, Exception innerException)
        : base(fixtureDirectory + ": " + problem, innerException)
    {
        FixtureDirectory = fixtureDirectory;
    }

    /// <summary>The offending fixture directory (or corpus root, for a missing root).</summary>
    public string FixtureDirectory { get; }
}

/// <summary>
/// The .NET half of the shared fixture corpus (01.6.1-T3.1, AC6): linked shared
/// source an adopting test project picks up with a <c>Compile</c> link, the same
/// way it picks up <see cref="ContainerImages"/>.
/// <para>
/// Shape contract (<c>fixtures/README.md</c>): a fixture directory holds exactly
/// one <c>input.&lt;ext&gt;</c>, a required <c>meta.json</c> with exactly the
/// fields <c>domain</c>, <c>kind</c>, <c>source</c>, <c>added_by</c> (all
/// non-empty strings), an optional <c>expected.json</c>, and an optional
/// <c>README.md</c>; every other entry is refused with
/// <see cref="FixtureShapeException"/>. Every corpus file — the
/// <c>input.&lt;ext&gt;</c> payload included — must be UTF-8 without BOM.
/// </para>
/// <para>
/// <c>expected.json</c> is required by the corpus contract to be the RFC 8785
/// canonical byte sequence of its content. This loader validates that it
/// parses as JSON; byte-exact canonical enforcement lives in
/// <c>packages/test-fixtures/src/goldenFile.ts</c>, because no JCS
/// implementation is linked into test projects — that asymmetry is
/// documented in <c>fixtures/README.md</c>.
/// </para>
/// </summary>
public static class FixtureLoader
{
    /// <summary>The repository root, located by walking up for <c>Evidgate.slnx</c>.</summary>
    public static readonly string RepoRoot = LocateRepoRoot();

    /// <summary>The repository <c>fixtures/</c> directory.</summary>
    public static readonly string FixturesRoot = Path.Combine(RepoRoot, "fixtures");

    private const string MetaFileName = "meta.json";
    private const string ExpectedFileName = "expected.json";
    private const string ReadmeFileName = "README.md";
    private const string InputPrefix = "input.";

    private static readonly string[] MetaFields = ["domain", "kind", "source", "added_by"];
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>A file under the repository's <c>fixtures/</c> directory.</summary>
    public static string Fixtures(string relativePath) =>
        Path.Combine(FixturesRoot, relativePath);

    /// <summary>
    /// Loads one fixture directory. Relative paths resolve against the current
    /// working directory; use <see cref="Fixtures"/> for corpus paths.
    /// </summary>
    /// <exception cref="FixtureShapeException">the path is empty (<c>&lt;empty&gt;</c>), the directory is missing, or it does not satisfy the shape contract.</exception>
    public static Fixture Load(string fixtureDirectory)
    {
        if (string.IsNullOrWhiteSpace(fixtureDirectory))
        {
            // '<empty>' matches the TS loader's refusal path for the same check,
            // so exception.Message reads identically on both runtimes.
            throw new FixtureShapeException("<empty>", "fixture directory is empty");
        }

        var directory = TrimTrailingSeparator(Path.GetFullPath(fixtureDirectory));
        if (!Directory.Exists(directory))
        {
            throw new FixtureShapeException(directory, "fixture directory does not exist");
        }

        string? metaPath = null;
        string? expectedPath = null;
        var inputs = new List<string>(1);

        foreach (var entry in Directory.GetFileSystemEntries(directory))
        {
            var entryName = Path.GetFileName(entry)!;
            if (Directory.Exists(entry))
            {
                throw new FixtureShapeException(
                    directory,
                    $"unexpected subdirectory '{entryName}'; a fixture holds only input.*, meta.json, optional expected.json and optional README.md");
            }

            if (entryName.StartsWith(InputPrefix, StringComparison.Ordinal))
            {
                inputs.Add(entry);
                continue;
            }

            switch (entryName)
            {
                case MetaFileName:
                    metaPath = entry;
                    break;
                case ExpectedFileName:
                    expectedPath = entry;
                    break;
                case ReadmeFileName:
                    // Optional provenance note; the loader does not parse it.
                    break;
                default:
                    throw new FixtureShapeException(
                        directory,
                        $"unexpected file '{entryName}'; a fixture holds only input.*, meta.json, optional expected.json and optional README.md");
            }
        }

        if (metaPath is null)
        {
            throw new FixtureShapeException(directory, "meta.json is missing");
        }

        if (inputs.Count == 0)
        {
            throw new FixtureShapeException(directory, "an input.<ext> file is missing");
        }

        if (inputs.Count > 1)
        {
            throw new FixtureShapeException(
                directory,
                $"more than one input.* file: {string.Join(", ", inputs.Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal))}");
        }

        var inputPath = inputs[0];
        var inputName = Path.GetFileName(inputPath)!;
        if (inputName.Length == InputPrefix.Length)
        {
            throw new FixtureShapeException(directory, "the input file has no extension (it must be named input.<ext>)");
        }

        var meta = ParseMeta(directory, metaPath);
        var inputText = ReadInputText(inputPath, directory, inputName);

        string? expectedText = null;
        JsonNode? expected = null;
        if (expectedPath is not null)
        {
            expectedText = ReadUtf8Json(expectedPath, directory, ExpectedFileName);
            try
            {
                // Materialize forces every JsonObject dictionary now, so a
                // duplicate member name — which System.Text.Json surfaces as
                // ArgumentException on first access rather than at parse time
                // (JSON.parse alone is last-wins, which is why loadFixture.ts
                // scans the raw text to catch the same duplicate) — is refused
                // here instead of leaking later when a test reads the node.
                expected = JsonNode.Parse(expectedText);
                if (expected is not null)
                {
                    Materialize(expected);
                }
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException)
            {
                throw new FixtureShapeException(directory, "expected.json is not valid JSON: " + exception.Message, exception);
            }
        }

        return new Fixture(
            meta.Domain,
            Path.GetFileName(directory),
            directory,
            inputPath,
            inputText,
            expectedText,
            expected,
            meta);
    }

    /// <summary>
    /// <see cref="Load"/> without throwing: false plus the refusal text when the
    /// directory is not a well-formed fixture.
    /// </summary>
    public static bool TryLoad(string fixtureDirectory, out Fixture? fixture, out string? problem)
    {
        try
        {
            // Load refuses an empty path itself (as '<empty>: fixture directory
            // is empty'), so there is no separate null guard here.
            fixture = Load(fixtureDirectory);
            problem = null;
            return true;
        }
        catch (FixtureShapeException exception)
        {
            fixture = null;
            problem = exception.Message;
            return false;
        }
    }

    /// <summary>
    /// Loads every fixture under a corpus root. A directory is a fixture when it
    /// contains <c>meta.json</c> or an <c>input.*</c> entry; anything else
    /// (<c>rfc8785/input</c>-style byte-exact vector directories, README files) is
    /// skipped, so non-fixture sibling families never break the walk while a
    /// malformed fixture — detected as a candidate — always fails loudly. Entries
    /// are returned sorted by corpus-relative path, ordinal.
    /// </summary>
    /// <exception cref="FixtureShapeException">the path is empty (<c>&lt;empty&gt;</c>), the root is missing, or a fixture candidate violates the shape contract.</exception>
    public static IReadOnlyList<Fixture> LoadAll(string fixturesRoot)
    {
        if (string.IsNullOrWhiteSpace(fixturesRoot))
        {
            throw new FixtureShapeException("<empty>", "fixtures root is empty");
        }

        var root = TrimTrailingSeparator(Path.GetFullPath(fixturesRoot));
        if (!Directory.Exists(root))
        {
            throw new FixtureShapeException(root, "fixtures root does not exist");
        }

        var candidates = new List<string>();
        var pending = new Stack<string>(Directory.GetDirectories(root));
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            var entries = Directory.GetFileSystemEntries(directory);
            var isFixtureCandidate = entries.Any(entry =>
            {
                var name = Path.GetFileName(entry);
                return name == MetaFileName ||
                    (name is not null && name.StartsWith(InputPrefix, StringComparison.Ordinal));
            });
            if (isFixtureCandidate)
            {
                candidates.Add(directory);
                continue;
            }

            foreach (var subdirectory in entries.Where(Directory.Exists))
            {
                pending.Push(subdirectory);
            }
        }

        return candidates
            .OrderBy(candidate => Path.GetRelativePath(root, candidate).Replace('\\', '/'), StringComparer.Ordinal)
            .Select(Load)
            .ToList();
    }

    private static FixtureMeta ParseMeta(string directory, string metaPath)
    {
        var text = ReadUtf8Json(metaPath, directory, MetaFileName);
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text);
            if (node is not null)
            {
                // See Load: a duplicate member name is materialized as
                // ArgumentException by the Materialize walk, not by Parse.
                Materialize(node);
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException)
        {
            throw new FixtureShapeException(directory, "meta.json is not valid JSON: " + exception.Message, exception);
        }

        if (node is not JsonObject metaObject)
        {
            throw new FixtureShapeException(directory, "meta.json must be a JSON object");
        }

        // Key-set check on the member names only: a present-but-null field is a
        // value refusal ('must be a non-empty string'), not a missing key —
        // the same classification loadFixture.ts applies (AC6).
        if (metaObject.Count != MetaFields.Length || MetaFields.Any(field => !metaObject.ContainsKey(field)))
        {
            throw new FixtureShapeException(
                directory,
                $"meta.json must have exactly the fields {string.Join(", ", MetaFields)} (no missing, no extra)");
        }

        return new FixtureMeta(
            MetaValue(directory, metaObject, "domain"),
            MetaValue(directory, metaObject, "kind"),
            MetaValue(directory, metaObject, "source"),
            MetaValue(directory, metaObject, "added_by"));
    }

    private static string MetaValue(string directory, JsonObject metaObject, string field)
    {
        var node = metaObject[field];
        var text = node?.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : null;
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new FixtureShapeException(directory, $"meta.json field '{field}' must be a non-empty string");
        }

        return text;
    }

    /// <summary>
    /// Reads the payload of an <c>input.&lt;ext&gt;</c> file. The same UTF-8-BOM
    /// refusal the JSON files get applies here too (AC6):
    /// <c>File.ReadAllText</c> would silently strip it, while the TS loader's
    /// <c>readFileSync(…, 'utf8')</c> would keep the U+FEFF — so the check runs
    /// on the raw bytes, then the rest decodes leniently like before.
    /// </summary>
    private static string ReadInputText(string path, string directory, string name)
    {
        var bytes = File.ReadAllBytes(path);
        if (HasUtf8Bom(bytes))
        {
            throw new FixtureShapeException(directory, $"'{name}' must be UTF-8 without BOM");
        }

        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>Reads a JSON corpus file as strict UTF-8; a BOM is an unknown shape.</summary>
    private static string ReadUtf8Json(string path, string directory, string name)
    {
        var bytes = File.ReadAllBytes(path);
        if (HasUtf8Bom(bytes))
        {
            throw new FixtureShapeException(directory, $"'{name}' must be UTF-8 without BOM");
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new FixtureShapeException(directory, $"'{name}' is not valid UTF-8", exception);
        }
    }

    /// <summary>True when the bytes open with the UTF-8 BOM (EF BB BF).</summary>
    private static bool HasUtf8Bom(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;

    /// <summary>
    /// Forces every <see cref="JsonObject"/> dictionary in the parsed tree to
    /// materialize. <c>JsonNode.Parse</c> builds objects lazily, so a duplicate
    /// member name survives parsing and surfaces as <c>ArgumentException</c> on
    /// the first <c>Count</c>/indexer access — calling this inside the parse
    /// <c>try</c> keeps that refusal inside <see cref="FixtureShapeException"/>.
    /// </summary>
    private static void Materialize(JsonNode node)
    {
        switch (node)
        {
            case JsonObject jsonObject:
                // Count forces the lazy OrderedDictionary to materialize — that is
                // where a duplicate member name throws ArgumentException.
                _ = jsonObject.Count;
                foreach (var member in jsonObject.ToArray())
                {
                    // member.Value is null for a JSON null member.
                    if (member.Value is not null)
                    {
                        Materialize(member.Value);
                    }
                }

                break;
            case JsonArray jsonArray:
                foreach (var element in jsonArray.ToArray())
                {
                    if (element is not null)
                    {
                        Materialize(element);
                    }
                }

                break;
        }
    }

    private static string TrimTrailingSeparator(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

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

        throw new InvalidOperationException(
            $"Evidgate.slnx not found above '{AppContext.BaseDirectory}'");
    }
}
