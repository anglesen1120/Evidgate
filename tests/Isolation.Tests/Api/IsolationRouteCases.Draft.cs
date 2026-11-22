using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;

namespace Isolation.Tests.Api;

/// <summary>
/// The draft surface of 03.6.1: two project-scoped routes (<c>POST</c> and <c>GET /v1/projects/{p}/drafts</c>) and
/// the two un-nested draft routes, whose permission is checked against the draft's own project. An org-B caller
/// naming org A's project or draft, or a random id, must be refused identically and must write no draft.
/// </summary>
public static partial class IsolationRouteCases
{
    /// <summary>The draft body the create route accepts; the reads and the patch reuse its content shape.</summary>
    private static JsonObject DraftBody(string path = "tests/isolation-probe.ts") => new()
    {
        ["kind"] = "template",
        ["base_commit"] = "abcdef1",
        ["content"] = DraftContent(path),
    };

    private static JsonObject DraftContent(string path = "tests/isolation-probe.ts") => new()
    {
        ["files"] = new JsonArray
        {
            new JsonObject
            {
                ["path"] = path,
                ["body_sha"] = "sha256:" + new string('a', 64),
                ["body_ref"] = "drafts/isolation-probe.ts",
            },
        },
    };

    private static JsonObject DraftUpdate() => new() { ["content"] = DraftContent("tests/isolation-updated.ts") };

    private static IEnumerable<RouteCase> DraftCases()
    {
        yield return ProjectCase("POST", "/v1/projects/{p}/drafts",
            p => new(HttpMethod.Post, $"/v1/projects/{p}/drafts", DraftBody()));

        // The list is project-scoped: org A's project answers the same 404 an unknown one gets, or an empty page,
        // and the caller's own project is the positive control.
        yield return ProjectCase("GET", "/v1/projects/{p}/drafts",
            p => new(HttpMethod.Get, $"/v1/projects/{p}/drafts?limit=1"), list: true, ownControl: true);

        yield return ObjectCaseWith("GET", "/v1/drafts/{d}",
            p => [("draft", p.DraftIds[0])],
            (id, p) => new(HttpMethod.Get, $"/v1/drafts/{id}"),
            (_, _) => [],
            own: p => new(HttpMethod.Get, $"/v1/drafts/{p.DraftIds[0]}"));

        yield return ObjectCaseWith("PATCH", "/v1/drafts/{d}",
            p => [("draft", p.DraftIds[0])],
            (id, p) => new(HttpMethod.Patch, $"/v1/drafts/{id}", DraftUpdate(), "\"1\""),
            (route, world) =>
            [
                // The caller's own draft with a stale tag: 412 precondition_failed proves the request shape reached
                // the handler, and it mutates nothing, so the shared world stays as seeded.
                new(route, "own draft with a stale If-Match answers 412", async runner =>
                {
                    var caller = OrgBAdmin(world);
                    var request = new ProbeRequest(
                        HttpMethod.Patch,
                        string.Create(CultureInfo.InvariantCulture, $"/v1/drafts/{world.Acme2.Projects[0].DraftIds[0]}"),
                        DraftUpdate(),
                        "\"99\"");

                    var response = await runner.SendAsync(caller, request);
                    var violations = new List<string>();
                    if (response.Status != (int)HttpStatusCode.PreconditionFailed
                        || (response.Code is not ("precondition_failed" or "conflict")))
                    {
                        violations.Add(RouteProbeRunner.Describe(
                            route, "own draft with a stale If-Match answers 412", caller,
                            $"answered {response}, expected 412 with the stale code"));
                    }

                    return violations;
                }),
            ]);
    }
}
