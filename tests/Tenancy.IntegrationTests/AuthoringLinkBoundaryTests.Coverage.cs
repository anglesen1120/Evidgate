using System.Globalization;
using System.Net;
using System.Text.Json;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

public sealed partial class AuthoringLinkBoundaryTests
{
    [Theory]
    [InlineData("")]
    [InlineData("%20")]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("null")]
    [InlineData("TRUE")]
    [InlineData("False")]
    [InlineData("not-a-bool")]
    [InlineData("true&covered=false")]
    [InlineData("true&covered=true")]
    public async Task Covered_PresentInvalidValue_Returns422(string value)
    {
        await using var world = await StartAsync();
        var positive = await world.Client.SendAsync(HttpMethod.Get, world.Tree.CasesPath + "?covered=false", world.Caller);
        positive.Status.ShouldBe(HttpStatusCode.OK);
        positive.Body.GetProperty("items").GetArrayLength().ShouldBe(1);

        var answer = await world.Client.SendAsync(HttpMethod.Get, world.Tree.CasesPath + "?covered=" + value, world.Caller);
        AssertInvalid(answer, "/covered", "value_invalid");
    }

    [Fact]
    public async Task Covered_AbsentFalseTrue_FormsComplementAndArchiveChangesCoverage()
    {
        await using var world = await StartAsync();
        var unlinked = await AuthoringLinkTestData.SeedManualCaseAsync(fixture, world.Database, world.Tree, 2);
        (await PostAsync(world, world.Catalog)).Status.ShouldBe(HttpStatusCode.Created);
        var all = await ItemsAsync(world, "");
        all.Select(item => item.GetProperty("case_id").GetGuid()).ShouldBe([world.Manual, unlinked], ignoreOrder: true);
        var covered = await ItemsAsync(world, "?covered=true");
        covered.ShouldHaveSingleItem().GetProperty("case_id").GetGuid().ShouldBe(world.Manual);
        covered[0].GetProperty("linked_live_count").GetInt32().ShouldBe(1);
        covered[0].GetProperty("linked_case_ids").EnumerateArray().Single().GetGuid().ShouldBe(world.Catalog);
        var uncovered = await ItemsAsync(world, "?covered=false");
        uncovered.ShouldHaveSingleItem().GetProperty("case_id").GetGuid().ShouldBe(unlinked);
        uncovered[0].GetProperty("linked_live_count").GetInt32().ShouldBe(0);
        uncovered[0].GetProperty("linked_case_ids").GetArrayLength().ShouldBe(0);

        await AuthoringLinkTestData.ArchiveAsync(fixture, world.Database, world.Catalog);
        (await ItemsAsync(world, "?covered=true")).ShouldBeEmpty();
        (await ItemsAsync(world, "?covered=false")).Length.ShouldBe(2);
        (await AuthoringLinkTestData.LinkCountAsync(fixture, world.Database, world.Tree)).ShouldBe(1);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(200)]
    public async Task Covered_LimitBoundary_KeysetWalkHasNoDuplicatesOrOmissions(int limit)
    {
        await using var world = await StartAsync();
        (await PostAsync(world, world.Catalog)).Status.ShouldBe(HttpStatusCode.Created);
        var expected = new HashSet<Guid> { world.Manual };
        for (var number = 2; number <= 4; number++)
        {
            var manual = await AuthoringLinkTestData.SeedManualCaseAsync(fixture, world.Database, world.Tree, number);
            if (number % 2 == 0)
            {
                var linked = await world.Client.SendAsync(HttpMethod.Post, world.Tree.LinkPath(manual), world.Caller,
                    new { test_case_id = world.Catalog }, NewKey());
                linked.Status.ShouldBe(HttpStatusCode.Created);
                expected.Add(manual);
            }
        }

        var actual = new List<Guid>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var path = world.Tree.CasesPath + "?covered=true&limit=" + limit.ToString(CultureInfo.InvariantCulture);
            if (cursor is not null)
            {
                path += "&cursor=" + Uri.EscapeDataString(cursor);
            }

            var answer = await world.Client.SendAsync(HttpMethod.Get, path, world.Caller);
            answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
            var items = answer.Body.GetProperty("items").EnumerateArray().ToArray();
            items.Length.ShouldBeLessThanOrEqualTo(limit);
            actual.AddRange(items.Select(item => item.GetProperty("case_id").GetGuid()));
            cursor = answer.Body.TryGetProperty("next_cursor", out var next) && next.ValueKind == JsonValueKind.String
                ? next.GetString() : null;
            (++pages).ShouldBeLessThan(10);
        }
        while (cursor is not null);

        actual.Count.ShouldBe(expected.Count);
        actual.ToHashSet().ShouldBe(expected, ignoreOrder: true);
    }

    [Fact]
    public async Task Covered_SoftDeletedManualCase_HidesButPreservesLinkForRestore()
    {
        await using var world = await StartAsync();
        (await PostAsync(world, world.Catalog)).Status.ShouldBe(HttpStatusCode.Created);
        (await ItemsAsync(world, "?covered=true")).ShouldHaveSingleItem();
        await fixture.ExecuteAsync(world.Database,
            $"update public.manual_test_case set deleted_at = now() where id = '{world.Manual}'");
        (await ItemsAsync(world, "?covered=true")).ShouldBeEmpty();
        (await ItemsAsync(world, "?covered=false")).ShouldBeEmpty();
        (await AuthoringLinkTestData.LinkCountAsync(fixture, world.Database, world.Tree)).ShouldBe(1);
        var newTarget = await AuthoringLinkTestData.SeedCatalogCaseAsync(fixture, world.Database, world.Tree);
        (await PostAsync(world, newTarget)).Status.ShouldBe(HttpStatusCode.NotFound);
        await fixture.ExecuteAsync(world.Database,
            $"update public.manual_test_case set deleted_at = null where id = '{world.Manual}'");
        (await ItemsAsync(world, "?covered=true")).ShouldHaveSingleItem();
        (await PostAsync(world, newTarget)).Status.ShouldBe(HttpStatusCode.Created);
    }

    private static async Task<JsonElement[]> ItemsAsync(World world, string query)
    {
        var answer = await world.Client.SendAsync(HttpMethod.Get, world.Tree.CasesPath + query, world.Caller);
        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        return answer.Body.GetProperty("items").EnumerateArray().ToArray();
    }
}
