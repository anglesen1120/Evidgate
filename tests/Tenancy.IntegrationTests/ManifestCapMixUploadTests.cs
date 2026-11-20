// Test inputs are written inline so each case reads as the document it checks.
#pragma warning disable CA1861
using System.Net;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// Over HTTP: the manifest upload answers 422 <c>manifest_invalid</c> with at most 50 details when unclean text and secrets
/// share the cap (49, 50 and 51 refusals), the same twice, nothing echoed and no row stored.
/// </summary>
[Trait("Category", "Docker")]
[Collection(ExclusiveTenancySuite.Name)]
public sealed class ManifestCapMixUploadTests
{
    private readonly PostgresTenancyFixture _fixture;

    public ManifestCapMixUploadTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    /// <summary>unclean rows, secret rows, then the details kept per kind.</summary>
    public static TheoryData<int, int, int, int> Mixes() => new()
    {
        { 19, 30, 30, 19 },
        { 20, 30, 30, 20 },
        { 21, 30, 30, 20 },
    };

    [Theory]
    [MemberData(nameof(Mixes))]
    public async Task FortyNineFiftyAndFiftyOneMixedRefusals_AnswerAtMostFiftyDetails_SecretsFirst_Deterministically(int unclean, int secrets, int keptSecrets, int keptUnclean)
    {
        await using var world = await ManifestTests.StartAsync(_fixture, cases: 1);
        var body = ManifestCapMixFixture.Build(world.SuiteRevision, world.Cases[0], unclean, secrets, hintSecret: false).ToJsonString();

        var first = await ManifestVariantParamsEquivalenceTests.UploadAsync(world.Client, world.Admin, world.Project, body);
        var second = await ManifestVariantParamsEquivalenceTests.UploadAsync(world.Client, world.Admin, world.Project, body);

        foreach (var answer in new[] { first, second })
        {
            answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
            answer.ErrorCode.ShouldBe("manifest_invalid");
            var details = answer.Body.GetProperty("error").GetProperty("details").EnumerateArray().ToList();
            details.Count.ShouldBe(keptSecrets + keptUnclean);

            var expected = new List<(string Path, string Reason, int Index)>();
            for (var row = unclean; row < unclean + keptSecrets; row++)
            {
                expected.Add(($"/planned_executions/{row}/variant/params/#2", "secret_in_variant_params", row));
            }

            for (var row = 0; row < keptUnclean; row++)
            {
                expected.Add(($"/planned_executions/{row}/variant/params/#2", "invalid_value", row));
            }

            details.Select(detail => (detail.GetProperty("path").GetString()!, detail.GetProperty("reason").GetString()!, detail.GetProperty("index").GetInt32()))
                .ShouldBe(expected);

            foreach (var echo in ManifestCapMixFixture.Echoes)
            {
                answer.Body.GetRawText().ShouldNotContain(echo, Case.Insensitive);
            }
        }

        first.Body.GetProperty("error").GetProperty("details").GetRawText().ShouldBe(second.Body.GetProperty("error").GetProperty("details").GetRawText());
        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.manifest where org_id = '{world.Org}'")).ShouldBe(0);
    }
}
