using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Evidgate.BuildingBlocks.Contracts.Validation;
using Shouldly;
using Xunit;

namespace Contract.Tests;

/// <summary>
/// 01.3.2-T2.4 / AC3 (T42): the <c>fixtures/contracts/</c> corpus run through
/// Corvus.Json.Validator — the same files, verdict sidecars and assertions the
/// CLI's Ajv suite (<c>packages/cli/src/parity.test.ts</c>) performs. A vector
/// whose Corvus verdict differs from its sidecar fails here, and any vector on
/// which the two runtimes disagree fails both suites: the corpus is the
/// contract, the engines must agree with it.
/// </summary>
/// <remarks>
/// The format is documented in <c>fixtures/contracts/README.md</c>. The
/// <c>divergence/</c> directory holds a seeded disagreement (dv-001) that is
/// skipped unless <see cref="DivergenceEnvVar"/> is <c>1</c>; QA sets it to
/// prove the failure path, and the <see cref="Divergence_Vector_Disagrees_With_Sidecar"/>
/// self-check keeps that mechanism honest in normal runs.
/// </remarks>
public sealed class ParityCorpusTests
{
    /// <summary>Env var that arms the seeded-divergence fixture (AC3).</summary>
    public const string DivergenceEnvVar = "EVIDGATE_PARITY_INCLUDE_DIVERGENCE";

    private static readonly Lazy<ContractSchemaSet> Schemas =
        new(() => ContractSchemaSet.Load(ContractPaths.ContractsDirectory));

