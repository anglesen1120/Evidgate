using System.Text.Json.Nodes;
using Evidgate.Modules.Authoring.Application.Reports;
using Evidgate.Modules.Authoring.Application.Reports.Sections;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>The four builders as the module registers them, run through the snapshot builder the job uses.</summary>
public sealed class ReportSectionRegistrationTests
{
    private static ReportSectionRegistry Registry()
    {
        using var provider = new ServiceCollection().AddReportSectionBuilders().BuildServiceProvider();
        return new ReportSectionRegistry(provider.GetServices<IReportSectionBuilder>());
    }

    [Fact]
    public void Registry_ServesTheFourSectionIdsAndRefusesAnyOther()
    {
        var registry = Registry();

        registry.FirstUnsupported(ReportSectionFixture.AllSections).ShouldBeNull();
        registry.FirstUnsupported(["test_plan", "release_components"]).ShouldBe(1);
    }

    [Fact]
    public void AddReportSectionBuilders_CalledTwice_RegistersEachBuilderOnce()
    {
        var services = new ServiceCollection().AddReportSectionBuilders().AddReportSectionBuilders();

        services.Count(descriptor => descriptor.ServiceType == typeof(IReportSectionBuilder)).ShouldBe(4);
    }

    [Fact]
    public void Build_OnlyTheSelectedSections_AppearInTheRequestedOrder()
    {
        var source = ReportSectionFixture.Source();
        source = source with { Request = source.Request with { Sections = ["traceability", "test_plan"] } };

        var payload = JsonNode.Parse(ReportSnapshotBuilder.Build(source, Registry(), CancellationToken.None).CanonicalPayload)!;

        payload["sections"]!.AsArray().Select(section => section!["id"]!.GetValue<string>()).ShouldBe(["traceability", "test_plan"]);
    }

    [Fact]
    public void Build_AllFourSections_SameSourceTwiceGivesTheSameHash_AndLanguageChangesIt()
    {
        var registry = Registry();

        var first = ReportSnapshotBuilder.Build(ReportSectionFixture.Source(), registry, CancellationToken.None);
        var second = ReportSnapshotBuilder.Build(ReportSectionFixture.Source(), registry, CancellationToken.None);
        var vietnamese = ReportSnapshotBuilder.Build(ReportSectionFixture.Source("vi"), registry, CancellationToken.None);

        second.ContentHash.ShouldBe(first.ContentHash);
        second.CanonicalPayload.ShouldBe(first.CanonicalPayload);
        vietnamese.ContentHash.ShouldNotBe(first.ContentHash);
        vietnamese.CanonicalPayload.ShouldContain("Ma trận truy vết");
        first.CanonicalPayload.ShouldNotContain("Ma trận truy vết");
    }

    [Fact]
    public void Build_SourceOrderOfRowsAndLinks_DoesNotChangeTheHash()
    {
        var registry = Registry();
        var source = ReportSectionFixture.Source();
        var shuffled = source with
        {
            Cases = [.. source.Cases.Reverse()],
            Requirements = [.. source.Requirements.Reverse()],
            Links = [.. source.Links.Reverse()],
        };

        ReportSnapshotBuilder.Build(shuffled, registry, CancellationToken.None).ContentHash
            .ShouldBe(ReportSnapshotBuilder.Build(source, registry, CancellationToken.None).ContentHash);
    }

    [Fact]
    public void Build_SeveralStaleLinksInAnyOrder_DoNotChangeTheHash()
    {
        var registry = Registry();
        var source = ReportSectionFixture.SourceWithManyStaleLinks();
        var reversed = source with { Links = [.. source.Links.Reverse()], Cases = [.. source.Cases.Reverse()] };

        ReportSnapshotBuilder.Build(reversed, registry, CancellationToken.None).ContentHash
            .ShouldBe(ReportSnapshotBuilder.Build(source, registry, CancellationToken.None).ContentHash);
    }
}
