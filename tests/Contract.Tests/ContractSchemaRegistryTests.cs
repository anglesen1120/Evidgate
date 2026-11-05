using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Evidgate.BuildingBlocks.Contracts.Validation;
using Evidgate.Modules.Ingestion.Contracts;
using Shouldly;
using Xunit;

namespace Contract.Tests;

/// <summary>
/// 04.7.1-T1.2/T1.4 and 05.1.1-T1.1/T1.4: the loader contract the two runtimes
/// share — recursive discovery of <c>contracts/{schemas,adapter/**,manifest}</c>,
/// keys that are never silently overwritten, cross-file <c>$ref</c>s resolved
/// from the loaded set only, the <c>x-max-bytes</c> cap — plus the two
/// registrations that must not drift from their schema files: the
/// <c>POST /v1/projects/{p}/manifests</c> body (05.1.1-T1.4) and the
/// adapter operations' reference to the 00 §9.4 envelope (04.7.1-T1.2).
/// </summary>
/// <remarks>
/// The verdict of a document that crosses a file boundary is pinned by the
/// <c>fixtures/contracts/vectors/execute.v1|collect.v1</c> corpus both runtimes
/// run (AC5/T42); these tests cover what a corpus cannot express: load-time
/// failures, the cap, and the spec-to-file binding.
/// </remarks>
public sealed class ContractSchemaRegistryTests
{
    private const string Dialect = "https://json-schema.org/draft/2020-12/schema";

    private static readonly Lazy<ContractSchemaSet> Schemas =
        new(() => ContractSchemaSet.Load(ContractPaths.ContractsDirectory));

    [Fact]
    public void Nested_Operations_Config_And_Manifest_Load_Under_Their_File_Name_Keys()
    {
        foreach (var key in new[]
        {
            "event-envelope.v1",
            "capability.v1",
            "config.v1",
            "discover.v1",
            "prepare.v1",
            "execute.v1",
            "collect.v1",
            "cleanup.v1",
            "manifest.v1",
        })
        {
            Schemas.Value.Contains(key).ShouldBeTrue(
                $"'{key}' must be discovered (contracts/schemas, contracts/adapter/** and contracts/manifest)");
        }

        // The two transport caps the plans decide: 04.7.1 §8 (≤ 16 KiB capability
        // document) and 04.1.3-T1's published 32 MiB manifest body.
        Schemas.Value.MaxBytes("capability.v1").ShouldBe(16384L);
        Schemas.Value.MaxBytes(ContractSchemaKeys.Manifest).ShouldBe(33_554_432L);
    }

