// 06.3.2-T1.1 (AC1): the committed hostile corpus lives in fixtures/hostile/, one file per guard, with
// fixtures/hostile/manifest.json as the single machine-readable index (the corpus is flat, so no FixtureLoader
// meta.json: that layout demands exactly one input.* per directory, which a multi-file corpus cannot satisfy —
// the same exemption fixtures/ci-recipes, fixtures/contracts and fixtures/threat-model document in
// fixtures/README.md). Every entry names the guard it exercises, the entry point the corpus drives it through,
// and either the failure code it must be refused with or its documented outcome.
using System.Text.Json;
using Shouldly;

namespace Tenancy.IntegrationTests;

/// <summary>
/// One row of <c>fixtures/hostile/manifest.json</c> (06.3.2 §9, 06.3.2-T1.1 AC1).
/// <para>
/// <see cref="GuardId"/> is the first word of <see cref="Guard"/> and must be one of
/// <see cref="HostileFixtureCorpus.GuardIds"/> — the closed list of guards the corpus exercises, so a row can
/// never name a guard that does not exist. <see cref="DrivenThrough"/> is one of the four documented entry
/// points, and exactly one of <see cref="ExpectedFailureCode"/>/<see cref="ExpectedOutcome"/> is set.
/// </para>
/// </summary>
/// <param name="File">The file name inside <c>fixtures/hostile/</c>.</param>
/// <param name="Guard">The guard this file exercises, as <c>&lt;guard-id&gt; — &lt;sentence&gt;</c>.</param>
/// <param name="DrivenThrough">The entry point the corpus drives the file through.</param>
/// <param name="ExpectedFailureCode">The refusal the corpus must observe, or null for an accepted/referenced row.</param>
/// <param name="ExpectedOutcome">The documented non-refusal outcome (<c>accepted</c>/<c>referenced</c>), or null.</param>
/// <param name="DeclaredMediaType">The media type the complete-path rows declare; null for the parse-driven rows.</param>
/// <param name="Generated">True for a fixture built at test time by <see cref="GenerateBombFixture"/>/<see cref="GenerateOversizedFixture"/>.</param>
internal sealed record HostileFixture(
    string File,
    string Guard,
    string DrivenThrough,
    string? ExpectedFailureCode,
    string? ExpectedOutcome,
    string? DeclaredMediaType,
    bool Generated)
{
    /// <summary>The entry point of an XML report: <see cref="IParsing.ParseXmlReport"/>.</summary>
    internal const string ParseXmlMode = "IParsing.ParseXmlReport";

    /// <summary>The entry point of an archive: <see cref="IParsing.EnumerateArchive"/>.</summary>
    internal const string EnumerateMode = "IParsing.EnumerateArchive";

    /// <summary>The entry point of a hostile object at <c>complete</c>.</summary>
    internal const string CompleteMode = "ArtifactCompletionVerifier (complete path)";

    /// <summary>A file another story owns the assertion for (06.2.3's masking scanner reads it from this corpus).</summary>
    internal const string ReferencedMode = "06.2.3 scanner (referenced, not re-tested here)";

    /// <summary>The positive control's outcome: the profile must ACCEPT the file, so a rejection is a decision.</summary>
    internal const string AcceptedOutcome = "accepted";

    /// <summary>The outcome of a file whose assertion lives in another story's suite.</summary>
    internal const string ReferencedOutcome = "referenced";

    /// <summary>The guard id of the row: the first word of <see cref="Guard"/>.</summary>
    internal string GuardId => Guard.Split(' ', 2)[0];

    /// <summary>True when the row documents a refusal and carries the failure code the corpus must observe.</summary>
    internal bool ExpectsRejection => ExpectedFailureCode is not null;

    /// <summary>True for the positive control: the corpus asserts the profile ACCEPTS it.</summary>
    internal bool IsAccepted => string.Equals(ExpectedOutcome, AcceptedOutcome, StringComparison.Ordinal);

    /// <summary>True for a file whose assertion belongs to another story.</summary>
    internal bool IsReferenced => string.Equals(ExpectedOutcome, ReferencedOutcome, StringComparison.Ordinal);

    /// <summary>True when the row is driven through an <see cref="IParsing"/> member.</summary>
    internal bool IsParseDriven => DrivenThrough is ParseXmlMode or EnumerateMode;

    /// <summary>True when the row is driven through the completion's media sniff or size cap.</summary>
    internal bool IsCompleteDriven => string.Equals(DrivenThrough, CompleteMode, StringComparison.Ordinal);

    /// <summary>True when the row is driven through <see cref="IParsing.EnumerateArchive"/>.</summary>
    internal bool IsArchiveDriven => string.Equals(DrivenThrough, EnumerateMode, StringComparison.Ordinal);
}

