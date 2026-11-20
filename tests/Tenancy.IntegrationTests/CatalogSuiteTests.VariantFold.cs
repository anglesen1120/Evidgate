using System.Net;
using System.Text.Json;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

// The suite-revision write is one of the two callers of VariantNormalizer; these pin its answers for the fold and
// the lone-surrogate rules. Intended fail-closed behaviour: a plan that re-submits a previously accepted upper-cased
// token-looking value is refused now; nothing stored is rewritten.
public sealed partial class CatalogSuiteTests
{
    [Fact]
    public async Task Revision_WithAnUpperCasedTokenPrefixUnderAnEnumLikeKey_Is422_NotSecretFree_AndEchoesNothing()
    {
        await using var world = await StartAsync();

        var answer = await CreateRevisionAsync(world, new { members = new[] { Member(world.Revisions[0], new { mode = "GHP_ZZECHOZZ" }, "m") } });

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("variant_not_secret_free");
        var detail = answer.Body.GetProperty("error").GetProperty("details").EnumerateArray().Single();
        detail.GetProperty("path").GetString().ShouldBe("/members/0/params/mode");
        detail.GetProperty("reason").GetString().ShouldBe("secret_value");
        answer.Body.GetRawText().ShouldNotContain("ZZECHOZZ", Case.Insensitive);
        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.variant where org_id = '{world.Org}'")).ShouldBe(0);
    }

    [Fact]
    public async Task Revision_WithALoneSurrogateParamsKey_Is422_ByPosition_NotAServerError()
    {
        await using var world = await StartAsync();
        var body = $$$"""{"members":[{"ref":"m","case_revision_id":"{{{world.Revisions[0]}}}","params":{"retries":1,"\ud800":1}}]}""";

        var answer = await world.Client.SendRawAsync(HttpMethod.Post, $"/v1/suites/{world.Suite}/revisions", headers =>
            {
                world.Admin(headers);
                headers.Add("Idempotency-Key", NewKey());
            },
            body);

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var detail = answer.Body.GetProperty("error").GetProperty("details").EnumerateArray().Single();
        detail.GetProperty("path").GetString().ShouldBe("/members/0/params/#2");
        detail.GetProperty("reason").GetString().ShouldBe("invalid_key");
        answer.Body.GetRawText().ShouldNotContain("ud800", Case.Insensitive);
        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.variant where org_id = '{world.Org}'")).ShouldBe(0);
    }
}
