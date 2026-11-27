using System.Text.Json.Nodes;

namespace Isolation.Tests.Api;

/// <summary>
/// The test-case report routes: create, history, one report and one job outcome. An org-B caller naming org A's
/// project, report or job, or a random id, is refused identically; the caller's own rows are the positive control.
/// </summary>
public static partial class IsolationRouteCases
{
    private static IEnumerable<RouteCase> ReportCases()
    {
        // The body names a live requirement of the project in {p}: a foreign probe is then refused by tenancy and not
        // by selection validation, and the caller's own project proves the route queues a job (202).
        static ProbeRequest Create(Guid project, Guid requirement) => new(HttpMethod.Post, $"/v1/projects/{project}/authoring/reports", new JsonObject
        {
            ["scope_kind"] = "requirements",
            ["selected_ids"] = new JsonArray { requirement.ToString("D") },
            ["sections"] = new JsonArray { "test_plan" },
            ["language"] = "en",
        });

        yield return ProjectBodyCase("POST", "/v1/projects/{p}/authoring/reports",
            p => Create(p.Id, p.AuthoringRequirementId),
            () => Create(Guid.CreateVersion7(), Guid.CreateVersion7()),
            (route, w) => new(route, "own project queues a report answers 202", runner => runner.OwnObjectAsync(
                route, "own project queues a report answers 202", OrgBAdmin(w),
                Create(w.Acme2.Projects[0].Id, w.Acme2.Projects[0].AuthoringRequirementId), expectedStatus: 202)));

        yield return ProjectCase("GET", "/v1/projects/{p}/authoring/reports",
            p => new(HttpMethod.Get, $"/v1/projects/{p}/authoring/reports?limit=1"), list: true, ownControl: true);

        yield return ObjectCaseWith("GET", "/v1/projects/{p}/authoring/reports/{r}", p => [("report", p.ReportId)],
            (id, p) => new(HttpMethod.Get, $"/v1/projects/{p.Id}/authoring/reports/{id}"), (_, _) => [],
            own: p => new(HttpMethod.Get, $"/v1/projects/{p.Id}/authoring/reports/{p.ReportId}"));

        yield return ObjectCaseWith("GET", "/v1/projects/{p}/authoring/reports/jobs/{j}", p => [("report job", p.ReportJobId)],
            (id, p) => new(HttpMethod.Get, $"/v1/projects/{p.Id}/authoring/reports/jobs/{id}"), (_, _) => [],
            own: p => new(HttpMethod.Get, $"/v1/projects/{p.Id}/authoring/reports/jobs/{p.ReportJobId}"));
    }
}
