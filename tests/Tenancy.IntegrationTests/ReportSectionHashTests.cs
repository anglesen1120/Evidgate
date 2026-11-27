using System.Text.Json;
using Evidgate.Modules.Authoring.Application.Reports;
using Evidgate.Modules.Authoring.Application.Reports.Sections;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>The content hash covers every section's content: a change only one section renders moves the hash of a report that selects that section.</summary>
public sealed class ReportSectionHashTests
{
    [Fact]
    public void Build_EverySectionContentIsInsideTheHash()
    {
        using var provider = new ServiceCollection().AddReportSectionBuilders().BuildServiceProvider();
        var registry = new ReportSectionRegistry(provider.GetServices<IReportSectionBuilder>());
        var changes = new Dictionary<string, Func<ReportSource, ReportSource>>(StringComparer.Ordinal)
        {
            // The request (strategy, environment) is hashed outside the sections, so the plan's change is one only it renders.
            ["test_plan"] = source => source with { Requirements = [source.Requirements[0] with { Kind = "epic" }, .. source.Requirements.Skip(1)] },
            ["test_cases"] = ChangeAStepExpectation,
            ["traceability"] = source => source with
            {
                Links = [.. source.Links, new ReportLink(ReportSectionFixture.Id(20), ReportSectionFixture.Covered, ReportSectionFixture.CurrentVersion, 2, null, ReportSectionFixture.UnlinkedCase)],
            },
            ["execution"] = source => source with
            {
                ManualResults = [new ReportManualResult(ReportSectionFixture.CurrentCase, ManualStatuses.Failed)],
            },
        };

        changes.Keys.Order(StringComparer.Ordinal).ShouldBe(ReportSectionFixture.AllSections.Order(StringComparer.Ordinal));
        foreach (var (section, change) in changes)
        {
            var baseline = ReportSnapshotBuilder.Build(Only(section), registry, CancellationToken.None);
            var changed = ReportSnapshotBuilder.Build(change(Only(section)), registry, CancellationToken.None);

            changed.ContentHash.ShouldNotBe(baseline.ContentHash, $"a change only the {section} section renders must change the hash");
            changed.CanonicalPayload.ShouldNotBe(baseline.CanonicalPayload);
            ReportSnapshotBuilder.Build(Only(section), registry, CancellationToken.None).ContentHash.ShouldBe(baseline.ContentHash);
        }
    }

    private static ReportSource Only(string section)
    {
        var source = ReportSectionFixture.Source();
        return source with { Request = source.Request with { Sections = [section] } };
    }

    private static ReportSource ChangeAStepExpectation(ReportSource source)
    {
        var steps = JsonSerializer.SerializeToElement(new
        {
            steps = new[] { new { no = 1, action = "Enter name", data = "Ada", expected = "Welcome back Ada" } },
        });

        return source with { Cases = [source.Cases[0] with { Steps = steps }, .. source.Cases.Skip(1)] };
    }
}
