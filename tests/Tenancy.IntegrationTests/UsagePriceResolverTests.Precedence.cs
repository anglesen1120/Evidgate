using Evidgate.Modules.Usage.Contracts;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The lookup's precedence and half-open range: an org's own override beats the global card, a row's
/// <c>effective_to</c> belongs to the next row, and another org's rows never apply. Split out of the suite file to keep
/// each file under the size cap.
/// </summary>
public sealed partial class UsagePriceResolverTests
{
    // ------------------------------------------------------------------ precedence and range

    [Fact]
    public async Task Resolve_WithoutAnOverride_ReturnsTheGlobalPrice()
    {
        await using var world = await StartAsync();
        (await LoadGlobalAsync(world, Card("global-2026"))).Succeeded.ShouldBeTrue();

        var price = (await ResolveAsync(world, world.Org, UsageMetric.Results, March)).ShouldNotBeNull();

        price.OrgId.ShouldBeNull();
        price.UnitPrice.ShouldBe(0.0100m);
        price.RateCardVersion.ShouldBe("global-2026");
    }

    [Fact]
    public async Task Resolve_OrgOverride_BeatsTheGlobalCard()
    {
        await using var world = await StartAsync();
        (await LoadGlobalAsync(world, Card("global-2026"))).Succeeded.ShouldBeTrue();
        (await LoadOrgAsync(world, world.Org, Card("org-2026", unitPrice: "0.0200"))).Succeeded.ShouldBeTrue();

        var price = (await ResolveAsync(world, world.Org, UsageMetric.Results, March)).ShouldNotBeNull();

        price.OrgId.ShouldBe(world.Org);
        price.UnitPrice.ShouldBe(0.0200m);
        price.RateCardVersion.ShouldBe("org-2026");
    }

    [Fact]
    public async Task Resolve_AtEffectiveFrom_Resolves()
    {
        await using var world = await StartAsync();
        (await LoadGlobalAsync(world, Card("global-2026", to: February))).Succeeded.ShouldBeTrue();

        var price = (await ResolveAsync(world, world.Org, UsageMetric.Results, January)).ShouldNotBeNull();

        price.RateCardVersion.ShouldBe("global-2026");
    }

    [Fact]
    public async Task Resolve_AtEffectiveTo_DoesNotResolveAndTheNextRowDoes()
    {
        await using var world = await StartAsync();
        (await LoadGlobalAsync(world, Card("global-january", to: February))).Succeeded.ShouldBeTrue();
        (await LoadGlobalAsync(world, Card("global-february", unitPrice: "0.0200", from: February))).Succeeded.ShouldBeTrue();

        var atTheBoundary = (await ResolveAsync(world, world.Org, UsageMetric.Results, February)).ShouldNotBeNull();
        // One microsecond, the column's own precision: a sub-microsecond instant would be rounded onto the boundary.
        var justBefore = (await ResolveAsync(world, world.Org, UsageMetric.Results, February.AddTicks(-10)))
            .ShouldNotBeNull();

        atTheBoundary.RateCardVersion.ShouldBe("global-february");
        atTheBoundary.UnitPrice.ShouldBe(0.0200m);
        justBefore.RateCardVersion.ShouldBe("global-january");
    }

    [Fact]
    public async Task Resolve_BeforeTheFirstRow_ReturnsNull()
    {
        await using var world = await StartAsync();
        (await LoadGlobalAsync(world, Card("global-2026", from: February))).Succeeded.ShouldBeTrue();

        (await ResolveAsync(world, world.Org, UsageMetric.Results, January)).ShouldBeNull();
        (await ResolveAsync(world, world.Org, UsageMetric.Results, February)).ShouldNotBeNull();
    }

    [Fact]
    public async Task Resolve_AnotherOrgsOverride_NeverApplies()
    {
        await using var world = await StartAsync();
        (await LoadGlobalAsync(world, Card("global-2026"))).Succeeded.ShouldBeTrue();
        (await LoadOrgAsync(world, world.OtherOrg, Card("other-2026", unitPrice: "0.0300"))).Succeeded.ShouldBeTrue();

        var mine = (await ResolveAsync(world, world.Org, UsageMetric.Results, March)).ShouldNotBeNull();
        var theirs = (await ResolveAsync(world, world.OtherOrg, UsageMetric.Results, March)).ShouldNotBeNull();

        mine.RateCardVersion.ShouldBe("global-2026");
        mine.OrgId.ShouldBeNull();
        theirs.RateCardVersion.ShouldBe("other-2026");
        theirs.OrgId.ShouldBe(world.OtherOrg);
    }
}
