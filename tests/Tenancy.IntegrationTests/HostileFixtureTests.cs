// 06.3.2-T1.2 (AC2): every file in fixtures/hostile/ plus the two generated fixtures is driven through the entry
// point its manifest row names — IParsing.ParseXmlReport / IParsing.EnumerateArchive for the bytes (the profile
// itself, never a helper that reimplements a cap) and the completion's media sniff and size cap for a hostile
// OBJECT at `complete` — and is refused with the failure code the row documents. One row is the positive
// control: the long-comment archive is ACCEPTED, so a rejection is a decision the guard takes and not a blanket
// refusal of everything. No corpus file reaches `ready`: a parse answers a failure, and the complete-path verdict
// is a rejection, which the writer persists as a `rejected` row carrying that code and never a `ready` one
// (CompleteArtifactUpload.cs:190-194 — verified by the artifact-complete suites, whose own rows it asserts).
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using Evidgate.BuildingBlocks.ObjectStorage;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Evidence.Application;
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Evidence.Domain;
using Evidgate.Modules.Evidence.Infrastructure.Parsing;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 06.3.2-T1.2 (AC1, AC2, AC5; plan §9): the committed hostile corpus driven through the 06.3.1 profile. Every
/// manifest row is exercised — the XML families through <see cref="IParsing.ParseXmlReport"/>, the archives
/// through <see cref="IParsing.EnumerateArchive"/>, the hostile objects through the verification the
/// <c>complete</c> route runs — and each asserts the documented failure code, with the one positive control
/// asserted accepted. The manifest is the corpus's index: an entry without a file, a file without an entry, a
/// row naming an unknown guard or an unknown failure code, or a second copy of a hostile document elsewhere in
/// the repository all fail here.
/// </summary>
[Trait("Category", "Hostile")]
public sealed class HostileFixtureTests(GeneratedHostileFixtures generated) : IClassFixture<GeneratedHostileFixtures>
{
    /// <summary>The parse-driven rows: driven through <see cref="IParsing"/>.</summary>
    public static TheoryData<string> ParseDrivenFixtures => Names(entry => entry.IsParseDriven);

    /// <summary>The complete-path rows: driven through <see cref="ArtifactCompletionVerifier"/>.</summary>
    public static TheoryData<string> CompleteDrivenFixtures => Names(entry => entry.IsCompleteDriven);

    /// <summary>
    /// AC1: the manifest is the index of the corpus — one row per committed file in both directions, the row count
    /// equal to the committed file count plus the two generated fixtures (asserted here, as AC1 requires), no
    /// generated fixture committed, and the flat corpus free of a FixtureLoader <c>meta.json</c> (that layout
    /// demands exactly one <c>input.*</c> per directory, which a multi-file corpus cannot satisfy; the exemption
    /// is documented in fixtures/hostile/README.md and fixtures/README.md).
    /// </summary>
    [Fact]
    public void ManifestListsEveryCorpusFile()
    {
        var committed = HostileFixtureCorpus.All
            .Where(entry => !entry.Generated)
            .Select(entry => entry.File)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        var generatedEntries = HostileFixtureCorpus.All
            .Where(entry => entry.Generated)
            .Select(entry => entry.File)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        committed.ShouldBe(HostileFixtureCorpus.FilesOnDisk);
        HostileFixtureCorpus.All.Count.ShouldBe(HostileFixtureCorpus.FilesOnDisk.Count + generatedEntries.Count);
        generatedEntries.ShouldBe(
            new[] { GenerateBombFixture.FileName, GenerateOversizedFixture.FileName }
                .OrderBy(name => name, StringComparer.Ordinal));

        foreach (var name in generatedEntries)
        {
            File.Exists(Path.Combine(HostileFixtureCorpus.DirectoryPath, name))
                .ShouldBeFalse($"{name} is generated at test time and must not be committed (AC1)");
            File.Exists(generated.PathOf(name)).ShouldBeTrue($"{name} is built for every run");
        }

        HostileFixtureCorpus.FilesOnDisk.ShouldNotContain("meta.json");

        // AC1: planted_secret.txt belongs to the 06.2.3 scanner corpus and carries a synthetic, self-describing
        // value — never a real credential.
        var planted = HostileFixtureCorpus.Entry("planted_secret.txt");

        planted.IsReferenced.ShouldBeTrue();
        File.ReadAllText(HostileFixtureCorpus.PathOf(planted)).ShouldContain("EVIDGATE-DUMMY-NOT-A-SECRET");
    }

