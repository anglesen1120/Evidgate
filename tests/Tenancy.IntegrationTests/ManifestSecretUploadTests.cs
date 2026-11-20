using System.Net;
using System.Text;
using System.Text.Json;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>The manifest upload refuses a secret that only shows after the enum-like fold or inside an array, without echoing it.</summary>
[Trait("Category", "Docker")]
[Collection(ExclusiveTenancySuite.Name)]
public sealed class ManifestSecretUploadTests
{
    private readonly PostgresTenancyFixture _fixture;

    public ManifestSecretUploadTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private static async Task<ApiAnswer> UploadAsync(ManifestTests.World world, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/projects/{world.Project}/manifests");
        world.Admin(request.Headers);
        request.Headers.Add("Idempotency-Key", NewKey());
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await world.Client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(string.IsNullOrEmpty(text) ? "{}" : text);

        return new ApiAnswer(response.StatusCode, document.RootElement.Clone(), response.Headers);
    }

    [Fact]
    public async Task UpperCasedTokenPrefixUnderAnEnumLikeKey_Is422_PositionOnly()
    {
        await using var world = await ManifestTests.StartAsync(_fixture);
        var manifest = ManifestUnitTests.Build(world.SuiteRevision, world.Cases, [new() { ["browser"] = "x", ["mode"] = "GHP_AAAAAAAA" }], ["staging"]);

        var answer = await UploadAsync(world, manifest.ToJsonString());

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var details = answer.Body.GetProperty("error").GetProperty("details").EnumerateArray().ToList();
        details.Select(detail => detail.GetProperty("reason").GetString()).Distinct().ShouldBe(["secret_in_variant_params"]);
        details.First().GetProperty("path").GetString().ShouldBe("/planned_executions/0/variant/params/#2");
        answer.Body.GetRawText().ShouldNotContain("GHP_AAAAAAAA", Case.Insensitive);
    }

    /// <summary>Parameter values are strings by contract, so an array never reaches the secret check: the schema refuses it first, without echoing it.</summary>
    [Fact]
    public async Task ArrayParamCarryingASecret_IsRefusedBySchema_NoEcho()
    {
        await using var world = await ManifestTests.StartAsync(_fixture);
        const string token = "ghp_AAAAAAAAAAAAAAAAAAAA";
        var manifest = ManifestUnitTests.Build(world.SuiteRevision, world.Cases, [new() { ["browser"] = "x" }], ["staging"]);
        manifest["planned_executions"]![0]!["variant"]!["params"]!["tags"] = new System.Text.Json.Nodes.JsonArray("a", token);
        manifest["matrix"]!["variants"]![0]!["params"]!["tags"] = new System.Text.Json.Nodes.JsonArray("a", token);

        var answer = await UploadAsync(world, manifest.ToJsonString());

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.Body.GetRawText().ShouldNotContain(token);
        answer.Body.GetRawText().ShouldNotContain("secret_in_variant_params");
    }

    [Fact]
    public async Task ReferencesAndOrdinaryEnumValues_StayAccepted()
    {
        await using var world = await ManifestTests.StartAsync(_fixture);
        var accepted = ManifestUnitTests.Build(world.SuiteRevision, world.Cases, [new() { ["browser"] = "CHROMIUM", ["api_token"] = "secret:ci_token", ["tags"] = "secret:ghp_AAAA" }], ["staging"]);

        (await UploadAsync(world, accepted.ToJsonString())).Status.ShouldBe(HttpStatusCode.Created);
    }
}
