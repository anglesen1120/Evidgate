using System.Net;
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
}
