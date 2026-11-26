using System.Net;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Shouldly;
using Xunit;

namespace Isolation.Tests.Api;

/// <summary>
/// The incident reads against the shared isolation world: each project carries three incidents of one run that share
/// a creation instant, one of them triaged. The route-wide probes live in <see cref="IsolationRouteCases"/>; these
/// tests pin the content half (own rows, filters, paging and permission) that a refusal probe cannot.
/// </summary>
[Collection(ApiIsolationSuite.Name)]
[Trait("Category", "Docker")]
public sealed class IncidentEndpointTests(ApiIsolationFixture fixture)
{
    [Fact]
    public async Task cross_tenant_incident_invisible()
    {
        var runner = new RouteProbeRunner(fixture);
        var world = fixture.World;
        var a = world.Acme;
        var b = world.Acme2;
        var own = Triage(b);
        var viewer = ProbeCaller.Member("org-B viewer", b.User("viewer"), b.Id);
        var victim = a.Projects[0].IncidentIds[0];
        var ownProject = b.Projects[0];

        (await runner.SendAsync(own, Get($"/v1/incidents/{ownProject.IncidentIds[0]}"))).Status.ShouldBe(200);
        (await runner.SendAsync(own, Get($"/v1/incidents/{victim}"))).Status.ShouldBe((int)HttpStatusCode.NotFound);
        var foreignList = await runner.SendAsync(own, Get($"/v1/projects/{a.Projects[0].Id}/incidents"));
        (foreignList.Status == (int)HttpStatusCode.NotFound || foreignList.ItemCount == 0).ShouldBeTrue(foreignList.ToString());
        Ids(await runner.SendAsync(own, Get($"/v1/projects/{ownProject.Id}/incidents"))).ShouldBe(ownProject.IncidentIds);
        (await runner.SendAsync(viewer, Get($"/v1/incidents/{ownProject.IncidentIds[0]}"))).Status.ShouldBe((int)HttpStatusCode.Forbidden);
        (await runner.SendAsync(viewer, Get($"/v1/projects/{ownProject.Id}/incidents"))).Status.ShouldBe((int)HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task list_incidents_keyset_and_filters()
    {
        var runner = new RouteProbeRunner(fixture);
        var org = fixture.World.Acme;
        var project = org.Projects[0];
        var caller = Triage(org);
        var url = $"/v1/projects/{project.Id}/incidents";
        var expected = project.IncidentIds;
        expected.Count.ShouldBeGreaterThan(1);

        var all = await runner.SendAsync(caller, Get(url + "?limit=200"));
        Ids(all).ShouldBe(expected);
        all.Json!.Value.GetProperty("items").EnumerateArray()
            .Select(row => row.GetProperty("created_at").GetDateTimeOffset()).Distinct().ShouldHaveSingleItem();

        var seen = new List<Guid>();
        string? cursor = null;
        do
        {
            var page = await runner.SendAsync(caller, Get(url + "?limit=1" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor))));
            seen.AddRange(Ids(page));
            cursor = page.Json!.Value.GetProperty("next_cursor").GetString();
        }
        while (cursor is not null && seen.Count <= expected.Count);
        seen.ShouldBe(expected);

        var key = all.Json!.Value.GetProperty("items")[0].GetProperty("execution_key").GetString();
        Ids(await runner.SendAsync(caller, Get(url + "?state=triaged"))).ShouldBe([expected[0]]);
        Ids(await runner.SendAsync(caller, Get(url + "?execution_key=" + key))).ShouldBe([expected[0]]);
        Ids(await runner.SendAsync(caller, Get(url + "?state=open&execution_key=" + key))).ShouldBeEmpty();
        foreach (var bad in new[] { "state=unrecognised", "state=", "state=open&state=closed", "execution_key=ABC", "limit=0", "limit=201", "limit=bad", "cursor=bad" })
        {
            (await runner.SendAsync(caller, Get(url + "?" + bad))).Status.ShouldBe((int)HttpStatusCode.UnprocessableEntity, bad);
        }
    }

    [Fact]
    public async Task list_incidents_cursor_before_or_after_all_rows()
    {
        var runner = new RouteProbeRunner(fixture);
        var org = fixture.World.Acme;
        var project = org.Projects[0];
        var caller = Triage(org);
        var url = $"/v1/projects/{project.Id}/incidents";
        var first = await runner.SendAsync(caller, Get(url + "?limit=1"));
        first.Status.ShouldBe((int)HttpStatusCode.OK);
        var instant = first.Json!.Value.GetProperty("items")[0].GetProperty("created_at").GetDateTimeOffset();

        var older = KeysetCursor.EncodeDescending(instant.AddSeconds(-1), Guid.Empty);
        var empty = await runner.SendAsync(caller, Get(url + "?cursor=" + Uri.EscapeDataString(older)));
        empty.Status.ShouldBe((int)HttpStatusCode.OK);
        Ids(empty).ShouldBeEmpty();
        empty.Json!.Value.GetProperty("next_cursor").GetString().ShouldBeNull();

        var newer = KeysetCursor.EncodeDescending(instant.AddSeconds(1), Guid.AllBitsSet);
        var all = await runner.SendAsync(caller, Get(url + "?cursor=" + Uri.EscapeDataString(newer)));
        all.Status.ShouldBe((int)HttpStatusCode.OK);
        Ids(all).ShouldBe(project.IncidentIds);
        all.Json!.Value.GetProperty("next_cursor").GetString().ShouldBeNull();
    }

    private static ProbeCaller Triage(IsolationOrg org) =>
        ProbeCaller.Member($"{org.Id} qa_engineer", org.User("qa_engineer"), org.Id);

    private static ProbeRequest Get(string url) => new(HttpMethod.Get, url);

    private static List<Guid> Ids(ProbeResponse response) =>
        [.. response.Json!.Value.GetProperty("items").EnumerateArray().Select(row => row.GetProperty("id").GetGuid())];
}
