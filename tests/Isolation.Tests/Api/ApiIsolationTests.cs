using System.Globalization;
using System.Text;
using Shouldly;
using Xunit;

namespace Isolation.Tests.Api;

/// <summary>
/// 02.9.1-T1.2 (AC1, AC2, AC6): every served <c>/v1</c> route, called by org B (<see cref="IsolationWorld.Acme2"/>)
/// with org A's (<see cref="IsolationWorld.Acme"/>) ids, against the real host and PostgreSQL 18. Each test runs
/// its whole slice of <see cref="IsolationRouteCases"/> and fails once, listing every violating route and case.
/// </summary>
/// <remarks>
/// AC1's 404 applies to object ids. A member naming a foreign org in <c>{o}</c> or <c>X-Evidgate-Org</c> is refused
/// earlier, at tenant resolution: 403 for a non-member (TenantResolver.cs:64-70, 00 §15 2026-10-03). The suite
/// accepts 403 or 404 there and proves no org-existence oracle by requiring the same status and error code for
/// org A's real id and a random one.
/// </remarks>
[Collection(ApiIsolationSuite.Name)]
[Trait("Category", "Docker")]
public sealed class ApiIsolationTests(ApiIsolationFixture fixture)
{
    private const string CiToken = "/v1/ci/token";

    /// <summary>AC1: every GET naming an org-A id answers as a random id does (404 for object ids), never the row.</summary>
    [Fact]
    public async Task GetById_ForeignOrg_404()
    {
        var runner = new RouteProbeRunner(fixture);
        var probes = IsolationRouteCases.MemberProbes(fixture.World).Where(p => p.Route.Method == "GET").ToList();

        var violations = await RunAsync(runner, probes);

        probes.Count.ShouldBeGreaterThan(0);
        violations.ShouldBeEmpty(Report("GET with org-A ids", probes.Count, violations));
    }

    /// <summary>
    /// AC2 (00 §9.2): org B's real <c>ci_producer</c> token on its sibling project gets 403
    /// <c>project_scope_mismatch</c> (or 403 where it lacks the route's permission), and on org A's ids the same
    /// refusal as on random ids; org A's revoked ingest token never mints a token.
    /// </summary>
    [Fact]
    public async Task TokenScope_OtherProject_403()
    {
        var runner = new RouteProbeRunner(fixture);
        var world = fixture.World;
        var probes = IsolationRouteCases.ServiceProbes(world)
            .Concat(IsolationRouteCases.MemberProbes(world).Where(p => p.Route.Template == CiToken))
            .ToList();

        var before = await fixture.SnapshotOrgAsync(world.Acme.Id);
        var violations = await RunAsync(runner, probes);
        var after = await fixture.SnapshotOrgAsync(world.Acme.Id);

        if (before != after)
        {
            violations.Add($"org A's rows changed during {nameof(TokenScope_OtherProject_403)}:" + Environment.NewLine + Diff(before, after));
        }

        probes.ShouldContain(p => p.Case.StartsWith("sibling project", StringComparison.Ordinal));
        violations.ShouldBeEmpty(Report("ci_producer scope", probes.Count, violations));
    }

    /// <summary>
    /// AC2: every served list, paged as org B to exhaustion, yields exactly org B's items (count and ids), never an
    /// org-A id; an org-A cursor fed to org B yields no org-A item. Every walk with a foreign caller must execute
    /// that cross-cursor step — a list seeded too small to page fails instead of silently skipping it.
    /// </summary>
    [Fact]
    public async Task List_OnlyOwnOrg_FullPagination()
    {
        var runner = new RouteProbeRunner(fixture);
        var walks = IsolationRouteCases.Lists(fixture.World);
        var violations = new List<string>();
        var cursorChecks = 0;

        foreach (var walk in walks)
        {
            var (ids, failure, pages) = await runner.WalkAsync(walk.Caller, walk.Url, walk.IdField);
            if (failure is not null)
            {
                violations.Add(RouteProbeRunner.Describe(walk.Route, walk.Case, walk.Caller, failure));
                continue;
            }

            var expected = await walk.Expected(runner);
            var seen = ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (seen.Count != ids.Count)
            {
                violations.Add(RouteProbeRunner.Describe(walk.Route, walk.Case, walk.Caller, $"pagination repeated an item ({ids.Count} items, {seen.Count} distinct)"));
            }

            if (!seen.SetEquals(expected))
            {
                violations.Add(RouteProbeRunner.Describe(walk.Route, walk.Case, walk.Caller,
                    $"paged {ids.Count} item(s), expected {expected.Count}; extra [{string.Join(", ", seen.Except(expected, StringComparer.OrdinalIgnoreCase))}], " +
                    $"missing [{string.Join(", ", expected.Except(seen, StringComparer.OrdinalIgnoreCase))}]"));
            }

            violations.AddRange(pages.SelectMany(runner.ForeignIdsIn).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(id => RouteProbeRunner.Describe(walk.Route, walk.Case, walk.Caller, $"a page carries org-A id {id}")));

            if (walk.ItemCheck is not null)
            {
                foreach (var page in pages)
                {
                    using var document = System.Text.Json.JsonDocument.Parse(page);
                    var response = new ProbeResponse(200, null, page, document.RootElement.Clone());
                    if (response.Json is { } json && json.TryGetProperty("items", out var items))
                    {
                        violations.AddRange(items.EnumerateArray().Select(walk.ItemCheck).OfType<string>()
                            .Select(v => RouteProbeRunner.Describe(walk.Route, walk.Case, walk.Caller, v)));
                    }
                }
            }

            if (walk.ForeignCaller is not null && walk.ForeignUrl is not null)
            {
                var (executed, cursorViolations) = await ForeignCursorAsync(runner, walk);
                violations.AddRange(cursorViolations);
                if (executed)
                {
                    cursorChecks++;
                }
                else
                {
                    violations.Add(RouteProbeRunner.Describe(walk.Route, walk.Case, walk.Caller,
                        "org A's first page had no next_cursor, so the cross-org cursor step never ran"));
                }
            }
        }

        var streams = IsolationRouteCases.OwnStreams(fixture.World);
        violations.AddRange(await RunAsync(runner, streams));

        if (cursorChecks == 0)
        {
            violations.Add("no org-A cursor was fed to org B: no walk with a foreign caller got an org-A next_cursor");
        }

        walks.Count.ShouldBeGreaterThan(0);
        violations.ShouldBeEmpty(Report("full-pagination lists", walks.Count, violations));
    }

