// Test inputs are written inline so each case reads as the document it checks.
#pragma warning disable CA1861
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// A refusal that sits only in <c>matrix.variants[*].params</c> is reported at <c>/matrix/variants/{n}/params/#p</c> without
/// an <c>index</c> (the matrix is not a planned execution), with the same reasons as the planned executions, and no value echoed.
/// </summary>
[Trait("Category", "Docker")]
[Collection(ExclusiveTenancySuite.Name)]
public sealed class ManifestMatrixPathUploadTests
{
    private readonly PostgresTenancyFixture _fixture;

    public ManifestMatrixPathUploadTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private static JsonObject Manifest(ManifestTests.World world) =>
        ManifestUnitTests.Build(
            world.SuiteRevision,
            world.Cases,
            [new() { ["browser"] = "x" }, new() { ["browser"] = "y", ["region"] = "eu" }],
            ["staging"]);

    private static Task<ApiAnswer> UploadAsync(ManifestTests.World world, JsonNode manifest) =>
        ManifestVariantParamsEquivalenceTests.UploadAsync(world.Client, world.Admin, world.Project, manifest.ToJsonString());

    private async Task<long> StoredAsync(ManifestTests.World world) =>
        await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.manifest where org_id = '{world.Org}'");

    private static List<JsonElement> Details(ApiAnswer answer) =>
        [.. answer.Body.GetProperty("error").GetProperty("details").EnumerateArray()];

    [Theory]
    [InlineData("a\u200Bb", "invalid_value")]
    [InlineData(ManifestCapMixFixture.Secret, "secret_in_variant_params")]
    public async Task ADirtyValueOnlyInTheMatrix_IsRefusedAtTheMatrixPath_WithoutAnIndex_AndItsCleanTwinIsAccepted(string dirty, string reason)
    {
        await using var world = await ManifestTests.StartAsync(_fixture);
        (await UploadAsync(world, Manifest(world))).Status.ShouldBe(HttpStatusCode.Created);
        var stored = await StoredAsync(world);

        var manifest = Manifest(world);
        manifest["matrix"]!["variants"]![1]!["params"]!["region"] = dirty;
        var answer = await UploadAsync(world, manifest);

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("manifest_invalid");
        var detail = Details(answer).ShouldHaveSingleItem();
        detail.GetProperty("path").GetString().ShouldBe("/matrix/variants/1/params/#2");
        detail.GetProperty("reason").GetString().ShouldBe(reason);
        detail.TryGetProperty("index", out _).ShouldBeFalse();
        answer.Body.GetRawText().ShouldNotContain("u200B", Case.Insensitive);
        answer.Body.GetRawText().ShouldNotContain("\u200B");
        answer.Body.GetRawText().ShouldNotContain("AAAAAAAAAAAA");
        (await StoredAsync(world)).ShouldBe(stored);
    }

    [Fact]
    public async Task ADirtyValueInPlannedExecutionsAndTheMatrix_ListsThePlannedDetailsFirst_ThenTheMatrixOne()
    {
        await using var world = await ManifestTests.StartAsync(_fixture);
        var manifest = Manifest(world);
        manifest["matrix"]!["variants"]![1]!["params"]!["region"] = "a\u200Bb";
        var planned = manifest["planned_executions"]!.AsArray();
        for (var row = 0; row < planned.Count; row++)
        {
            if (planned[row]!["variant"]!["params"]!["browser"]!.GetValue<string>() == "y")
            {
                planned[row]!["variant"]!["params"]!["region"] = "a\u200Bb";
            }
        }

        var answer = await UploadAsync(world, manifest);

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        var details = Details(answer);
        details.Select(detail => detail.GetProperty("path").GetString()).ShouldBe(
            [
                "/planned_executions/1/variant/params/#2",
                "/planned_executions/3/variant/params/#2",
                "/matrix/variants/1/params/#2",
            ]);
        details.Select(detail => detail.GetProperty("reason").GetString()).Distinct().ShouldBe(["invalid_value"]);
        details.Select(detail => detail.TryGetProperty("index", out _)).ShouldBe([true, true, false]);
        (await StoredAsync(world)).ShouldBe(0);
    }
}
