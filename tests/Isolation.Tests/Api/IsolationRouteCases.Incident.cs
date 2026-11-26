namespace Isolation.Tests.Api;

/// <summary>
/// The incident reads (08.1.1): both need <c>healing:triage</c>, which org B's <c>tenant_admin</c> lacks, so the
/// member probes are sent by org B's <c>qa_engineer</c>. The service half is the generic project/object one: a
/// <c>ci_producer</c> holds no triage permission and is refused whatever it names.
/// </summary>
public static partial class IsolationRouteCases
{
    private static IEnumerable<RouteCase> IncidentCases()
    {
        static ProbeRequest List(Guid project) => new(HttpMethod.Get, $"/v1/projects/{project}/incidents");
        static ProbeRequest Detail(Guid id) => new(HttpMethod.Get, $"/v1/incidents/{id}");

        var list = ProjectCase("GET", "/v1/projects/{p}/incidents", List, list: true);
        yield return list with
        {
            Member = w =>
            [
                .. w.Acme.Projects.Select(p => Pair(list.Route, $"org A project {p.Slug} in {{p}}", ProbeExpect.EmptyOrNotFound,
                    HealingTriageCaller(w), List(p.Id), List(Guid.CreateVersion7()))),
                new(list.Route, "X-Evidgate-Org: A with an org-A project id", runner => runner.CallerPairAsync(
                    list.Route, "X-Evidgate-Org: A with an org-A project id", ForeignHeaderCaller(w), RandomHeaderCaller(w), List(w.Acme.Projects[0].Id))),
                OwnObject(list.Route, "own project answers 200", HealingTriageCaller(w), List(w.Acme2.Projects[0].Id)),
            ],
        };

        var detail = ObjectCase("GET", "/v1/incidents/{i}", p => [("incident", p.IncidentIds[0])], Detail);
        yield return detail with
        {
            Member = w =>
            [
                .. w.Acme.Projects.Select(p => Pair(detail.Route, $"org A incident of project {p.Slug}", ProbeExpect.NotFound,
                    HealingTriageCaller(w), Detail(p.IncidentIds[0]), Detail(Guid.CreateVersion7()))),
                new(detail.Route, "X-Evidgate-Org: A with an org-A id", runner => runner.CallerPairAsync(
                    detail.Route, "X-Evidgate-Org: A with an org-A id", ForeignHeaderCaller(w), RandomHeaderCaller(w), Detail(w.Acme.Projects[0].IncidentIds[0]))),
                OwnObject(detail.Route, "own incident answers 200", HealingTriageCaller(w), Detail(w.Acme2.Projects[0].IncidentIds[0])),
            ],
        };
    }
}
