using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The <c>completeness</c> filter of the run list: an exact match on the run's <em>current</em> evidence revision,
/// so a run without one (null join row) is excluded by both values, the pointer moving to a later revision moves the
/// match, and another tenant's revisions never reach the page.
/// </summary>
public sealed partial class ListRunsFilterTests
{
    [Fact]
    public async Task ListRunsFilter_Completeness_SelectsTheCurrentRevisionAndExcludesRunsWithoutOne()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var noRevision = await CreateRunAsync(world);
        var complete = await CreateRunAsync(world);
        var incomplete = await CreateRunAsync(world);
        await AppendEvidenceAsync(world, complete, [("complete", 1)]);
        await AppendEvidenceAsync(world, incomplete, [("incomplete", 1)]);

        // Control: without the filter the run that has no revision is listed, so its absence below is the filter's.
        (await IdsAsync(world, "limit=50")).ShouldBe(new[] { noRevision, complete, incomplete }, ignoreOrder: true);
        (await IdsAsync(world, "completeness=complete")).ShouldBe(new[] { complete });
        (await IdsAsync(world, "completeness=incomplete")).ShouldBe(new[] { incomplete });

        // AND-combined with another filter, in both directions of the list.
        (await IdsAsync(world, "completeness=incomplete&state=queued")).ShouldBe(new[] { incomplete });
        (await IdsAsync(world, "completeness=incomplete&state=complete")).ShouldBeEmpty();
        (await IdsAsync(world, "completeness=incomplete&order=desc")).ShouldBe(new[] { incomplete });

        // The current revision decides: a later incomplete revision moves the run to the other value.
        await AppendEvidenceAsync(world, complete, [("incomplete", 2)]);
        (await IdsAsync(world, "completeness=complete")).ShouldBeEmpty();
        (await IdsAsync(world, "completeness=incomplete")).ShouldBe(new[] { complete, incomplete }, ignoreOrder: true);
    }

    [Fact]
    public async Task ListRunsFilter_Completeness_OtherTenantRevisionNeverMatches()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var mine = await CreateRunAsync(world);
        await using var second = await StartSecondTenantAsync(world.Database, "rival");
        var theirs = await CreateRunAsync(second);
        await AppendEvidenceAsync(world, mine, [("incomplete", 1)]);
        await AppendEvidenceAsync(second, theirs, [("incomplete", 1)]);

        // Both runs satisfy the predicate, so only the tenant scope separates each page.
        (await IdsAsync(world, "completeness=incomplete")).ShouldBe(new[] { mine });
        (await IdsAsync(second, "completeness=incomplete")).ShouldBe(new[] { theirs });
        (await IdsAsync(world, "completeness=complete")).ShouldBeEmpty();
        (await IdsAsync(second, "completeness=complete")).ShouldBeEmpty();
    }
}
