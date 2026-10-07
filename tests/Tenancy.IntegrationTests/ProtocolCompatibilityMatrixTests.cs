using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Evidgate.BuildingBlocks.Contracts.Validation;
using Evidgate.Modules.Ingestion.Contracts;
using Evidgate.Modules.Ingestion.Domain;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 04.7.1-T3 / AC6 (T42): the table-driven protocol compatibility matrix. Every row of the shared
/// corpus <c>fixtures/contracts/protocol-matrix/negotiation.v1.json</c> is one test case here and one
/// in the TypeScript suite (<c>packages/test-fixtures/src/protocolMatrix.test.ts</c>): the same rows,
/// the same rules (<c>contracts/adapter/README.md</c> §Negotiation rules), so flipping any single
/// <c>expected</c> value in the corpus fails both suites.
/// </summary>
/// <remarks>
/// <para>
/// Each row runs the real server path: validate the document with
/// <see cref="ContractSchemaSet"/> against <c>capability.v1</c> (the same validator
/// <c>RegisterAdapterCapability</c> uses), bind it with the internal <c>CapabilityBinder.Bind</c>, then
/// negotiate through the exported <see cref="IProtocolNegotiator"/> with the row's
/// <see cref="ServerProtocol"/>. Expected values come only from the corpus — no verdict is hard-coded
/// in C#.
/// </para>
/// <para>
/// The comparison is exact and per-row: <see cref="Rows"/> keys each test case by row id, and the
/// assertions compare the whole expected object (verdict, code, <c>details</c> in order, minor
/// relation, unknown-flag list, and the distinct ordinal-sorted <c>capability_invalid</c> path set)
/// against the actual. Changing one <c>expected</c> value therefore changes exactly one row's expected
/// object and fails exactly that row's test case.
/// </para>
/// </remarks>
public sealed class ProtocolCompatibilityMatrixTests
{
    /// <summary>The corpus file, relative to the repository root.</summary>
    private const string CorpusRelativePath = "fixtures/contracts/protocol-matrix/negotiation.v1.json";

    private static readonly string RepoRoot = LocateRepoRoot();

    private static readonly Lazy<Corpus> CorpusFile = new(LoadCorpus);

    private static readonly Lazy<ContractSchemaSet> Schemas =
        new(() => ContractSchemaSet.Load(Path.Combine(RepoRoot, "contracts")));

    private static readonly ProtocolNegotiator Negotiator = new();