    /// <summary>
    /// 13.1.1-T1.2: the manual test case <c>steps</c> document is discovered like every other schema
    /// (key = file name without <c>.json</c>, under <c>contracts/schemas</c>), its <c>$id</c> is its
    /// own file name, and it declares no transport cap — it is not a request body but the jsonb value
    /// carried inside one, whose 64 KiB cap belongs to the case route (plan 13.1.1 §8). The corpus the
    /// CLI's Ajv suite reads is asserted to exist here as well: a schema without vectors would leave the
    /// two runtimes unpinned.
    /// </summary>
    [Fact]
    public void AuthoringStepsSchema_IsDiscovered()
    {
        Schemas.Value.Contains(ContractSchemaKeys.AuthoringSteps).ShouldBeTrue(
            "'authoring-steps.v1' must be discovered under contracts/schemas (13.1.1 §3)");

        var file = Path.Combine(
            ContractPaths.ContractsDirectory,
            "schemas",
            ContractSchemaKeys.AuthoringSteps + ".json");
        using var schema = JsonDocument.Parse(File.ReadAllText(file));
        schema.RootElement.GetProperty("$schema").GetString().ShouldBe(Dialect);
        schema.RootElement.GetProperty("$id").GetString().ShouldBe(ContractSchemaKeys.AuthoringSteps + ".json");

        Schemas.Value.MaxBytes(ContractSchemaKeys.AuthoringSteps).ShouldBeNull(
            "the steps document is a jsonb value, not a body: the case route's 64 KiB cap is not the schema's");

        // The corpus both runtimes run (fixtures/contracts/README.md): one sidecar per vector.
        var corpus = Path.Combine(
            ContractPaths.FixturesDirectory,
            "vectors",
            ContractSchemaKeys.AuthoringSteps);
        Directory.Exists(corpus).ShouldBeTrue($"the '{ContractSchemaKeys.AuthoringSteps}' vector directory must exist");

        var vectors = Directory.EnumerateFiles(corpus, "*.json")
            .Where(vector => !vector.EndsWith(".verdict.json", StringComparison.Ordinal))
            .ToArray();
        vectors.ShouldNotBeEmpty("a schema without accept/reject vectors is not pinned by either runtime");
        foreach (var vector in vectors)
        {
            File.Exists(vector[..^".json".Length] + ".verdict.json")
                .ShouldBeTrue($"missing verdict sidecar for {Path.GetFileName(vector)}");
        }

        // The declared bounds, through the discovered key: 100 steps is accepted, 101 is not.
        string Steps(int count) => string.Join(
            ',',
            Enumerable.Range(1, count).Select(no => $$"""{"no":{{no}},"action":"Open the page","expected":"The page is shown"}"""));

        using var accepted = JsonDocument.Parse($$"""{"steps":[{{Steps(100)}}]}""");
        Schemas.Value.Validate(ContractSchemaKeys.AuthoringSteps, accepted.RootElement).IsValid
            .ShouldBeTrue("100 steps is the declared maximum (plan 13.1.1 §3)");

        using var refused = JsonDocument.Parse($$"""{"steps":[{{Steps(101)}}]}""");
        Schemas.Value.Validate(ContractSchemaKeys.AuthoringSteps, refused.RootElement).Errors
            .ShouldBe([new ContractValidationError(null, "/steps", "max_items")]);
    }