    /// <summary>
    /// Every row names a guard of the corpus's closed list, an entry point the corpus drives, and a failure code
    /// that is an existing constant of the vocabulary its entry point returns — so a row can never point at a
    /// guard or a code that does not exist.
    /// </summary>
    [Fact]
    public void EveryManifestRowNamesAnExistingGuardAndFailureCode()
    {
        var parseReasons = Constants(typeof(ParsingFailureReason));
        var failureCodes = Constants(typeof(ArtifactFailureCode));

        foreach (var entry in HostileFixtureCorpus.All)
        {
            HostileFixtureCorpus.GuardIds.ShouldContain(entry.GuardId, $"{entry.File} names a guard of the profile");
            HostileFixtureCorpus.DriveModes.ShouldContain(entry.DrivenThrough, $"{entry.File} names an entry point");
            entry.Guard.ShouldStartWith(entry.GuardId);

            if (!entry.ExpectsRejection)
            {
                (entry.IsAccepted || entry.IsReferenced).ShouldBeTrue($"{entry.File} documents a known outcome");
                entry.ExpectedOutcome.ShouldNotBeNull();

                continue;
            }

            entry.IsParseDriven.ShouldNotBe(entry.IsCompleteDriven, $"{entry.File} names exactly one driver");
            (entry.IsParseDriven ? parseReasons : failureCodes)
                .ShouldContain(entry.ExpectedFailureCode!, $"{entry.File}'s code exists in its own vocabulary");
        }

        // Both complete-path guards are covered, not only the media sniff.
        HostileFixtureCorpus.All.ShouldContain(
            entry => entry.ExpectedFailureCode == ArtifactFailureCode.TooLarge,
            "the size cap is exercised by a corpus row");
        HostileFixtureCorpus.All.ShouldContain(
            entry => entry.ExpectedFailureCode == ArtifactFailureCode.MediaTypeMismatch,
            "the media sniff is exercised by a corpus row");
    }

    /// <summary>
    /// AC2: every parse-driven row is refused by <see cref="IParsing"/> with the reason its row documents
    /// (<c>artifact_invalid</c> carrying exactly one <c>content</c> reason), and the long-comment archive — the
    /// positive control — is accepted with its entry listed, so the profile still parses a well-formed archive.
    /// </summary>
    /// <param name="file">The manifest file name of the case.</param>
    [Theory]
    [MemberData(nameof(ParseDrivenFixtures))]
    public void RejectsAllCorpusFixtures(string file)
    {
        var entry = HostileFixtureCorpus.Entry(file);
        var bytes = Bytes(entry);
        var parsing = new ParsingService();

        using var content = new MemoryStream(bytes, writable: false);

        if (string.Equals(entry.DrivenThrough, HostileFixture.ParseXmlMode, StringComparison.Ordinal))
        {
            AssertProfileRefusal(
                parsing.ParseXmlReport(content, HostileFixtureCorpus.MaxBytes, TestContext.Current.CancellationToken),
                entry);

            return;
        }

        var archive = parsing.EnumerateArchive(content, HostileFixtureCorpus.MaxBytes, TestContext.Current.CancellationToken);

        if (entry.IsAccepted)
        {
            archive.IsSuccess.ShouldBeTrue(archive.Error?.Message);
            archive.Value.Select(item => item.Name).ShouldContain("trace.trace");

            return;
        }

        AssertProfileRefusal(archive, entry);
    }

    /// <summary>
    /// AC2 for the hostile OBJECTS: an object whose declared media type the profile has no signature for is
    /// refused by the completion's sniff (<c>media_type_mismatch</c>, the fail-closed arm of
    /// <c>StagingSniffFailureAsync</c>, decided with no byte read), and one above the kind cap is refused with
    /// <c>too_large</c> from the length the store's <c>HEAD</c> reports. Every verdict is a rejection, so no
    /// corpus object can be committed as evidence.
    /// </summary>
    /// <param name="file">The manifest file name of the case.</param>
    [Theory]
    [MemberData(nameof(CompleteDrivenFixtures))]
    public async Task CompletePathRejectsAllCorpusObjects(string file)
    {
        var entry = HostileFixtureCorpus.Entry(file);
        var mediaType = entry.DeclaredMediaType;

        mediaType.ShouldNotBeNull($"{file} declares the media type it is refused for");

        var verdict = await VerifyAsync(Bytes(entry), mediaType!);

        verdict.Verified.ShouldBeFalse($"{file} must never reach ready");
        verdict.VerifiedBytes.ShouldBeNull("nothing was verified for a refused object");
        verdict.FailureCode.ShouldBe(entry.ExpectedFailureCode);
    }