    /// <summary>One test case per corpus row, keyed by the row id so a failure names the row.</summary>
    public static IEnumerable<object[]> Rows()
    {
        foreach (var row in CorpusFile.Value.Rows)
        {
            yield return [row.Id];
        }
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public void Row_Matches_Expected_Verdict(string rowId)
    {
        var row = CorpusFile.Value.Rows.Single(r => r.Id == rowId);
        var document = DocumentUnderTest(row);

        var verdict = Schemas.Value.Validate(ContractSchemaKeys.Capability, document);

        // Schema first (mirrors RegisterAdapterCapability): a schema failure is capability_invalid and
        // never reaches negotiation, so a major mismatch on an otherwise-invalid document is not reported.
        if (row.Expected.Code == "capability_invalid")
        {
            verdict.IsValid.ShouldBeFalse($"{row.Id}: expected capability_invalid, got a valid document");

            var actualPaths = verdict.Errors
                .Select(error => error.Path)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var expectedPaths = row.Expected.Paths!.Order(StringComparer.Ordinal).ToArray();

            actualPaths.ShouldBe(expectedPaths, $"{row.Id}: capability_invalid paths differ");
            return;
        }

        verdict.IsValid.ShouldBeTrue($"{row.Id}: expected a schema-valid document, got {Format(verdict)}");

        var binding = CapabilityBinder.Bind(document);
        // Negotiate through the exported interface (04.7.1-T2.3), not the internal concrete type.
        var result = ((IProtocolNegotiator)Negotiator).Negotiate(
            binding.Document,
            new ServerProtocol(row.Server.SupportedMajors, row.Server.Minor));

        if (row.Expected.Verdict == "accepted")
        {
            result.Accepted.ShouldBeTrue($"{row.Id}: expected accepted, got {result.RejectionCode}");
            result.RejectionCode.ShouldBeNull();
            result.Details.ShouldBeEmpty();
            result.Minor.ShouldBe(MinorRelationOf(row.Expected.Minor!));
            result.UnknownSupportsFlags.ShouldBe(row.Expected.UnknownSupportsFlags!);

            // Every declared support flag survives binding — an unknown flag is kept, never dropped (AC3).
            var declared = document.GetProperty("supports").EnumerateObject()
                .Select(flag => flag.Name)
                .Order(StringComparer.Ordinal)
                .ToArray();
            binding.Document.Supports.Keys.Order(StringComparer.Ordinal).ToArray().ShouldBe(declared);
        }
        else
        {
            result.Accepted.ShouldBeFalse($"{row.Id}: expected rejected, got accepted");
            result.RejectionCode.ShouldBe(row.Expected.Code);
            result.Minor.ShouldBeNull();
            result.UnknownSupportsFlags.ShouldBeEmpty();

            // details[] order is part of the contract: adapter major first, then the supported majors.
            var actual = result.Details.Select(detail => $"{detail.Path}|{detail.Reason}").ToArray();
            var expected = row.Expected.Details!.Select(detail => $"{detail.Path}|{detail.Reason}").ToArray();
            actual.ShouldBe(expected, $"{row.Id}: details differ");
        }
    }

    /// <summary>
    /// The corpus shape contract: the declared grid axes are covered exactly once, ids are unique, no
    /// row carries an unknown kind, every row carries the members its kind requires, and each
    /// <c>expected</c> object carries exactly the members its verdict defines.
    /// </summary>
    [Fact]
    public void Corpus_Declares_Every_Grid_Combination_Exactly_Once()
    {
        var corpus = CorpusFile.Value;

        corpus.Format.ShouldBe("evidgate.protocol-matrix.v1");
        corpus.Comment.ShouldNotBeNullOrWhiteSpace();

        corpus.Rows.Select(row => row.Id).Distinct(StringComparer.Ordinal).Count()
            .ShouldBe(corpus.Rows.Count, "row ids must be unique");

        corpus.Rows.Select(row => row.Kind).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .ShouldBe(["grid", "precedence", "unknown_field"], Case.Sensitive, "no row may carry an unknown kind");

        var expected = (
                from majors in corpus.Grid.ServerSupportedMajors
                from serverMinor in corpus.Grid.ServerMinor
                from adapterMajor in corpus.Grid.AdapterMajor
                from adapterMinor in corpus.Grid.AdapterMinor
                select Key(majors, serverMinor, adapterMajor, adapterMinor))
            .Order(StringComparer.Ordinal)
            .ToArray();

        var actual = corpus.Rows
            .Where(row => row.Kind == "grid")
            .Select(row => Key(row.Server.SupportedMajors, row.Server.Minor, row.Adapter!.Major, row.Adapter.Minor))
            .Order(StringComparer.Ordinal)
            .ToArray();

        actual.ShouldBe(expected, "the grid rows must cover every declared combination exactly once");

        foreach (var row in corpus.Rows)
        {
            row.Why.ShouldNotBeNullOrWhiteSpace($"{row.Id}: every row documents its expected outcome");
            row.Server.SupportedMajors.ShouldNotBeEmpty($"{row.Id}: server.supported_majors is required");

            if (row.Kind == "grid")
            {
                row.Adapter.ShouldNotBeNull($"{row.Id}: a grid row carries adapter");
                row.Document.HasValue.ShouldBeFalse($"{row.Id}: a grid row has no document (base_document is used)");
            }
            else
            {
                row.Document.HasValue.ShouldBeTrue($"{row.Id}: a non-grid row carries document");
                row.Adapter.ShouldBeNull($"{row.Id}: a non-grid row has no adapter");
            }

            // The expected object's key set is exact per verdict: the typed record tolerates extra
            // members, the TypeScript suite rejects them, so assert the same here.
            var expectedKeys = row.ExpectedRaw.EnumerateObject()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal)
                .ToArray();

            switch (row.Expected.Verdict)
            {
                case "accepted":
                    expectedKeys.ShouldBe(
                        ["minor", "unknown_supports_flags", "verdict"],
                        Case.Sensitive,
                        $"{row.Id}: an accepted expected carries exactly verdict, minor and unknown_supports_flags");
                    row.Expected.Minor.ShouldNotBeNullOrWhiteSpace($"{row.Id}: accepted rows name the minor relation");
                    row.Expected.UnknownSupportsFlags.ShouldNotBeNull($"{row.Id}: accepted rows list unknown flags");
                    row.Expected.Code.ShouldBeNull();
                    break;
                case "rejected":
                    row.Expected.Code.ShouldNotBeNullOrWhiteSpace($"{row.Id}: rejected rows name a code");
                    if (row.Expected.Code == "capability_invalid")
                    {
                        expectedKeys.ShouldBe(
                            ["code", "paths", "verdict"],
                            Case.Sensitive,
                            $"{row.Id}: a capability_invalid expected carries exactly verdict, code and paths");
                        row.Expected.Paths.ShouldNotBeNull($"{row.Id}: capability_invalid rows list paths");
                        row.Expected.Paths!.ShouldNotBeEmpty();
                    }
                    else
                    {
                        expectedKeys.ShouldBe(
                            ["code", "details", "verdict"],
                            Case.Sensitive,
                            $"{row.Id}: an unsupported_protocol_major expected carries exactly verdict, code and details");
                        row.Expected.Details.ShouldNotBeNull($"{row.Id}: unsupported_protocol_major rows list details");
                        row.Expected.Details!.ShouldNotBeEmpty();
                    }

                    break;
                default:
                    throw new InvalidOperationException($"{row.Id}: unknown verdict '{row.Expected.Verdict}'");
            }
        }
    }

