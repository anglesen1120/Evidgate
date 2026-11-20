using System.Text.Json.Nodes;

namespace Tenancy.IntegrationTests;

/// <summary>
/// Manifests whose planned executions each carry exactly one refusal: unclean text first (lowest indexes), then secrets, so
/// the verifier's "secrets fill the budget first" order differs from the index order.
/// </summary>
internal static class ManifestCapMixFixture
{
    internal const string Secret = "ghp_AAAAAAAAAAAA";

    /// <summary>Text that must never appear in an answer: the unclean value, the secret literals and the key name.</summary>
    internal static readonly string[] Echoes = ["\u200B", "\\u200B", "plain", "AAAAAAAAAAAA", "api_token"];

    internal static JsonObject Build(Guid suite, Guid caseRevision, int unclean, int secrets, bool hintSecret)
    {
        var manifest = ManifestUnitTests.Build(suite, [caseRevision], [new() { ["browser"] = "x" }], ["staging"]);
        var template = manifest["planned_executions"]![0]!.ToJsonString();
        var planned = new JsonArray();
        for (var row = 0; row < unclean + secrets; row++)
        {
            var node = JsonNode.Parse(template)!.AsObject();

            // An all-x browser of a different length per row keeps every row distinct without tripping the secret scan.
            var parameters = new JsonObject { ["browser"] = new string('x', row + 1) };
            if (row < unclean)
            {
                parameters["region"] = "a\u200Bb";
            }
            else
            {
                parameters["api_token"] = "plain";
            }

            node["variant"] = new JsonObject { ["params"] = parameters };
            planned.Add(node);
        }

        manifest["planned_executions"] = planned;
        manifest["shard_plan"]!["shards"]![0]!["to_index"] = planned.Count;
        if (hintSecret)
        {
            manifest["shard_plan"]!["shards"]![0]!["producer_hint"] = Secret;
        }

        return manifest;
    }
}
