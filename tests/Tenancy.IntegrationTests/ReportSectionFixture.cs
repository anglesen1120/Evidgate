using System.Text.Json;
using Evidgate.Modules.Authoring.Application.Reports;

namespace Tenancy.IntegrationTests;

/// <summary>
/// One scope that holds every state the section builders must tell apart: a requirement with current coverage, one
/// whose only link pins a superseded version, a case reached by a current link, a case reached only by that stale link,
/// and a case no link reaches.
/// </summary>
internal static class ReportSectionFixture
{
    internal static readonly Guid Feature = Id(1);
    internal static readonly Guid Item = Id(2);
    internal static readonly Guid Covered = Id(3);
    internal static readonly Guid Uncovered = Id(4);
    internal static readonly Guid CurrentCase = Id(5);
    internal static readonly Guid StaleCase = Id(6);
    internal static readonly Guid UnlinkedCase = Id(7);
    internal static readonly Guid Release = Id(8);
    internal static readonly Guid CurrentVersion = Id(9);

    internal static readonly string[] AllSections = ["test_plan", "test_cases", "traceability", "execution"];

    internal static Guid Id(int value) => new(value, 0, 0, new byte[8]);

    internal static ReportSource Source(string language = "en", string scope = ReportScopeKinds.FeatureNodes)
    {
        var steps = JsonSerializer.SerializeToElement(new
        {
            steps = new[] { new { no = 1, action = "Enter name", data = "Ada", expected = "Welcome Ada" } },
        });
        var request = new ReportRequest(
            scope,
            [scope == ReportScopeKinds.Release ? Release : Feature],
            AllSections,
            language,
            "Smoke <b>only</b>",
            "Linux =SUM(A1)");

        return new ReportSource(
            request,
            [new ReportNode(Feature, null, "feature", "Sign in", 0)],
            [new ReportItem(Item, Feature, "Credentials", "Item description", "high", "approved")],
            [Case(CurrentCase, "TC-1"), Case(StaleCase, "TC-2"), Case(UnlinkedCase, "TC-3")],
            [
                new ReportRequirement(Covered, "user_story", "Authenticate", "REQ-1", Feature, null, CurrentVersion, 2, "Requirement body", "sha256:a"),
                new ReportRequirement(Uncovered, "user_story", "Recover", null, Feature, null, Id(10), 2, "Recovery body", "sha256:b"),
            ],
            [
                new ReportLink(Id(11), Covered, CurrentVersion, 2, null, CurrentCase),
                new ReportLink(Id(12), Uncovered, Id(13), 1, null, StaleCase),
            ]);

        ReportCase Case(Guid id, string code) =>
            new(id, Item, code, "Case " + code, "Description", "User exists", steps, "high", "approved", 3, "sha256:c");
    }

    /// <summary>The base scope plus more stale links: a direct one, a stale link to the item (which reaches all three cases) and one on the covered requirement.</summary>
    internal static ReportSource SourceWithManyStaleLinks()
    {
        var source = Source();
        return source with
        {
            Links =
            [
                .. source.Links,
                new ReportLink(Id(14), Uncovered, Id(13), 0, null, StaleCase),
                new ReportLink(Id(15), Uncovered, Id(13), 1, Item, null),
                new ReportLink(Id(16), Covered, Id(17), 1, null, UnlinkedCase),
            ],
        };
    }
}