    /// <summary>
    /// The document under test: for a grid row, <c>base_document</c> with <c>protocol</c> replaced by the
    /// row's adapter version; otherwise the row's own <c>document</c>.
    /// </summary>
    private static JsonElement DocumentUnderTest(Row row)
    {
        if (row.Kind != "grid")
        {
            return row.Document!.Value.Clone();
        }

        var node = JsonNode.Parse(CorpusFile.Value.BaseDocument.GetRawText())!.AsObject();
        node["protocol"] = new JsonObject
        {
            ["major"] = row.Adapter!.Major,
            ["minor"] = row.Adapter.Minor,
        };

        return JsonSerializer.SerializeToElement(node);
    }

    private static MinorRelation MinorRelationOf(string relation) => relation switch
    {
        "equal" => MinorRelation.Equal,
        "adapter_newer" => MinorRelation.AdapterNewer,
        "adapter_older" => MinorRelation.AdapterOlder,
        var other => throw new InvalidOperationException($"unknown minor relation '{other}'"),
    };

    private static string Key(IReadOnlyList<int> majors, int serverMinor, int adapterMajor, int adapterMinor) =>
        $"s{string.Join("-", majors)}-m{serverMinor}-a{adapterMajor}-n{adapterMinor}";

    private static string Format(ContractValidationResult result) =>
        result.Errors.Count == 0
            ? "(valid)"
            : string.Join("; ", result.Errors.Select(error => $"{error.Path}: {error.Reason}"));

    private static Corpus LoadCorpus()
    {
        var path = Path.Combine(RepoRoot, CorpusRelativePath);
        File.Exists(path).ShouldBeTrue($"the shared protocol-matrix corpus must exist at {CorpusRelativePath}");

        var corpus = JsonSerializer.Deserialize<Corpus>(File.ReadAllText(path));
        corpus.ShouldNotBeNull($"unparseable corpus {CorpusRelativePath}");
        return corpus;
    }

    private static string LocateRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Evidgate.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("could not locate the repository root (Evidgate.slnx)");
    }

    /// <summary>The shared corpus (fixtures/contracts/protocol-matrix/negotiation.v1.json).</summary>
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record Corpus(
        [property: JsonPropertyName("format")] string Format,
        [property: JsonPropertyName("comment")] string Comment,
        [property: JsonPropertyName("base_document")] JsonElement BaseDocument,
        [property: JsonPropertyName("grid")] Grid Grid,
        [property: JsonPropertyName("rows")] IReadOnlyList<Row> Rows);

    /// <summary>The declared grid axes; every combination must appear exactly once among the grid rows.</summary>
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record Grid(
        [property: JsonPropertyName("server_supported_majors")] IReadOnlyList<IReadOnlyList<int>> ServerSupportedMajors,
        [property: JsonPropertyName("server_minor")] IReadOnlyList<int> ServerMinor,
        [property: JsonPropertyName("adapter_major")] IReadOnlyList<int> AdapterMajor,
        [property: JsonPropertyName("adapter_minor")] IReadOnlyList<int> AdapterMinor);

    /// <summary>One corpus row.</summary>
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record Row(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("why")] string Why,
        [property: JsonPropertyName("server")] Server Server,
        [property: JsonPropertyName("adapter")] Adapter? Adapter,
        [property: JsonPropertyName("document")] JsonElement? Document,
        [property: JsonPropertyName("expected")] JsonElement ExpectedRaw)
    {
        /// <summary>
        /// The typed expected outcome. Kept raw above so the shape test can assert the exact key set
        /// per verdict: <see cref="Expected"/> tolerates extra members, the TypeScript suite does not.
        /// </summary>
        public Expected Expected => JsonSerializer.Deserialize<Expected>(ExpectedRaw.GetRawText())!;
    }

    /// <summary>The server side of a row.</summary>
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record Server(
        [property: JsonPropertyName("supported_majors")] IReadOnlyList<int> SupportedMajors,
        [property: JsonPropertyName("minor")] int Minor);

    /// <summary>The adapter version of a grid row.</summary>
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record Adapter(
        [property: JsonPropertyName("major")] int Major,
        [property: JsonPropertyName("minor")] int Minor);

    /// <summary>The expected outcome of a row.</summary>
    private sealed record Expected(
        [property: JsonPropertyName("verdict")] string Verdict,
        [property: JsonPropertyName("minor")] string? Minor,
        [property: JsonPropertyName("unknown_supports_flags")] IReadOnlyList<string>? UnknownSupportsFlags,
        [property: JsonPropertyName("code")] string? Code,
        [property: JsonPropertyName("details")] IReadOnlyList<Detail>? Details,
        [property: JsonPropertyName("paths")] IReadOnlyList<string>? Paths);

    /// <summary>One expected <c>details[]</c> row of an <c>unsupported_protocol_major</c> rejection.</summary>
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record Detail(
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("reason")] string Reason);
}