/// <summary>
/// The 06.3.2 hostile-input corpus as code sees it: the directory, the manifest loaded from disk with a strict
/// parser (a row with an unknown key, a missing field, two outcomes or a duplicate file name is a failure of the
/// corpus, not a silently skipped case), the closed guard list, and the files actually present.
/// </summary>
internal static class HostileFixtureCorpus
{
    /// <summary>The corpus directory under <c>fixtures/</c>.</summary>
    internal const string DirectoryName = "hostile";

    /// <summary>The manifest every row is loaded from.</summary>
    internal const string ManifestFileName = "manifest.json";

    /// <summary>The layout note; not a fixture, so it is never a manifest row.</summary>
    internal const string ReadmeFileName = "README.md";

    /// <summary>
    /// The per-kind byte cap the corpus drives every parse with: the largest kind the profile parses (an 8 MiB
    /// object) is the worst case, and every committed file is a few KiB. The complete-path rows use the smaller
    /// <see cref="KindCapBytes"/> so the oversized fixture trips the cap.
    /// </summary>
    internal const int MaxBytes = 8 * 1024 * 1024;

    /// <summary>The kind cap the complete-path rows declare: below <see cref="GenerateOversizedFixture.DeclaredBytes"/>, above every media fixture.</summary>
    internal const int KindCapBytes = 4 * 1024;

    /// <summary>The corpus directory, absolute.</summary>
    internal static string DirectoryPath { get; } =
        Path.Combine(RlsExceptionFile.RepositoryRoot, "fixtures", DirectoryName);

    /// <summary>The manifest path, absolute.</summary>
    internal static string ManifestPath { get; } = Path.Combine(DirectoryPath, ManifestFileName);

    /// <summary>
    /// The guards the corpus exercises, as a closed list: a row names one of these, so a row can never name a
    /// guard that does not exist. Each id is the decision point the failure code comes from — the XML prologue
    /// scan / the reader profile, the archive's entry-name normalization / central-directory pre-check / entry
    /// count, the completion's media sniff / size cap — plus the scanner 06.2.3 owns.
    /// </summary>
    internal static readonly IReadOnlyList<string> GuardIds =
    [
        "xml-dtd",
        "xml-wellformed",
        "zip-entry-name",
        "zip-central-directory",
        "zip-entry-count",
        "complete-media-sniff",
        "complete-size-cap",
        "secret-scan",
    ];

    /// <summary>The four entry points a row may be driven through.</summary>
    internal static readonly IReadOnlyList<string> DriveModes =
    [
        HostileFixture.ParseXmlMode,
        HostileFixture.EnumerateMode,
        HostileFixture.CompleteMode,
        HostileFixture.ReferencedMode,
    ];

    private static readonly Lazy<IReadOnlyList<HostileFixture>> Manifest = new(LoadManifest);

    /// <summary>Every manifest row, in file order.</summary>
    internal static IReadOnlyList<HostileFixture> All => Manifest.Value;

