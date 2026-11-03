using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

[Trait("Category", "Docker")]
public sealed class QaS200Tests(PostgresTenancyFixture fixture)
{
    private sealed record World(TenancyApiFactory Api, HttpClient Client, string Database, Guid User, Guid Org, Guid Project)
        : IAsyncDisposable
    {
        internal Action<HttpRequestHeaders> Admin => TenancyApiFactory.Resolved(user: User, orgHeader: Org);

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
        }
    }

    private async Task<World> StartAsync()
    {
        var database = await fixture.SharedDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(fixture.AppConnectionString(database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, Unique.Slug("shapes"));
        var project = await CreateProjectAsync(client, user, org, "checkout");
        return new World(api, client, database, user, org, project);
    }

    private static (string Label, string Value)[] Hostile(string control, int maxLength) =>
    [
        ("empty", ""),
        ("one-over", new string('a', maxLength + 1)),
        ("huge", new string('a', 100_000)),
        ("trailing-newline", control + "\n"),
        ("crlf", control + "\r\n"),
        ("nul", control + "\0"),
        ("embedded-nul", "a\0" + control),
        ("cyrillic", "\u0430" + control),
        ("fullwidth", "\uFF41" + control),
        ("unicode-line-separator", control + "\u2028"),
        ("bidi", "\u202e" + control),
        ("zero-width", control + "\u200d"),
    ];

    [Fact]
    public async Task ProjectSlug_HostileCreateAndPatch_RefuseWithoutWriting()
    {
        await using var world = await StartAsync();
        var read = await world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{world.Project}", world.Admin);
        read.Status.ShouldBe(HttpStatusCode.OK);
        var failures = new List<string>();
        foreach (var (label, slug) in Hostile("ab", 63).Append(("one-character", "a")))
        {
            await RefusesAsync(() => world.Client.SendAsync(HttpMethod.Post, $"/v1/orgs/{world.Org}/projects",
                world.Admin, new { name = "Project", slug }, NewKey()), "create/" + label, failures);
            await RefusesAsync(() => world.Client.SendAsync(HttpMethod.Patch, $"/v1/projects/{world.Project}",
                world.Admin, new { slug }, NewKey(), read.ETag), "patch/" + label, failures);
        }

        var after = await world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{world.Project}", world.Admin);
        after.Text("slug").ShouldBe("checkout");
        after.ETag.ShouldBe(read.ETag);
        (await fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.project where org_id = '{world.Org}'"))
            .ShouldBe(1);
        foreach (var slug in new[] { "ab", new string('a', 63) })
        {
            (await world.Client.SendAsync(HttpMethod.Post, $"/v1/orgs/{world.Org}/projects",
                world.Admin, new { name = "Project", slug }, NewKey())).Status.ShouldBe(HttpStatusCode.Created);
        }

        (await world.Client.SendAsync(HttpMethod.Patch, $"/v1/projects/{world.Project}",
            world.Admin, new { slug = "patched" }, NewKey(), read.ETag)).Status.ShouldBe(HttpStatusCode.OK);
        failures.ShouldBeEmpty();
    }

    [Fact]
    public async Task ProjectCasePrefix_HostilePatch_RefusesWithoutChangingVersion()
    {
        await using var world = await StartAsync();
        var read = await world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{world.Project}", world.Admin);
        var failures = new List<string>();
        foreach (var (label, prefix) in Hostile("AB", 8).Append(("one-character", "A")))
        {
            await RefusesAsync(() => world.Client.SendAsync(HttpMethod.Patch, $"/v1/projects/{world.Project}",
                world.Admin, new { case_prefix = prefix }, NewKey(), read.ETag), label, failures);
        }

        var after = await world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{world.Project}", world.Admin);
        after.Text("case_prefix").ShouldBe(read.Text("case_prefix"));
        after.ETag.ShouldBe(read.ETag);
        (await world.Client.SendAsync(HttpMethod.Patch, $"/v1/projects/{world.Project}", world.Admin,
            new { case_prefix = "ABCDEFG1" }, NewKey(), read.ETag)).Status.ShouldBe(HttpStatusCode.OK);
        failures.ShouldBeEmpty();
    }

    [Fact]
    public async Task OrganizationDomains_HostileAndNullEntries_RefuseWithoutWriting()
    {
        await using var world = await StartAsync();
        var read = await world.Client.SendAsync(HttpMethod.Get, $"/v1/orgs/{world.Org}", world.Admin);
        var failures = new List<string>();
        var domains = Hostile("a.b", 253).Select(item => (item.Label, Domains: new string?[] { item.Value }))
            .Append(("null-entry", new string?[] { null }))
            .Append(("label-over-63", new string?[] { new string('a', 64) + ".b" }))
            .Append(("too-many", Enumerable.Repeat<string?>("a.b", 65).ToArray()));
        foreach (var (label, values) in domains)
        {
            await RefusesAsync(() => world.Client.SendAsync(HttpMethod.Patch, $"/v1/orgs/{world.Org}", world.Admin,
                new { allowed_email_domains = values }, NewKey(), read.ETag), label, failures);
        }

        var after = await world.Client.SendAsync(HttpMethod.Get, $"/v1/orgs/{world.Org}", world.Admin);
        after.ETag.ShouldBe(read.ETag);
        var maxDomain = string.Join('.', new string('a', 63), new string('b', 63), new string('c', 63), new string('d', 61));
        maxDomain.Length.ShouldBe(253);
        (await world.Client.SendAsync(HttpMethod.Patch, $"/v1/orgs/{world.Org}", world.Admin,
            new { allowed_email_domains = new[] { maxDomain } }, NewKey(), read.ETag)).Status.ShouldBe(HttpStatusCode.OK);
        failures.ShouldBeEmpty();
    }

    [Fact]
    public async Task SuiteSlug_HostileCreate_RefusesWithoutWriting()
    {
        await using var world = await StartAsync();
        var failures = new List<string>();
        foreach (var (label, slug) in Hostile("ab", 63).Append(("one-character", "a")))
        {
            await RefusesAsync(() => world.Client.SendAsync(HttpMethod.Post, $"/v1/projects/{world.Project}/suites",
                world.Admin, new { name = "Suite", slug }, NewKey()), label, failures);
        }

        (await fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.suite where org_id = '{world.Org}'"))
            .ShouldBe(0);
        foreach (var slug in new[] { "ab", new string('a', 63) })
        {
            (await world.Client.SendAsync(HttpMethod.Post, $"/v1/projects/{world.Project}/suites",
                world.Admin, new { name = "Suite", slug }, NewKey())).Status.ShouldBe(HttpStatusCode.Created);
        }

        failures.ShouldBeEmpty();
    }

    private static object Configuration(string reference = "secret:database") => new
    {
        base_urls = new Dictionary<string, string>(),
        dependency_versions = new Dictionary<string, string>(),
        secret_refs = new[] { reference },
    };

    [Fact]
    public async Task EnvironmentKey_HostileRoute_RefusesBeforeWriting()
    {
        await using var world = await StartAsync();
        var failures = new List<string>();
        // TestServer throws in its URI decoder for NUL paths, before the application can answer.
        // The body-based routes below still exercise NUL through HTTP.
        foreach (var (label, key) in Hostile("prod", 64)
            .Where(item => item.Label is not ("huge" or "empty" or "nul" or "embedded-nul"))
            .Append(("long-route", new string('a', 4096))))
        {
            await RefusesAsync(() => world.Client.SendAsync(HttpMethod.Post,
                $"/v1/projects/{world.Project}/environments/{Uri.EscapeDataString(key)}/revisions",
                world.Admin, Configuration(), NewKey()), label, failures, allowRoutingRefusal: true);
        }

        (await fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.environment where org_id = '{world.Org}'"))
            .ShouldBe(0);
        (await world.Client.SendAsync(HttpMethod.Post, $"/v1/projects/{world.Project}/environments/{new string('a', 64)}/revisions",
            world.Admin, Configuration(), NewKey())).Status.ShouldBe(HttpStatusCode.Created);
        failures.ShouldBeEmpty();
    }

    [Fact]
    public async Task EnvironmentSecretReference_HostileBody_RefusesWithoutWriting()
    {
        await using var world = await StartAsync();
        var failures = new List<string>();
        foreach (var (label, name) in Hostile("database", 64))
        {
            await RefusesAsync(() => world.Client.SendAsync(HttpMethod.Post,
                $"/v1/projects/{world.Project}/environments/prod/revisions", world.Admin,
                Configuration("secret:" + name), NewKey()), label, failures, allowBodyLimit: true);
        }

        (await fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.environment where org_id = '{world.Org}'"))
            .ShouldBe(0);
        (await world.Client.SendAsync(HttpMethod.Post, $"/v1/projects/{world.Project}/environments/prod/revisions",
            world.Admin, Configuration(), NewKey())).Status.ShouldBe(HttpStatusCode.Created);
        failures.ShouldBeEmpty();
    }

    [Fact]
    public async Task Manifest_HostileEnvironmentAndReference_RefuseWithoutWriting()
    {
        await using var world = await StartAsync();
        var cases = await ManifestTests.SeedCasesAsync(fixture, world.Api, world.Org, world.Project, world.Database, 1);
        var suite = await world.Client.SendAsync(HttpMethod.Post, $"/v1/projects/{world.Project}/suites",
            world.Admin, new { name = "Suite", slug = "manifest" }, NewKey());
        suite.Status.ShouldBe(HttpStatusCode.Created);
        var revision = await world.Client.SendAsync(HttpMethod.Post, $"/v1/suites/{suite.Id()}/revisions",
            world.Admin, new { members = cases.Select(id => new { case_revision_id = id }).ToArray() }, NewKey());
        revision.Status.ShouldBe(HttpStatusCode.Created);
        var revisionId = revision.Id("suite_revision_id");
        var control = ManifestUnitTests.Build(revisionId, cases, [new() { ["browser"] = "secret:database" }], ["prod"]);
        (await world.Client.SendAsync(HttpMethod.Post, $"/v1/projects/{world.Project}/manifests",
            world.Admin, control, NewKey())).Status.ShouldBe(HttpStatusCode.Created);
        var failures = new List<string>();
        foreach (var (label, name) in Hostile("database", 64))
        {
            var body = ManifestUnitTests.Build(revisionId, cases, [new() { ["browser"] = "secret:" + name }], ["prod"]);
            await RefusesAsync(() => world.Client.SendAsync(HttpMethod.Post, $"/v1/projects/{world.Project}/manifests",
                world.Admin, body, NewKey()), "reference/" + label, failures, allowBodyLimit: true);
        }

        var stored = await fixture.ScalarAsync<long>(world.Database,
            $"select count(*) from public.manifest where org_id = '{world.Org}'");
        if (stored != 1)
        {
            failures.Add($"hostile input changed manifest rows from 1 to {stored}");
        }
        failures.ShouldBeEmpty();
    }

    [Fact]
    public async Task AdapterName_HostileFirstSend_RefusesWithoutStoringEvents()
    {
        await using var world = await IngestTestWorld.StartAsync(fixture, IngestTestWorld.Writer());
        var failures = new List<string>();
        foreach (var (label, name) in Hostile("playwright", 64))
        {
            await RefusesAsync(() => IngestQaRequests.PostEventsAsync(world.Client, world.RunId, Carrier(world, name)),
                label, failures, allowBodyLimit: true);
        }

        (await world.CountAsync("event", $"org_id = '{world.Org}' and run_id = '{world.RunId}'")).ShouldBe(0);
        (await world.CountAsync("adapter_capability", $"org_id = '{world.Org}'")).ShouldBe(0);
        (await IngestQaRequests.PostEventsAsync(world.Client, world.RunId, Carrier(world, "playwright")))
            .Status.ShouldBe(HttpStatusCode.OK);
        (await world.CountAsync("adapter_capability", $"org_id = '{world.Org}'")).ShouldBe(1);
        failures.ShouldBeEmpty();
    }

    private static object Carrier(IngestTestWorld world, string name)
    {
        var capability = new
        {
            protocol = new { major = 1, minor = 0 },
            platforms = new[] { "linux-arm64" },
            supports = new { discovery = true, step_events = true, retry = true, cancel = true },
            mode = "full",
            engine = new { family = "playwright", version = "1.49.0" },
        };
        return IngestQaRequests.Batch(IngestQaRequests.Event(Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 0), 1,
            type: "run.log", status: null, ext: new { schema = name + "/1", capability }));
    }

    private static async Task RefusesAsync(Func<Task<ApiAnswer>> send, string label, List<string> failures,
        bool allowRoutingRefusal = false, bool allowBodyLimit = false)
    {
        var watch = Stopwatch.StartNew();
        var answer = await send();
        watch.Stop();
        var refused = answer.Status == HttpStatusCode.UnprocessableEntity
            || (allowRoutingRefusal && answer.Status is HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
            || (allowBodyLimit && answer.Status == HttpStatusCode.RequestEntityTooLarge);
        if (!refused || watch.Elapsed > TimeSpan.FromSeconds(5))
        {
            failures.Add($"{label}: HTTP {(int)answer.Status}, code={answer.ErrorCode}, elapsed={watch.Elapsed}");
        }
    }
}