    /// <summary>
    /// AC5: no second copy of an <c>xxe*.xml</c> or <c>billion_laughs*</c> document exists outside
    /// <c>fixtures/hostile/</c>, so no parser test keeps its own hostile input outside the manifest.
    /// </summary>
    [Fact]
    public void TheCorpusIsTheSingleSourceOfHostileFixtures()
    {
        // Only the real corpus directory is exempt. A bare prefix would also exempt a sibling that merely starts
        // with the same characters (fixtures/hostile-old/, fixtures/hostile2/), so the prefix carries the separator;
        // both sides are normalised to it first.
        var corpus = Path.GetRelativePath(RlsExceptionFile.RepositoryRoot, HostileFixtureCorpus.DirectoryPath)
            .Replace(Path.DirectorySeparatorChar, '/');
        var corpusPrefix = corpus.TrimEnd('/') + '/';
        var offenders = new List<string>();

        foreach (var path in RepositoryFiles(RlsExceptionFile.RepositoryRoot))
        {
            var relative = Path.GetRelativePath(RlsExceptionFile.RepositoryRoot, path)
                .Replace(Path.DirectorySeparatorChar, '/');

            if (relative.StartsWith(corpusPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            if (IsCorpusName(Path.GetFileName(path)))
            {
                offenders.Add(relative);
            }
        }

        offenders.ShouldBeEmpty("the hostile corpus lives only in fixtures/hostile: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// AC1: the two generated fixtures are reproducible from their seeds (a second build is byte-identical) and
    /// carry the property their row documents — the bomb more entries than the profile's cap, the oversized object
    /// more bytes than the kind cap and fewer than the parse cap.
    /// </summary>
    [Fact]
    public void TheGeneratedFixturesAreReproducibleAndAsDocumented()
    {
        var bomb = File.ReadAllBytes(generated.PathOf(GenerateBombFixture.FileName));
        var oversized = File.ReadAllBytes(generated.PathOf(GenerateOversizedFixture.FileName));

        bomb.SequenceEqual(GenerateBombFixture.Build()).ShouldBeTrue("the bomb is a pure function of its seed");
        oversized.SequenceEqual(GenerateOversizedFixture.Build())
            .ShouldBeTrue("the oversized object is a pure function of its seed");

        using (var content = new MemoryStream(bomb, writable: false))
        using (var archive = new ZipArchive(content, ZipArchiveMode.Read))
        {
            archive.Entries.Count.ShouldBe(GenerateBombFixture.EntryCount);
        }

        GenerateBombFixture.EntryCount.ShouldBeGreaterThan(ParsingLimits.MaxZipEntries);
        oversized.Length.ShouldBe(GenerateOversizedFixture.DeclaredBytes);
        GenerateOversizedFixture.DeclaredBytes.ShouldBeGreaterThan(HostileFixtureCorpus.KindCapBytes);
        GenerateOversizedFixture.DeclaredBytes.ShouldBeLessThanOrEqualTo(HostileFixtureCorpus.MaxBytes);
    }

    /// <summary>The file's bytes: a committed fixture from the corpus directory, a generated one from the temp tree.</summary>
    /// <param name="entry">The manifest row.</param>
    private byte[] Bytes(HostileFixture entry) =>
        File.ReadAllBytes(entry.Generated ? generated.PathOf(entry.File) : HostileFixtureCorpus.PathOf(entry));

    /// <summary>The profile refusal a row documents: a failure carrying <c>artifact_invalid</c> and exactly its reason.</summary>
    /// <typeparam name="T">The success payload of the driven member.</typeparam>
    /// <param name="result">The profile's answer.</param>
    /// <param name="entry">The manifest row the answer is compared with.</param>
    private static void AssertProfileRefusal<T>(Result<T> result, HostileFixture entry)
    {
        result.IsSuccess.ShouldBeFalse($"{entry.File} must be refused by the {entry.GuardId} guard");
        result.Error.ShouldNotBeNull();
        result.Error!.Code.ShouldBe(ParsingErrorCode.ArtifactInvalid);
        result.Error.Status.ShouldBe(422);
        result.Error.Details.ShouldNotBeNull();
        result.Error.Details!.Count.ShouldBe(1);
        result.Error.Details[0].Path.ShouldBe("content");
        result.Error.Details[0].Reason.ShouldBe(entry.ExpectedFailureCode);
    }

    /// <summary>
    /// Drives one hostile object through the verification the <c>complete</c> route runs
    /// (<see cref="ArtifactCompletionVerifier"/>): a store that reports exactly the object's length and SHA-256
    /// behind one ETag, the artifact's own tenant, and a kind policy that ACCEPTS the declared media type and caps
    /// the size at <see cref="HostileFixtureCorpus.KindCapBytes"/>. The declared type is allowed by policy on
    /// purpose: what the corpus proves is the media sniff's fail-closed arm, not the policy gate. The row is
    /// present, so the kind — <c>raw_report</c>, the kind every artifact this corpus describes is — contributes
    /// only its sensitivity default, which no verdict reads.
    /// </summary>
    /// <param name="bytes">The hostile object's bytes, as the store holds them.</param>
    /// <param name="declaredMediaType">The media type the artifact declares.</param>
    private static async Task<ArtifactCompletionVerdict> VerifyAsync(byte[] bytes, string declaredMediaType)
    {
        var orgId = Guid.CreateVersion7();
        var projectId = Guid.CreateVersion7();
        var artifactId = Guid.CreateVersion7();
        var checksum = Convert.ToHexStringLower(SHA256.HashData(bytes));

        var facts = new ArtifactFacts(
            ArtifactState.Uploaded,
            bytes.LongLength,
            $"sha256:{checksum}",
            declaredMediaType,
            "raw_report",
            $"projects/{projectId}/artifacts/{artifactId}",
            projectId,
            FailureCode: null,
            ProjectStatus: "active",
            MaxBytes: HostileFixtureCorpus.KindCapBytes,
            MediaTypes: [declaredMediaType],
            CaptureEnabled: true,
            DefaultSensitivity: null,
            BoundCiRunRef: null);

        var scopes = Substitute.For<ITenantScopeFactory>();
        scopes
            .BeginAsync(Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Substitute.For<ITenantScope>()));

        var db = Substitute.For<ITenantDb>();
        db
            .QuerySingleOrDefaultAsync<ArtifactFacts>(
                Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(facts);

        var tenant = Substitute.For<IRequestTenantContextAccessor>();
        tenant.Require().Returns(new RequestTenantContext(orgId, projectId));

        var permissions = new GrantedPermissionEvaluator();

        var storage = Substitute.For<IObjectStorage>();
        storage
            .HeadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ObjectHead(bytes.LongLength, checksum, "\"etag-hostile-corpus\""));

        var verifier = new ArtifactCompletionVerifier(scopes, db, tenant, permissions, storage);
        var principal = RequestPrincipal.Service(
            projectId, [Permissions.Artifact.Write], credentialId: "jti-hostile-corpus");

        var result = await verifier.VerifyAsync(artifactId, principal, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);

        return result.Value;
    }

    /// <summary>The public string constants of one closed vocabulary (the codes a manifest row may name).</summary>
    /// <param name="vocabulary">The type carrying the constants.</param>
    private static IReadOnlyList<string> Constants(Type vocabulary) =>
    [
        .. vocabulary
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field is { IsLiteral: true } && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .OrderBy(value => value, StringComparer.Ordinal),
    ];

    /// <summary>The theory data of the rows a predicate keeps.</summary>
    /// <param name="predicate">The row filter.</param>
    private static TheoryData<string> Names(Func<HostileFixture, bool> predicate)
    {
        var data = new TheoryData<string>();

        foreach (var entry in HostileFixtureCorpus.All.Where(predicate))
        {
            data.Add(entry.File);
        }

        return data;
    }

    /// <summary>True for a file name the corpus owns: an <c>xxe*.xml</c> document or a <c>billion_laughs*</c> bomb.</summary>
    /// <param name="name">The file name.</param>
    private static bool IsCorpusName(string name) =>
        (name.StartsWith("xxe", StringComparison.OrdinalIgnoreCase)
            && name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
        || name.StartsWith("billion_laughs", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Every file under <paramref name="directory"/>, minus the directories no source corpus file lives in (the
    /// git store, dependencies, build output) and minus symlinked directories. A manual walk keeps the AC5 check
    /// self-contained — it works in a shallow, detached or exported checkout and never shells out to git.
    /// </summary>
    /// <param name="directory">The directory to walk.</param>
    private static IEnumerable<string> RepositoryFiles(string directory)
    {
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.None };

        foreach (var file in Directory.EnumerateFiles(directory, "*", options))
        {
            yield return file;
        }

        foreach (var child in Directory.EnumerateDirectories(directory, "*", options))
        {
            if (PrunedDirectories.Contains(Path.GetFileName(child), StringComparer.Ordinal)
                || Directory.ResolveLinkTarget(child, returnFinalTarget: false) is not null)
            {
                continue;
            }

            foreach (var file in RepositoryFiles(child))
            {
                yield return file;
            }
        }
    }

    /// <summary>The directories no source corpus file lives in: the git store, dependencies and build output.</summary>
    private static readonly string[] PrunedDirectories = [".git", "node_modules", "bin", "obj", ".vs", ".idea"];

    /// <summary>
    /// Grants every permission, exactly as the Identity module's evaluator does for a member holding
    /// <c>artifact:write</c>. A hand-written fake rather than a substitute: the member returns
    /// <see cref="ValueTask{TResult}"/>, which NSubstitute's setup chain would produce outside an await (CA2012).
    /// </summary>
    private sealed class GrantedPermissionEvaluator : IPermissionEvaluator
    {
        /// <inheritdoc />
        public ValueTask<PermissionDecision> EvaluateAsync(
            string permission,
            IRequestPrincipal principal,
            IRequestTenantContext context,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(PermissionDecision.Granted);
    }
}