    /// <summary>
    /// AC2: every mutation naming org A (in <c>{o}</c>, <c>{p}</c>, an object id or a body reference), by an org-B
    /// member or org B's <c>ci_producer</c>, is refused as a random id is, and org A's rows are byte-identical
    /// before and after the whole matrix (owner-connection snapshot, taken in this test).
    /// </summary>
    [Fact]
    public async Task Mutation_ForeignOrg_RefusedAndNoWrite()
    {
        var runner = new RouteProbeRunner(fixture);
        var world = fixture.World;
        var probes = IsolationRouteCases.MemberProbes(world)
            .Concat(IsolationRouteCases.ServiceProbes(world))
            .Where(p => p.Route.Method != "GET" && p.Route.Template != CiToken)
            .ToList();

        var before = await fixture.SnapshotOrgAsync(world.Acme.Id);
        var violations = await RunAsync(runner, probes);
        var after = await fixture.SnapshotOrgAsync(world.Acme.Id);

        if (before != after)
        {
            violations.Add("org A's rows changed during the mutation matrix:" + Environment.NewLine + Diff(before, after));
        }

        probes.Count.ShouldBeGreaterThan(0);
        violations.ShouldBeEmpty(Report("mutations naming org A", probes.Count, violations));
    }

    private static async Task<List<string>> RunAsync(RouteProbeRunner runner, IEnumerable<RouteProbe> probes)
    {
        var violations = new List<string>();
        foreach (var probe in probes)
        {
            try
            {
                violations.AddRange(await probe.RunAsync(runner));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                violations.Add($"{probe}: threw {exception.GetType().Name}: {exception.Message}");
            }
        }

        return violations;
    }

    /// <summary>
    /// Org A's first-page cursor on org B's list: refused (4xx, never 5xx) or only org-B items, never an org-A id.
    /// <c>Executed</c> is false when org A's first page has no <c>next_cursor</c>.
    /// </summary>
    private static async Task<(bool Executed, IReadOnlyList<string> Violations)> ForeignCursorAsync(RouteProbeRunner runner, ListWalk walk)
    {
        const string name = "org-A cursor fed to org B";
        var cursor = await runner.FirstCursorAsync(walk.ForeignCaller!, walk.ForeignUrl!);
        if (cursor is null)
        {
            return (false, []);
        }

        var url = walk.Url + (walk.Url.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "cursor=" + Uri.EscapeDataString(cursor);
        var response = await runner.SendAsync(walk.Caller, new(HttpMethod.Get, url));
        var violations = new List<string>();
        if (response.Status >= 500 || (response.IsSuccess && response.ItemCount is null))
        {
            violations.Add(RouteProbeRunner.Describe(walk.Route, name, walk.Caller, $"GET {url} answered {response}"));
        }

        violations.AddRange(runner.ForeignIdsIn(response.Text).Select(id => RouteProbeRunner.Describe(walk.Route, name, walk.Caller, $"GET {url} yielded org-A id {id}")));
        return (true, violations);
    }

    private static string Report(string what, int count, List<string> violations)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"{violations.Count} isolation violation(s) across {count} {what} case(s):");
        foreach (var violation in violations)
        {
            text.AppendLine().Append("- ").Append(violation);
        }

        return text.ToString();
    }

    private static string Diff(string before, string after)
    {
        var old = before.Split(Environment.NewLine).ToHashSet(StringComparer.Ordinal);
        var now = after.Split(Environment.NewLine);
        return string.Join(Environment.NewLine, now.Where(line => !old.Contains(line)).Select(line => "  changed " + line));
    }
}