    /// <summary>Every vector file + its verdict sidecar, divergence excluded unless armed.</summary>
    public static IEnumerable<object[]> Vectors()
    {
        var includeDivergence = Environment.GetEnvironmentVariable(DivergenceEnvVar) == "1";
        foreach (var vectorFile in EnumerateVectors(includeDivergence))
        {
            yield return [vectorFile];
        }
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Corpus_Vector_Matches_Its_Verdict(string vectorFile)
    {
        var relative = Path.GetRelativePath(ContractPaths.FixturesDirectory, vectorFile);
        var sidecarPath = vectorFile[..^".json".Length] + ".verdict.json";
        File.Exists(sidecarPath).ShouldBeTrue($"missing verdict sidecar for {relative}");

        var verdict = JsonSerializer.Deserialize<Verdict>(File.ReadAllText(sidecarPath));
        verdict.ShouldNotBeNull($"unparseable verdict sidecar {relative}.verdict.json");

        // The schema key comes from the directory name; a wrong sidecar schema
        // is a corpus authoring error, not a validator verdict.
        var schemaKey = Path.GetDirectoryName(relative)!.Split(Path.DirectorySeparatorChar)[^1];
        verdict.Schema.ShouldBe(schemaKey, $"verdict schema mismatch in {relative}.verdict.json");

        var result = Schemas.Value.Validate(schemaKey, File.ReadAllBytes(vectorFile));

        result.IsValid.ShouldBe(
            verdict.Valid,
            $"{relative}: engine verdict differs from the sidecar (errors: {Format(result.Errors)})");

        if (verdict.Valid)
        {
            result.Errors.ShouldBeEmpty($"{relative} expected accept, got {Format(result.Errors)}");
            return;
        }

        verdict.Errors.ShouldNotBeNull($"{relative}.verdict.json is a reject without an errors[] row");
        verdict.Errors.ShouldNotBeEmpty($"{relative}.verdict.json is a reject with an empty errors[]");

        var expected = verdict.Errors
            .Select(e => $"{IndexText(e.Index)}|{e.Path}|{e.Reason}")
            .Order(StringComparer.Ordinal)
            .ToArray();
        var actual = result.Errors
            .Select(e => $"{IndexText(e.Index)}|{e.Path}|{e.Reason}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        actual.ShouldBe(
            expected,
            $"{relative}: details[] differ.\n  actual:   {Format(result.Errors)}\n  expected: {Format(verdict.Errors)}");
    }

    /// <summary>
    /// The seeded-divergence fixture is a corpus-level escape hatch: excluded by
    /// default, and arming it (<see cref="DivergenceEnvVar"/>=1) makes the
    /// corpus theory red on it in both suites. This test verifies the mechanism
    /// itself — the file exists, is excluded from the default enumeration, and
    /// really does disagree with its sidecar when validated.
    /// </summary>
    [Fact]
    public void Divergence_Vector_Disagrees_With_Sidecar()
    {
        var vector = Path.Combine(
            ContractPaths.FixturesDirectory,
            "divergence",
            "event-envelope.v1",
            "dv-001-seeded-disagreement.json");
        File.Exists(vector).ShouldBeTrue("the seeded-divergence fixture must exist");
        EnumerateVectors(includeDivergence: false).ShouldNotContain(vector);
        EnumerateVectors(includeDivergence: true).ShouldContain(vector);

        var sidecar = vector[..^".json".Length] + ".verdict.json";
        var verdict = JsonSerializer.Deserialize<Verdict>(File.ReadAllText(sidecar));
        verdict.ShouldNotBeNull();
        var result = Schemas.Value.Validate(verdict.Schema!, File.ReadAllBytes(vector));

        (result.IsValid == verdict.Valid
            && MatchesExactly(result.Errors, verdict.Errors ?? [])).ShouldBeFalse(
            "dv-001 must disagree with its seeded verdict; if it agrees, either the fixture or this check is broken");
    }

    /// <summary>
    /// V-1 (01.3.2-T2 hardening): the mapped <c>details[]</c> list hard-caps at
    /// <see cref="ContractValidationResult.MaxDetails"/>; the moment a further
    /// distinct row would exceed it, collection stops and the terminal
    /// <c>truncated</c> marker row lands as the last element — the identical
    /// contract the CLI applies (fixtures/contracts/README.md). 150 items each
    /// violating <c>attempt_no ≥ 1</c> produce 150 distinct rows pre-cap.
    /// </summary>
    [Fact]
    public void Detail_Errors_Are_Capped_With_A_Terminal_Truncated_Marker()
    {
        var document = BuildInvalidBatch(count: 150);

        var result = Schemas.Value.Validate(ContractSchemaKeys.EventEnvelope, document);

        result.IsValid.ShouldBeFalse();
        result.Errors.Count.ShouldBe(ContractValidationResult.MaxDetails + 1);
        result.Errors[^1].ShouldBe(ContractValidationError.Truncated);
        result.Errors.SkipLast(1).ShouldAllBe(e =>
            e.Index != null && e.Reason == "minimum" && e.Path.StartsWith("/events/", StringComparison.Ordinal));
    }

    /// <summary>
    /// The cap boundary is exact: 100 distinct mapped rows are reported verbatim
    /// with no marker appended.
    /// </summary>
    [Fact]
    public void Detail_Errors_At_The_Cap_Are_Untruncated()
    {
        var document = BuildInvalidBatch(count: ContractValidationResult.MaxDetails);

        var result = Schemas.Value.Validate(ContractSchemaKeys.EventEnvelope, document);

        result.IsValid.ShouldBeFalse();
        result.Errors.Count.ShouldBe(ContractValidationResult.MaxDetails);
        result.Errors.ShouldAllBe(e => e.Reason == "minimum");
    }

    /// <summary>
    /// V-2 (01.3.2-T2 hardening): a composite-keyword failure with no leaf child
    /// must still produce a mapped row — the Ajv parity corpus pins identical
    /// (index, path, reason) triples for these keywords, and the rows below are
    /// what Ajv 8 emits for the same schema+document pairs.
    /// </summary>
    [Theory]
    // The schema key is unique per case: Corvus caches compiled schemas by
    // canonical URI (urn:evidgate:contract:<key>), so parallel cases sharing a
    // key would validate against each other's text.
    [InlineData(
        "probe-not.v1",
        "{\"$schema\":\"#D\",\"not\":{\"const\":1}}",
        "1",
        "-||not")]
    [InlineData(
        "probe-not-nested.v1",
        "{\"$schema\":\"#D\",\"type\":\"object\",\"properties\":{\"a\":{\"not\":{\"const\":1}}}}",
        "{\"a\":1}",
        "-|/a|not")]
    [InlineData(
        "probe-anyof.v1",
        "{\"$schema\":\"#D\",\"type\":\"object\",\"properties\":{\"a\":{\"anyOf\":[{\"type\":\"string\"},{\"const\":2}]}}}",
        "{\"a\":1}",
        "-|/a|any_of")]
    [InlineData(
        "probe-contains.v1",
        "{\"$schema\":\"#D\",\"type\":\"array\",\"contains\":{\"const\":1}}",
        "[]",
        "-||contains")]
    [InlineData(
        "probe-property-names.v1",
        "{\"$schema\":\"#D\",\"type\":\"object\",\"properties\":{\"a\":{\"type\":\"object\",\"propertyNames\":{\"maxLength\":2}}}}",
        "{\"a\":{\"abc\":1}}",
        "-|/a|max_length")]
    [InlineData(
        "probe-dependent-schemas.v1",
        "{\"$schema\":\"#D\",\"type\":\"object\",\"dependentSchemas\":{\"a\":{\"required\":[\"b\"]}}}",
        "{\"a\":1}",
        "-|/b|required")]
    public void Self_Leaf_Composite_Failures_Map_To_Rows(
        string schemaKey, string schemaText, string bodyText, string expectedRow)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"contract-probe-{Guid.NewGuid():N}");
        try
        {
            var schemasDirectory = Path.Combine(directory, "contracts", "schemas");
            Directory.CreateDirectory(schemasDirectory);
            File.WriteAllText(
                Path.Combine(schemasDirectory, schemaKey + ".json"),
                schemaText.Replace("#D", "https://json-schema.org/draft/2020-12/schema", StringComparison.Ordinal));

            var set = ContractSchemaSet.Load(Path.Combine(directory, "contracts"));
            using var body = JsonDocument.Parse(bodyText);

            var result = set.Validate(schemaKey, body.RootElement);

            result.IsValid.ShouldBeFalse($"expected the violation to surface, got {Format(result.Errors)}");
            Canonical(result.Errors).ShouldBe([expectedRow]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// A satisfied <c>not</c> emits its subschema's failing rows in Corvus's
    /// Detailed output — diagnostics of a satisfied negation, never evidence.
    /// A document valid under <c>not</c> but invalid elsewhere surfaces only the
    /// real error (Ajv reports the identical single <c>required</c> row).
    /// </summary>
    [Fact]
    public void Satisfied_Not_Never_Leaks_Its_Subschema_Diagnostics()
    {
        var schemaText = """
            {"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","properties":{"a":{"not":{"const":1}}},"required":["x"]}
            """;

        var directory = Path.Combine(Path.GetTempPath(), $"contract-probe-{Guid.NewGuid():N}");
        try
        {
            var schemasDirectory = Path.Combine(directory, "contracts", "schemas");
            Directory.CreateDirectory(schemasDirectory);
            File.WriteAllText(Path.Combine(schemasDirectory, "probe-not-noise.v1.json"), schemaText);

            var set = ContractSchemaSet.Load(Path.Combine(directory, "contracts"));
            using var body = JsonDocument.Parse("{\"a\":2}");

            var result = set.Validate("probe-not-noise.v1", body.RootElement);

            result.IsValid.ShouldBeFalse();
            Canonical(result.Errors).ShouldBe(["-|/x|required"]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>A batch of <paramref name="count"/> events each missing only <c>attempt_no ≥ 1</c> (one <c>minimum</c> row per item).</summary>
    private static JsonElement BuildInvalidBatch(int count)
    {
        const string eventJson =
            """{"schema_version":1,"event_id":"00000000-0000-0000-0000-000000000001","producer_id":"p","sequence":0,"execution_key":"0000000000000000000000000000000000000000000000000000000000000000","attempt_no":0,"type":"run.log","occurred_at":"2026-01-01T00:00:00Z"}""";
        using var document = JsonDocument.Parse($"{{\"events\":[{string.Join(",", Enumerable.Repeat(eventJson, count))}]}}");
        return document.RootElement.Clone();
    }

    /// <summary>The corpus comparison form applied to live rows: sorted <c>index|path|reason</c> ("-" for a null index).</summary>
    private static string[] Canonical(IReadOnlyList<ContractValidationError> errors) =>
        errors.Select(e => $"{IndexText(e.Index)}|{e.Path}|{e.Reason}")
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static IEnumerable<string> EnumerateVectors(bool includeDivergence)
    {
        foreach (var root in VectorRoots(includeDivergence))
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var directory in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
            {
                foreach (var file in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
                {
                    if (file.EndsWith(".verdict.json", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    yield return file;
                }
            }
        }
    }

    /// <summary>
    /// Corpus roots: <c>vectors/</c> always; <c>divergence/</c> (the same
    /// one-directory-per-schema-key layout) only when armed — the seeded
    /// disagreement must never run in a normal build.
    /// </summary>
    private static IEnumerable<string> VectorRoots(bool includeDivergence)
    {
        yield return Path.Combine(ContractPaths.FixturesDirectory, "vectors");
        if (includeDivergence)
        {
            yield return Path.Combine(ContractPaths.FixturesDirectory, "divergence");
        }
    }

    private static bool MatchesExactly(
        IReadOnlyList<ContractValidationError> actual,
        IReadOnlyList<ExpectedError> expected) =>
        actual.Select(e => $"{IndexText(e.Index)}|{e.Path}|{e.Reason}")
            .Order(StringComparer.Ordinal)
            .SequenceEqual(
                expected.Select(e => $"{IndexText(e.Index)}|{e.Path}|{e.Reason}")
                    .Order(StringComparer.Ordinal));

    private static string IndexText(int? index) =>
        index?.ToString(CultureInfo.InvariantCulture) ?? "-";

    private static string Format(IReadOnlyList<ContractValidationError> errors) =>
        errors.Count == 0
            ? "(none)"
            : string.Join("; ", errors.Select(e => $"[{IndexText(e.Index)}] {e.Path}: {e.Reason}"));

    private static string Format(IReadOnlyList<ExpectedError> errors) =>
        errors.Count == 0
            ? "(none)"
            : string.Join("; ", errors.Select(e => $"[{IndexText(e.Index)}] {e.Path}: {e.Reason}"));

    /// <summary>One verdict sidecar (fixtures/contracts/README.md).</summary>
    private sealed class Verdict
    {
        [JsonPropertyName("schema")]
        public string? Schema { get; set; }

        [JsonPropertyName("valid")]
        public bool Valid { get; set; }

        [JsonPropertyName("errors")]
        public IReadOnlyList<ExpectedError>? Errors { get; set; }
    }

    /// <summary>One expected <c>details[]</c> row of a reject verdict.</summary>
    private sealed class ExpectedError
    {
        [JsonPropertyName("index")]
        public int? Index { get; set; }

        [JsonPropertyName("path")]
        public string? Path { get; set; }

        [JsonPropertyName("reason")]
        public string? Reason { get; set; }
    }
}
