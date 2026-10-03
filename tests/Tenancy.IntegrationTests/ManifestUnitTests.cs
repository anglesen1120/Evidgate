// Test inputs are written inline so each case reads as the document it checks.
#pragma warning disable CA1861
using System.Text.Json;
using System.Text.Json.Nodes;
using Evidgate.Modules.Orchestrator.Contracts;
using Evidgate.Modules.Orchestrator.Domain;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>The pure manifest checks of 05.1.1-T2.3/T2.4 and the environment-revision rules of 05.1.2 AC2/AC3.</summary>
public sealed class ManifestUnitTests
{
    private static readonly Guid Suite = Guid.Parse("00000000-0000-0000-0000-000000000020");
    private static readonly Guid CaseA = Guid.Parse("00000000-0000-0000-0000-000000000010");
    private static readonly Guid CaseB = Guid.Parse("00000000-0000-0000-0000-000000000011");

    /// <summary>A manifest whose planned executions are the full product of the axes, in one shard per execution.</summary>
    internal static JsonObject Build(Guid suite, Guid[] cases, Dictionary<string, string>[] variants, string[] envs, int shards = 1)
    {
        var planned = new JsonArray();
        foreach (var caseId in cases)
        {
            foreach (var variant in variants)
            {
                foreach (var env in envs)
                {
                    planned.Add(new JsonObject
                    {
                        ["case_revision_id"] = caseId.ToString(),
                        ["variant"] = new JsonObject { ["params"] = Params(variant) },
                        ["target_env"] = env,
                    });
                }
            }
        }

        var count = planned.Count;
        var shardArray = new JsonArray();
        var size = Math.Max(1, (int)Math.Ceiling(count / (double)shards));
        for (var from = 0; from < count || shardArray.Count == 0; from += size)
        {
            shardArray.Add(new JsonObject { ["shard_id"] = $"s{shardArray.Count}", ["from_index"] = from, ["to_index"] = Math.Min(count, from + size) });
            if (count == 0)
            {
                break;
            }
        }

        return new JsonObject
        {
            ["schema_version"] = 1,
            ["suite_revision_id"] = suite.ToString(),
            ["planned_executions"] = planned,
            ["matrix"] = new JsonObject
            {
                ["case_revision_ids"] = new JsonArray([.. cases.Select(id => (JsonNode)id.ToString())]),
                ["variants"] = new JsonArray([.. variants.Select(variant => (JsonNode)new JsonObject { ["params"] = Params(variant) })]),
                ["target_envs"] = new JsonArray([.. envs.Select(env => (JsonNode)env)]),
            },
            ["shard_plan"] = new JsonObject { ["shards"] = shardArray },
            ["env_needs"] = new JsonObject { ["target_envs"] = new JsonArray([.. envs.Select(env => (JsonNode)new JsonObject { ["target_env"] = env })]) },
        };

        static JsonObject Params(Dictionary<string, string> variant) => new([.. variant.Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)pair.Value))]);
    }

    private static JsonObject Golden() => Build(
        Suite,
        [CaseA, CaseB],
        [new() { ["browser"] = "chromium" }, new() { ["browser"] = "firefox" }],
        ["staging"],
        shards: 2);

    private static (ManifestFacts? Facts, ManifestRefusal? Refusal) Verify(JsonNode manifest)
    {
        using var document = JsonDocument.Parse(manifest.ToJsonString());

        return ManifestVerifier.Verify(document.RootElement.Clone());
    }

    [Fact]
    public void ACompleteManifest_IsAccepted_WithItsFacts()
    {
        var (facts, refusal) = Verify(Golden());

        refusal.ShouldBeNull();
        facts!.PlannedCount.ShouldBe(4);
        facts.CaseRevisionIds.ShouldBe([CaseA, CaseB]);
        facts.SuiteRevisionId.ShouldBe(Suite);
        facts.EnvironmentRevisionId.ShouldBeNull();
    }

    [Fact]
    public void TheCommittedGoldenFixtures_AreAccepted()
    {
        foreach (var name in new[] { "acc-01-golden.json", "acc-02-declared-empty.json" })
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(FixtureRoot(), name)));
            ManifestVerifier.Verify(document.RootElement).Refusal.ShouldBeNull(name);
        }
    }

    [Fact]
    public void ADeclaredEmptyMatrix_IsAccepted_ButCasesWithNoVariantOrEnv_AreNot()
    {
        Verify(Build(Suite, [], [], [])).Refusal.ShouldBeNull();

        var noVariants = Build(Suite, [CaseA], [], ["staging"]);
        var refusal = Verify(noVariants).Refusal!;
        refusal.Reason.ShouldBe("matrix_not_covered");
        refusal.Details.Select(detail => detail.Path).ShouldContain("/matrix/variants");
    }

    [Fact]
    public void AMissingTuple_IsNotCovered()
    {
        var manifest = Golden();
        manifest["planned_executions"]!.AsArray().RemoveAt(3);
        manifest["shard_plan"]!["shards"]![1]!["to_index"] = 3;

        var refusal = Verify(manifest).Refusal!;

        refusal.Reason.ShouldBe("matrix_not_covered");
        refusal.Details.Select(detail => detail.Path).ShouldContain("/planned_executions");
    }

    [Fact]
    public void ADuplicateOrForeignTuple_IsNamedByIndex()
    {
        var manifest = Golden();
        manifest["planned_executions"]![3] = manifest["planned_executions"]![0]!.DeepClone();
        manifest["planned_executions"]![2]!["target_env"] = "prod";

        var refusal = Verify(manifest).Refusal!;

        refusal.Reason.ShouldBe("matrix_not_covered");
        refusal.Details.Where(detail => detail.Index is not null).Select(detail => detail.Index).ShouldBe([2, 3], ignoreOrder: true);
    }

    [Fact]
    public void VariantKeyOrder_DoesNotMatter_ForCoverage()
    {
        var manifest = Build(Suite, [CaseA], [new() { ["a"] = "1", ["b"] = "2" }], ["staging"]);
        manifest["planned_executions"]![0]!["variant"]!["params"] = new JsonObject { ["b"] = "2", ["a"] = "1" };

        Verify(manifest).Refusal.ShouldBeNull();
    }

    [Theory]
    [InlineData(0, 3, 3, 4)] // a partition
    [InlineData(0, 2, 1, 4)] // overlap
    [InlineData(0, 2, 3, 4)] // gap
    [InlineData(1, 2, 2, 4)] // does not start at 0
    [InlineData(0, 2, 2, 3)] // stops short of the planned count
    public void TheShardPlan_MustPartitionThePlan(int from0, int to0, int from1, int to1)
    {
        var manifest = Golden();
        manifest["shard_plan"] = new JsonObject
        {
            ["shards"] = new JsonArray(
                new JsonObject { ["shard_id"] = "a", ["from_index"] = from0, ["to_index"] = to0 },
                new JsonObject { ["shard_id"] = "b", ["from_index"] = from1, ["to_index"] = to1 }),
        };

        var refusal = Verify(manifest).Refusal;

        var partitions = from0 == 0 && to0 == from1 && to1 == 4;
        (refusal is null).ShouldBe(partitions);
        if (!partitions)
        {
            refusal!.Reason.ShouldBe("matrix_not_covered");
        }
    }

    [Fact]
    public void AShardIdUsedTwice_OrAnEmptyShardInANonEmptyPlan_IsRefused()
    {
        var twice = Golden();
        twice["shard_plan"]!["shards"]![1]!["shard_id"] = "s0";
        Verify(twice).Refusal!.Reason.ShouldBe("matrix_not_covered");

        var empty = Golden();
        empty["shard_plan"]!["shards"]!.AsArray().Add(new JsonObject { ["shard_id"] = "idle", ["from_index"] = 4, ["to_index"] = 4 });
        Verify(empty).Refusal!.Reason.ShouldBe("matrix_not_covered");
    }

    [Fact]
    public void EnvNeeds_ForAnUnknownOrRepeatedEnvironment_AreRefused()
    {
        var manifest = Golden();
        manifest["env_needs"]!["target_envs"]!.AsArray().Add(new JsonObject { ["target_env"] = "prod" });

        var refusal = Verify(manifest).Refusal!;

        refusal.Reason.ShouldBe("matrix_not_covered");
        refusal.Details.Single().Path.ShouldBe("/env_needs/target_envs/1");
    }

    [Fact]
    public void ASecretLiteral_IsRefused_ByPosition_WithoutEchoingIt()
    {
        const string token = "ghp_abcdefghijklmnopqrstuvwxyz0123456789";
        var manifest = Build(Suite, [CaseA], [new() { ["browser"] = "chromium", ["api_token"] = "plain", ["mode"] = token }], ["staging"]);

        var refusal = Verify(manifest).Refusal!;

        refusal.Reason.ShouldBe("secret_in_variant_params");
        refusal.Details.Select(detail => (detail.Path, detail.Index)).ShouldBe(
        [
            ("/planned_executions/0/variant/params/#2", 0),
            ("/planned_executions/0/variant/params/#3", 0),
            ("/matrix/variants/0/params/#2", null),
            ("/matrix/variants/0/params/#3", null),
        ]);
        JsonSerializer.Serialize(refusal).ShouldNotContain(token);
        JsonSerializer.Serialize(refusal).ShouldNotContain("api_token");
    }

    [Theory]
    [InlineData("api_token", "secret:ci_token", false)]
    [InlineData("password", "secret:db.password", false)]
    [InlineData("browser", "chromium", false)]
    [InlineData("locale", "en-US", false)]
    [InlineData("password", "hunter2", true)]
    [InlineData("mode", "secret:ghp_abcdefghijklmnopqrstuvwxyz0123456789", true)]
    [InlineData("mode", "secret:has space", true)]
    [InlineData("mode", "Bearer abcdefghijklmnop", true)]
    [InlineData("ghp_abcdefghijklmnopqrstuvwxyz0123456789", "x", true)]
    public void IsSecret_AllowsReferences_AndRefusesLiterals(string key, string value, bool expected) =>
        ManifestVerifier.IsSecret(key, value).ShouldBe(expected);

    [Fact]
    public void ARefusal_CarriesAtMostFiftyDetails()
    {
        var variants = Enumerable.Range(0, 60).Select(i => new Dictionary<string, string> { ["password"] = $"pw{i}" }).ToArray();
        var manifest = Build(Suite, [CaseA], variants, ["staging"]);

        Verify(manifest).Refusal!.Details.Count.ShouldBe(ManifestVerifier.MaxDetails);
    }

    [Fact]
    public void UnstorableText_IsDetected_OnlyWhereItReallyIs()
    {
        using var nul = JsonDocument.Parse("""{"a":["x","b\u0000"]}""");
        using var lone = JsonDocument.Parse("""{"a":"x\ud800y"}""");
        using var loneKey = JsonDocument.Parse("""{"\udc00":"x"}""");
        using var escaped = JsonDocument.Parse("""{"a":"\\u0000 is text"}""");
        using var pair = JsonDocument.Parse("""{"a":"\ud83d\ude00"}""");

        JsonBody.HasUnstorableText(nul.RootElement).ShouldBeTrue();
        JsonBody.HasUnstorableText(lone.RootElement).ShouldBeTrue();
        JsonBody.HasUnstorableText(loneKey.RootElement).ShouldBeTrue();
        JsonBody.HasUnstorableText(escaped.RootElement).ShouldBeFalse();
        JsonBody.HasUnstorableText(pair.RootElement).ShouldBeFalse();
    }

    [Theory]
    [InlineData("/planned_executions/0/variant/params/ghp_abcdefghijklmnop", "type", "/planned_executions/0/variant/params/*")]
    [InlineData("/dependency_versions/AKIAABCDEFGHIJKLMNOP", "max_length", "/dependency_versions/*")]
    [InlineData("/base_urls/web", "type", "/base_urls/*")]
    [InlineData("/password", "additional_properties", "")]
    [InlineData("/planned_executions/3/target_env", "required", "/planned_executions/3/target_env")]
    public void SchemaErrorPaths_NeverNameCallerKeys(string path, string reason, string expected) =>
        JsonBody.SafePath(new Evidgate.BuildingBlocks.Contracts.Validation.ContractValidationError(null, path, reason)).ShouldBe(expected);

    [Fact]
    public void SecretsInEnvNeedsRefs_AndProducerHints_AreRefused()
    {
        const string token = "ghp_abcdefghijklmnopqrstuvwxyz0123456789";
        var manifest = Golden();
        manifest["env_needs"]!["target_envs"]![0]!["secret_refs"] = new JsonArray("secret:fine", $"secret:{token}");
        manifest["shard_plan"]!["shards"]![1]!["producer_hint"] = token;

        var refusal = Verify(manifest).Refusal!;

        refusal.Reason.ShouldBe("secret_value_detected");
        refusal.Details.Select(detail => detail.Path).ShouldBe(["/env_needs/target_envs/0/secret_refs/1", "/shard_plan/shards/1/producer_hint"]);
        JsonSerializer.Serialize(refusal).ShouldNotContain(token);
    }

    [Fact]
    public void ADuplicateMatrixCase_IsNotCovered()
    {
        var manifest = Build(Suite, [CaseA, CaseA], [new() { ["browser"] = "x" }], ["staging"]);

        var refusal = Verify(manifest).Refusal!;

        refusal.Reason.ShouldBe("matrix_not_covered");
        refusal.Details.Select(detail => detail.Path).ShouldContain("/matrix/case_revision_ids/1");
    }

    [Fact]
    public void VariantKeys_CannotCollide_ThroughSeparatorCharacters()
    {
        using var a = JsonDocument.Parse("""{"a":"x\u0001b","c":"y"}""");
        using var b = JsonDocument.Parse("""{"a":"x","b":"\u0001c"}""");

        ManifestVerifier.VariantKey(a.RootElement).ShouldNotBe(ManifestVerifier.VariantKey(b.RootElement));
    }

    // ------------------------------------------------------------------ environment revisions

    private static CreateEnvironmentRevisionRequest Env(
        Dictionary<string, string>? urls = null,
        Dictionary<string, string>? versions = null,
        string[]? refs = null) =>
        new(null, null, urls ?? new() { ["web"] = "https://staging.example.test" }, versions ?? new() { ["postgres"] = "18.0" }, refs);

    [Fact]
    public void EnvironmentHash_IsStable_AcrossOrderAndNameAndRefOrder()
    {
        var a = EnvironmentRevisionRules.Check(new CreateEnvironmentRevisionRequest(
            "Staging",
            null,
            new Dictionary<string, string> { ["web"] = "https://a.test", ["api"] = "https://b.test" },
            new Dictionary<string, string> { ["x"] = "1", ["y"] = "2" },
            ["secret:b", "secret:a"])).Configuration!;
        var b = EnvironmentRevisionRules.Check(new CreateEnvironmentRevisionRequest(
            "Another name",
            3,
            new Dictionary<string, string> { ["api"] = "https://b.test", ["web"] = "https://a.test" },
            new Dictionary<string, string> { ["y"] = "2", ["x"] = "1" },
            ["secret:a", "secret:b", "secret:a"])).Configuration!;

        a.RevisionHash.ShouldBe(b.RevisionHash);
        a.RevisionHash.ShouldMatch("^sha256:[0-9a-f]{64}$");
        b.SecretRefs.ShouldBe(["secret:a", "secret:b"]);
        EnvironmentRevisionRules.Check(Env(versions: new() { ["postgres"] = "18.1" })).Configuration!.RevisionHash.ShouldNotBe(a.RevisionHash);
    }

    [Fact]
    public void EnvironmentHash_MatchesTheGoldenVector()
    {
        // sha256 over {"base_urls":{"web":"https://staging.example.test"},"dependency_versions":{"postgres":"18.0"},"secret_refs":[]}
        var expected = "sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("""{"base_urls":{"web":"https://staging.example.test"},"dependency_versions":{"postgres":"18.0"},"secret_refs":[]}""")));

        EnvironmentRevisionRules.Check(Env()).Configuration!.RevisionHash.ShouldBe(expected);
    }

    [Theory]
    [InlineData("https://user:pw@staging.example.test")]
    [InlineData("https://staging.example.test/?token=abc")]
    [InlineData("https://staging.example.test/?api_key=abc")]
    [InlineData("https://staging.example.test/ghp_abcdefghijklmnopqrstuvwxyz0123456789/x")]
    [InlineData("https://staging.example.test/#access_token=abc")]
    [InlineData("https://ghp_abcdefghijklmnopqrstuvwxyz0123456789.example.test/")]
    [InlineData("ghp_abcdefghijklmnopqrstuvwxyz0123456789")]
    public void ASecretInABaseUrl_IsDetected_WithoutEcho(string url)
    {
        var (configuration, details) = EnvironmentRevisionRules.Check(Env(urls: new() { ["web"] = url }));

        configuration.ShouldBeNull();
        details.Single().ShouldBe(new Evidgate.BuildingBlocks.SharedKernel.Api.ErrorDetail("/base_urls/#1", "secret_value_detected"));
    }

    [Theory]
    [InlineData("ftp://staging.example.test")]
    [InlineData("staging.example.test")]
    [InlineData("/relative")]
    public void ABaseUrl_MustBeAnAbsoluteHttpUrl(string url) =>
        EnvironmentRevisionRules.Check(Env(urls: new() { ["web"] = url })).Details.Single().Reason.ShouldBe("invalid_url");

    [Fact]
    public void PlainUrlsAndVersions_AreAccepted()
    {
        var (configuration, details) = EnvironmentRevisionRules.Check(Env(
            urls: new() { ["web"] = "https://staging.example.test/app?locale=en&page=2", ["api"] = "http://10.0.0.5:8080/v2" },
            versions: new() { ["@acme/payments"] = "4.2.1", ["chromium"] = "131.0.6778.33" },
            refs: ["secret:db_password"]));

        details.ShouldBeEmpty();
        configuration.ShouldNotBeNull();
    }

    [Theory]
    [InlineData("db_password")]
    [InlineData("secret:")]
    [InlineData("secret:ghp_abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("secret:hunter2 with space")]
    public void ASecretRef_ThatIsNotAReference_IsASecretValue(string reference)
    {
        var details = EnvironmentRevisionRules.Check(Env(refs: ["secret:fine", reference])).Details;

        details.Single().ShouldBe(new Evidgate.BuildingBlocks.SharedKernel.Api.ErrorDetail("/secret_refs/1", "secret_value_detected"));
    }

    [Theory]
    [InlineData("postgres", "Bearer abcdefghijklmnopqrstuvwxyz")]
    [InlineData("api_token", "1.0")]
    [InlineData("postgres", "0123456789abcdef0123456789abcdef")]
    public void ASecretInADependencyVersion_IsDetected(string name, string version) =>
        EnvironmentRevisionRules.Check(Env(versions: new() { [name] = version })).Details.Single().Reason.ShouldBe("secret_value_detected");

    [Fact]
    public void ANulCharacter_IsAnInvalidValue() =>
        EnvironmentRevisionRules.Check(Env(versions: new() { ["postgres"] = "18\0" })).Details.Single().Reason.ShouldBe("invalid_value");

    private static string FixtureRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "fixtures", "contracts")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory!.FullName, "fixtures", "contracts", "vectors", "manifest.v1");
    }
}