    /// <summary>
    /// 13.1.1-T5: the import/export table <c>authoring-import.v1</c> is discovered like every other schema (key = file
    /// name without <c>.json</c>), its <c>$id</c> is its own file name, and it declares no transport cap — an import
    /// arrives as CSV, XLSX or this document, so the 5 MiB cap and the 5 000-row cap are the route's (plan 13.1.1 §5
    /// route 25, AC16). The row cap is asserted here as well, because the CSV and XLSX readers build the same document
    /// and count against the schema's own bound. The steps shape a case row carries is inlined rather than referenced
    /// across files, so this test also pins it field for field to <c>authoring-steps.v1.json</c>: a case may not pass
    /// one contract and fail the other.
    /// </summary>
    [Fact]
    public void AuthoringImportSchema_IsDiscovered()
    {
        Schemas.Value.Contains(ContractSchemaKeys.AuthoringImport).ShouldBeTrue(
            "'authoring-import.v1' must be discovered under contracts/schemas (13.1.1-T5)");

        var file = Path.Combine(
            ContractPaths.ContractsDirectory,
            "schemas",
            ContractSchemaKeys.AuthoringImport + ".json");
        using var schema = JsonDocument.Parse(File.ReadAllText(file));
        schema.RootElement.GetProperty("$schema").GetString().ShouldBe(Dialect);
        schema.RootElement.GetProperty("$id").GetString().ShouldBe(ContractSchemaKeys.AuthoringImport + ".json");

        Schemas.Value.MaxBytes(ContractSchemaKeys.AuthoringImport).ShouldBeNull(
            "an import is CSV, XLSX or this document: the 5 MiB cap is the route's, not the schema's");

        schema.RootElement.GetProperty("properties").GetProperty("rows").GetProperty("maxItems").GetInt32()
            .ShouldBe(5000, "the 5 000-row cap of AC16 is declared here too (plan 13.1.1 §5 route 25)");

        // The declared shape, through the discovered key: a module and a case are accepted, an undeclared row member is
        // not, and the steps member of a case is bounded by the same 100 steps as authoring-steps.v1.
        string Steps(int count) => string.Join(
            ',',
            Enumerable.Range(1, count).Select(no => $$"""{"no":{{no}},"action":"Open the page","expected":"The page is shown"}"""));

        // Three dollars: the case row closes two objects in a row, and a "}}" run is content here, not an interpolation.
        using var accepted = JsonDocument.Parse(
            $$$"""
            {"format":"authoring-import","version":1,"rows":[
              {"kind":"node","key":"n1","node_kind":"module","title":"Shop","position":0},
              {"kind":"item","key":"i1","parent_key":"n1","title":"Pay","priority":"high"},
              {"kind":"case","key":"c1","item_key":"i1","title":"Declined card","priority":"high","steps":{"steps":[{{{Steps(100)}}}]}}
            ]}
            """);
        Schemas.Value.Validate(ContractSchemaKeys.AuthoringImport, accepted.RootElement).IsValid
            .ShouldBeTrue("a node, an item and a case with 100 steps are the declared shape (plan 13.1.1 §3)");

        // The conditional row selection must keep the leaf, not collapse the row: the importer answers steps_invalid by
        // reading a "/rows/N/steps/…" instance path (the mapper drops only 'if' rows and reporting-composite subtrees,
        // ContractErrorMapper.cs:138-143), so a row-level "/rows/N" error would become value_invalid instead.
        using var overlongSteps = JsonDocument.Parse(
            $$$"""
            {"format":"authoring-import","version":1,"rows":[
              {"kind":"case","key":"c1","item_key":"i1","title":"Declined card","priority":"high","steps":{"steps":[{{{Steps(101)}}}]}}
            ]}
            """);
        var overlongResult = Schemas.Value.Validate(ContractSchemaKeys.AuthoringImport, overlongSteps.RootElement);
        overlongResult.IsValid.ShouldBeFalse("101 steps exceed the 100-step bound of the steps contract (plan 13.1.1 §3)");
        overlongResult.Errors.ShouldContain(
            error => error.Path.StartsWith("/rows/0/steps", StringComparison.Ordinal),
            "a steps violation keeps its row-scoped instance path, which the importer reports as steps_invalid at /steps");

        using var badStep = JsonDocument.Parse(
            $$$"""
            {"format":"authoring-import","version":1,"rows":[
              {"kind":"case","key":"c1","item_key":"i1","title":"Declined card","priority":"high","steps":{"steps":[{"no":1,"expected":"the payment is refused"}]}}
            ]}
            """);
        var badStepResult = Schemas.Value.Validate(ContractSchemaKeys.AuthoringImport, badStep.RootElement);
        badStepResult.IsValid.ShouldBeFalse("a step without an action breaks the steps contract, so the row is refused");
        badStepResult.Errors.ShouldContain(
            error => error.Path.StartsWith("/rows/0/steps", StringComparison.Ordinal),
            "a required member inside a step is still a row-scoped steps violation, not a row-level one");

        using var refused = JsonDocument.Parse(
            """{"format":"authoring-import","version":1,"rows":[{"kind":"node","key":"n1","node_kind":"module","title":"Shop","bogus":1}]}""");
        Schemas.Value.Validate(ContractSchemaKeys.AuthoringImport, refused.RootElement).IsValid
            .ShouldBeFalse("a row member the contract does not declare is refused (additionalProperties: false)");

        // The inlined steps shape equals authoring-steps.v1.json's: same root members, same array bounds, same step
        // members and the same bounds on each.
        using var steps = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            ContractPaths.ContractsDirectory,
            "schemas",
            ContractSchemaKeys.AuthoringSteps + ".json")));
        var theirs = steps.RootElement.GetProperty("properties").GetProperty("steps");
        var mine = schema.RootElement.GetProperty("$defs").GetProperty("steps").GetProperty("properties").GetProperty("steps");

        mine.GetProperty("minItems").GetInt32().ShouldBe(theirs.GetProperty("minItems").GetInt32());
        mine.GetProperty("maxItems").GetInt32().ShouldBe(theirs.GetProperty("maxItems").GetInt32());
        mine.GetProperty("items").GetProperty("required").GetRawText()
            .ShouldBe(theirs.GetProperty("items").GetProperty("required").GetRawText());
        mine.GetProperty("items").GetProperty("additionalProperties").GetBoolean()
            .ShouldBe(theirs.GetProperty("items").GetProperty("additionalProperties").GetBoolean());

        foreach (var field in new[] { "no", "action", "expected", "data" })
        {
            var mineField = mine.GetProperty("items").GetProperty("properties").GetProperty(field);
            var theirField = theirs.GetProperty("items").GetProperty("properties").GetProperty(field);

            foreach (var bound in new[] { "type", "minLength", "maxLength", "minimum", "maximum" })
            {
                var mineHas = mineField.TryGetProperty(bound, out var mineValue);
                var theirHas = theirField.TryGetProperty(bound, out var theirValue);

                mineHas.ShouldBe(theirHas, $"'{field}.{bound}' must be declared by both steps shapes");
                if (mineHas)
                {
                    mineValue.GetRawText().ShouldBe(theirValue.GetRawText(), $"'{field}.{bound}' must not drift");
                }
            }
        }
    }

    [Fact]
    public void Byte_Cap_Rejects_A_Body_Before_It_Is_Parsed()
    {
        var overCap = new byte[Schemas.Value.MaxBytes(ContractSchemaKeys.Manifest)!.Value + 1];

        var result = Schemas.Value.Validate(ContractSchemaKeys.Manifest, overCap);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldBe([ContractValidationError.BodyTooLarge]);
    }

    [Fact]
    public void Duplicate_Keys_Fail_Loud_Instead_Of_Last_One_Wins()
    {
        using var tree = new SchemaTree();
        var key = $"dup-{Guid.NewGuid():N}.v1";
        tree.Write($"schemas/{key}.json", MinimalSchema(key));
        tree.Write($"adapter/operations/{key}.json", MinimalSchema(key));

        var exception = Should.Throw<InvalidOperationException>(() => ContractSchemaSet.Load(tree.Root));

        exception.Message.ShouldContain($"duplicate contract schema key '{key}'");
        exception.Message.ShouldContain("schemas");
        exception.Message.ShouldContain("operations");
    }

    [Fact]
    public void A_Ref_Outside_The_Loaded_Set_Fails_At_Load()
    {
        using var tree = new SchemaTree();
        var absent = $"absent-{Guid.NewGuid():N}.v1";
        var key = $"dangling-{Guid.NewGuid():N}.v1";
        tree.Write(
            $"schemas/{key}.json",
            $$"""{"$schema":"{{Dialect}}","$id":"{{key}}.json","$ref":"urn:evidgate:contract:{{absent}}"}""");

        var exception = Record.Exception(() => ContractSchemaSet.Load(tree.Root));

        // No file-system and no HTTP resolution: the registry is exactly the
        // loaded set, so an unresolved address is a load failure, never a fetch.
        exception.ShouldNotBeNull("a $ref outside the loaded set must fail at load, not at first use");
        exception!.Message.ShouldContain(absent);
    }

    /// <summary>
    /// 05.1.1-T1.4: the spec body and the schema file cannot drift. The path's
    /// request body references <c>contracts/manifest/&lt;file&gt;</c>, that file's
    /// <c>$id</c> is its own name, the key both loaders derive from it is
    /// <see cref="ContractSchemaKeys.Manifest"/>, and the empty inline stub the
    /// reference replaced must not come back (a mirror could drift; a reference
    /// cannot).
    /// </summary>
    [Fact]
    public void Manifest_Body_In_The_Spec_Is_The_Schema_File()
    {
        var specPath = Path.Combine(ContractPaths.ContractsDirectory, "openapi.yaml");
        var lines = File.ReadAllLines(specPath);
        var start = Array.IndexOf(lines, "  /projects/{p}/manifests:");
        start.ShouldBeGreaterThanOrEqualTo(0, "the 00 §9.3 manifest endpoint must stay documented");

        var end = lines.Length;
        for (var index = start + 1; index < lines.Length; index++)
        {
            if (Regex.IsMatch(lines[index], @"^  /\S"))
            {
                end = index;
                break;
            }
        }

        var block = string.Join('\n', lines[start..end]);
        block.Contains("operationId: createManifest", StringComparison.Ordinal)
            .ShouldBeTrue($"the sliced block must be the manifest operation, got:\n{block}");

        var reference = Regex.Match(block, @"^\s+\$ref: '\./manifest/(?<file>[^']+)'$", RegexOptions.Multiline);
        reference.Success.ShouldBeTrue($"the manifest request body must $ref contracts/manifest/*.json:\n{block}");

        var file = reference.Groups["file"].Value;
        var schemaFile = Path.Combine(ContractPaths.ContractsDirectory, "manifest", file);
        File.Exists(schemaFile).ShouldBeTrue($"'{file}' is referenced by the spec but does not exist");

        using var document = JsonDocument.Parse(File.ReadAllText(schemaFile));
        document.RootElement.GetProperty("$id").GetString().ShouldBe(file);
        Path.GetFileNameWithoutExtension(file).ShouldBe(ContractSchemaKeys.Manifest);

        File.ReadAllText(specPath)
            .Contains("ManifestUpload", StringComparison.Ordinal)
            .ShouldBeFalse(
                "the empty ManifestUpload stub must stay deleted: the body is the schema file, never an inline mirror");
    }

    /// <summary>
    /// The schema admits every count the configured caps may be raised to, so the runtime cap — not a schema bound —
    /// is what refuses a plan over its configured value. The 256-shard bound is the schema's own. Validated as a
    /// parsed document because the transport cap (32 MiB) would otherwise fire first on the execution axis: a body
    /// that large is a 413, not a <c>max_items</c> row, which is why the execution axis is asserted at its declared
    /// value and the document is materialised on the shard axis.
    /// </summary>
    [Fact]
    public void Manifest_Bounds_Are_Encoded_In_The_Schema()
    {
        ExecutionBound().ShouldBe(IngestionLimits.MaxManifestExecutionsCeiling, "the planned-execution bound is the configurable ceiling");

        var matrix = ManifestSchema("properties")["matrix"]!.AsObject()["properties"]!.AsObject();
        matrix["case_revision_ids"]!.AsObject()["maxItems"]!.GetValue<int>()
            .ShouldBe(IngestionLimits.MaxManifestExecutionsCeiling, "the matrix's case bound is the same ceiling");
        matrix["variants"]!.AsObject()["maxItems"]!.GetValue<int>()
            .ShouldBe(IngestionLimits.MaxManifestVariantsCeiling, "the variant axis admits the configurable ceiling");

        var shard = ManifestSchema("$defs")["shard"]!.AsObject()["properties"]!.AsObject();
        shard["from_index"]!.AsObject()["maximum"]!.GetValue<int>()
            .ShouldBe(IngestionLimits.MaxManifestExecutionsCeiling - 1, "a shard starts inside the largest plan");
        shard["to_index"]!.AsObject()["maximum"]!.GetValue<int>()
            .ShouldBe(IngestionLimits.MaxManifestExecutionsCeiling, "a shard ends at the largest plan's count");

        AtLimit(1, 256).IsValid.ShouldBeTrue("256 shards are the declared bound");
        AtLimit(1, 257).Errors.ShouldBe(
            [new ContractValidationError(null, "/shard_plan/shards", "max_items")]);
    }

    /// <summary>The schema's declared bound on <c>planned_executions</c>.</summary>
    private static int ExecutionBound() =>
        ManifestSchema("properties")["planned_executions"]!.AsObject()["maxItems"]!.GetValue<int>();

    /// <summary>One section of the manifest schema as a JSON object.</summary>
    private static JsonObject ManifestSchema(string section) =>
        JsonNode.Parse(File.ReadAllText(
            Path.Combine(ContractPaths.ContractsDirectory, "manifest", "manifest.v1.json")))!
            .AsObject()[section]!.AsObject();

    /// <summary>Validates (as a parsed body) a manifest with exactly these counts.</summary>
    private static ContractValidationResult AtLimit(int plannedExecutions, int shards)
    {
        const string execution =
            """{"case_revision_id":"00000000-0000-0000-0000-000000000010","variant":{"params":{}},"target_env":"e"}""";
        const string shard = """{"shard_id":"s","from_index":0,"to_index":0}""";
        var text = $$$"""
            {"schema_version":1,"suite_revision_id":"00000000-0000-0000-0000-000000000020","planned_executions":[{{{string.Join(',', Enumerable.Repeat(execution, plannedExecutions))}}}],"matrix":{"case_revision_ids":[],"variants":[],"target_envs":[]},"shard_plan":{"shards":[{{{string.Join(',', Enumerable.Repeat(shard, shards))}}}]},"env_needs":{"target_envs":[]}}
            """;

        using var document = JsonDocument.Parse(text);
        return Schemas.Value.Validate(ContractSchemaKeys.Manifest, document.RootElement);
    }

    /// <summary>
    /// The corpus the spec's own body definition must agree with: the golden
    /// vector is accepted and a reject vector is rejected, each exactly as its
    /// sidecar says, when validated through the key derived from
    /// <c>openapi.yaml</c> rather than a hard-coded one. The per-vector theory
    /// that also runs these files is <see cref="ParityCorpusTests"/>.
    /// </summary>
    [Theory]
    [InlineData("acc-01-golden.json")]
    [InlineData("rej-11-matrix-duplicate-variant.json")]
    public void ManifestSchema_ParityCorpus(string vector)
    {
        var key = SpecManifestKey();
        var file = Path.Combine(ContractPaths.FixturesDirectory, "vectors", key, vector);
        var sidecar = JsonDocument.Parse(File.ReadAllText(file[..^".json".Length] + ".verdict.json")).RootElement;
        sidecar.GetProperty("schema").GetString().ShouldBe(key);

        var result = Schemas.Value.Validate(key, File.ReadAllBytes(file));

        result.IsValid.ShouldBe(sidecar.GetProperty("valid").GetBoolean(), vector);
    }

    /// <summary>The manifest schema key as the spec's body reference derives it (file name without <c>.json</c>).</summary>
    private static string SpecManifestKey()
    {
        var spec = File.ReadAllText(Path.Combine(ContractPaths.ContractsDirectory, "openapi.yaml"));
        var reference = Regex.Match(spec, @"\$ref: '\./manifest/(?<file>[^']+)'");
        reference.Success.ShouldBeTrue("the manifest request body must $ref contracts/manifest/*.json");
        return Path.GetFileNameWithoutExtension(reference.Groups["file"].Value);
    }

    private static string MinimalSchema(string key) =>
        $$"""{"$schema":"{{Dialect}}","$id":"{{key}}.json","type":"object"}""";

    /// <summary>A throwaway <c>contracts/</c> tree; the directory name is random so the key can be too.</summary>
    private sealed class SchemaTree : IDisposable
    {
        public SchemaTree()
        {
            Root = Path.Combine(Path.GetTempPath(), $"contract-tree-{Guid.NewGuid():N}", "contracts");
        }

        public string Root { get; }

        public void Write(string relativePath, string content)
        {
            var path = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public void Dispose()
        {
            var directory = Directory.GetParent(Root);
            if (directory is not null && directory.Exists)
            {
                directory.Delete(recursive: true);
            }
        }
    }
}