    /// <summary>The committed fixture file names (everything in the directory but the manifest and the README).</summary>
    internal static IReadOnlyList<string> FilesOnDisk =>
    [
        .. Directory
            .GetFiles(DirectoryPath)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(name => name is not ManifestFileName and not ReadmeFileName)
            .OrderBy(name => name, StringComparer.Ordinal),
    ];

    /// <summary>The one manifest row for <paramref name="file"/>.</summary>
    /// <param name="file">The fixture file name.</param>
    internal static HostileFixture Entry(string file) =>
        All.SingleOrDefault(entry => string.Equals(entry.File, file, StringComparison.Ordinal))
        ?? throw new InvalidOperationException($"{ManifestFileName} has no row for {file}");

    /// <summary>The file's absolute path.</summary>
    /// <param name="entry">The row whose file is resolved.</param>
    internal static string PathOf(HostileFixture entry) => Path.Combine(DirectoryPath, entry.File);

    /// <summary>
    /// The strict manifest parse: one object per row, only the documented keys, <c>file</c>/<c>guard</c>/
    /// <c>driven_through</c> required and non-empty, exactly one of <c>expected_failure_code</c>/
    /// <c>expected_outcome</c>, and no duplicate file name — so the corpus cannot quietly lose a case.
    /// </summary>
    private static List<HostileFixture> LoadManifest()
    {
        var rows = new List<HostileFixture>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        using var document = JsonDocument.Parse(File.ReadAllText(ManifestPath));

        document.RootElement.ValueKind.ShouldBe(JsonValueKind.Array, $"{ManifestFileName} is a JSON array");
        document.RootElement.GetArrayLength().ShouldBeGreaterThan(0);

        foreach (var row in document.RootElement.EnumerateArray())
        {
            row.ValueKind.ShouldBe(JsonValueKind.Object, "every manifest row is an object");

            foreach (var member in row.EnumerateObject())
            {
                AllowedKeys.ShouldContain(
                    member.Name,
                    $"{ManifestFileName} has no unknown key; {member.Name} is not documented");
            }

            var file = Text(row, "file");
            var failureCode = OptionalText(row, "expected_failure_code");
            var outcome = OptionalText(row, "expected_outcome");

            (failureCode is null ^ outcome is null)
                .ShouldBeTrue($"{file} carries exactly one of expected_failure_code / expected_outcome");
            seen.Add(file).ShouldBeTrue($"{file} is listed once in {ManifestFileName}");

            rows.Add(new HostileFixture(
                file,
                Text(row, "guard"),
                Text(row, "driven_through"),
                failureCode,
                outcome,
                OptionalText(row, "declared_media_type"),
                Flag(row, "generated")));
        }

        return rows;
    }

    /// <summary>The keys a manifest row may carry.</summary>
    private static readonly IReadOnlyList<string> AllowedKeys =
    [
        "file",
        "guard",
        "driven_through",
        "expected_failure_code",
        "expected_outcome",
        "declared_media_type",
        "generated",
    ];

    private static string Text(JsonElement row, string name)
    {
        row.TryGetProperty(name, out var value).ShouldBeTrue($"a {ManifestFileName} row is missing {name}");
        value.ValueKind.ShouldBe(JsonValueKind.String, $"{name} is a JSON string");

        var text = value.GetString()!;
        text.ShouldNotBeNullOrWhiteSpace($"{name} is non-empty");

        return text;
    }

    private static string? OptionalText(JsonElement row, string name)
    {
        if (!row.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        value.ValueKind.ShouldBe(JsonValueKind.String, $"{name} is a JSON string when present");

        var text = value.GetString();
        text.ShouldNotBeNullOrWhiteSpace($"{name} is non-empty when present");

        return text;
    }

    private static bool Flag(JsonElement row, string name)
    {
        if (!row.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return false;
        }

        (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            .ShouldBeTrue($"{name} is a JSON boolean when present");

        return value.GetBoolean();
    }
}
