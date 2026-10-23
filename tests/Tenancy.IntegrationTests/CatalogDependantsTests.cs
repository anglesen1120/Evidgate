// Request bodies are written inline so each test reads as the request it sends; the arrays are tiny and built once per test.
#pragma warning disable CA1861
using System.Net;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Catalog.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 05.3.3-T2.2: <see cref="ICatalogContracts.GetDependantsAsync"/> — the transitive dependants of a suite member over
/// <c>case_dependency</c> — over a real PostgreSQL and the real host: a direct edge, a transitive chain, a diamond, the
/// root excluded, per-variant and per-revision scoping, tenant isolation, a manually planted cycle and one statement
/// however large the graph.
/// </summary>
[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class CatalogDependantsTests
{
    private const string Commit1 = "aaaaaaa1111111";

    private readonly PostgresTenancyFixture _fixture;

    public CatalogDependantsTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private sealed record World(TenancyApiFactory Api, HttpClient Client, string Database, Guid User, Guid Org, Guid Project, Guid Suite, Guid[] Revisions)
        : IAsyncDisposable
    {
        public Action<System.Net.Http.Headers.HttpRequestHeaders> Admin => TenancyApiFactory.Resolved(user: User, orgHeader: Org);

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
        }
    }

    /// <summary>An org with a project, <paramref name="cases"/> registered cases (one revision each) and an empty suite.</summary>
    private async Task<World> StartAsync(int cases = 6)
    {
        var database = await _fixture.CreateDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, "acme");
        var project = await CreateProjectAsync(client, user, org, "checkout");
        var revisions = await SeedCasesAsync(api, org, project, database, cases, 0);

        var admin = TenancyApiFactory.Resolved(user: user, orgHeader: org);
        var suite = await client.SendAsync(HttpMethod.Post, $"/v1/projects/{project}/suites", admin, new { name = "Smoke", slug = "smoke" }, NewKey());
        suite.Status.ShouldBe(HttpStatusCode.Created, suite.ErrorMessage);

        return new World(api, client, database, user, org, project, suite.Id(), revisions);
    }

    private async Task<Guid[]> SeedCasesAsync(TenancyApiFactory api, Guid org, Guid project, string database, int count, int offset)
    {
        var files = Enumerable.Range(offset, count).Select(i => new SourceFile(
            $"t/{i}.spec.ts",
            $"// evidgate.id: 018f2f0e-8a52-7c4b-9d3e-{i:D12}\ntest('{i}', () => {{ expect(a).toBe({i}); }});",
            "playwright",
            "typescript")).ToArray();

        await using var services = api.Services.CreateAsyncScope();
        var scopes = services.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
        await using var scope = await scopes.BeginAsync(org, null, false, TestContext.Current.CancellationToken);
        var outcome = await services.ServiceProvider.GetRequiredService<ICaseIdentityService>()
            .RegisterDiscoveredAsync(project, Commit1, files, TestContext.Current.CancellationToken);
        outcome.Succeeded.ShouldBeTrue(outcome.Failure);
        await scope.CommitAsync(TestContext.Current.CancellationToken);

        var ids = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            ids.Add(await _fixture.ScalarAsync<Guid>(
                database,
                $"select r.id from public.test_revision r join public.test_case c on c.org_id = r.org_id and c.id = r.case_id where c.project_id = '{project}' and c.file_path = 't/{offset + i}.spec.ts'"));
        }

        return [.. ids];
    }

    private static object Member(Guid revision, object? parameters = null, string? reference = null) =>
        new { @ref = reference, case_revision_id = revision, @params = parameters };

    private static async Task<Guid> CreateRevisionAsync(World world, object body)
    {
        var created = await world.Client.SendAsync(HttpMethod.Post, $"/v1/suites/{world.Suite}/revisions", world.Admin, body, NewKey());
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);

        return created.Id("suite_revision_id");
    }

    private Task<string> VariantHashAsync(World world, Guid revision, Guid caseRevision) =>
        _fixture.ScalarAsync<string>(
            world.Database,
            $"select v.variant_hash from public.suite_member m join public.variant v on v.org_id = m.org_id and v.id = m.variant_id where m.suite_revision_id = '{revision}' and m.case_revision_id = '{caseRevision}'");

    private Task<string> VariantHashForBrowserAsync(World world, Guid revision, Guid caseRevision, string browser) =>
        _fixture.ScalarAsync<string>(
            world.Database,
            $"select v.variant_hash from public.suite_member m join public.variant v on v.org_id = m.org_id and v.id = m.variant_id where m.suite_revision_id = '{revision}' and m.case_revision_id = '{caseRevision}' and v.params ->> 'browser' = '{browser}'");

    /// <summary>Runs the read under <paramref name="org"/>'s scope and reports how many <c>public.</c> statements it issued.</summary>
    private static async Task<(IReadOnlyList<DependantMember> Members, int Statements)> GetDependantsAsync(
        World world, Guid org, Guid suiteRevision, Guid caseRevision, string variantHash)
    {
        await using var services = world.Api.Services.CreateAsyncScope();
        var scopes = services.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
        await using var scope = await scopes.BeginAsync(org, null, false, TestContext.Current.CancellationToken);
        var contracts = services.ServiceProvider.GetRequiredService<ICatalogContracts>();

        world.Api.Statements.Clear();
        var members = await contracts.GetDependantsAsync(suiteRevision, caseRevision, variantHash, TestContext.Current.CancellationToken);
        var statements = world.Api.Statements.Snapshot().Count(statement => statement.Contains("public.", StringComparison.Ordinal));

        await scope.CommitAsync(TestContext.Current.CancellationToken);
        return (members, statements);
    }

    private static async Task<IReadOnlyList<DependantMember>> DependantsAsync(World world, Guid revision, Guid caseRevision, string variantHash) =>
        (await GetDependantsAsync(world, world.Org, revision, caseRevision, variantHash)).Members;

    // ------------------------------------------------------------------ the walk

    [Fact]
    public async Task GetDependantsAsync_ReturnsTheDirectDependant()
    {
        await using var world = await StartAsync();
        var revision = await CreateRevisionAsync(world, new
        {
            members = new[] { Member(world.Revisions[0], null, "setup"), Member(world.Revisions[1], null, "dependent") },
            edges = new[] { new { parent = "setup", child = "dependent" } },
        });

        var dependants = await DependantsAsync(world, revision, world.Revisions[0], await VariantHashAsync(world, revision, world.Revisions[0]));

        dependants.Select(member => member.CaseRevisionId).ShouldBe([world.Revisions[1]]);
        dependants.Single().VariantHash.ShouldBe(await VariantHashAsync(world, revision, world.Revisions[1]));
    }

    [Fact]
    public async Task GetDependantsAsync_ReturnsTheWholeTransitiveChain()
    {
        await using var world = await StartAsync();
        var revision = await CreateRevisionAsync(world, new
        {
            members = new[] { Member(world.Revisions[0], null, "a"), Member(world.Revisions[1], null, "b"), Member(world.Revisions[2], null, "c") },
            edges = new[] { new { parent = "a", child = "b" }, new { parent = "b", child = "c" } },
        });

        var dependants = await DependantsAsync(world, revision, world.Revisions[0], await VariantHashAsync(world, revision, world.Revisions[0]));

        dependants.Select(member => member.CaseRevisionId).ShouldBe([world.Revisions[1], world.Revisions[2]], ignoreOrder: true);
    }

    [Fact]
    public async Task GetDependantsAsync_ReturnsEachDependantOfADiamondOnce()
    {
        await using var world = await StartAsync();
        var revision = await CreateRevisionAsync(world, new
        {
            members = new[]
            {
                Member(world.Revisions[0], null, "a"), Member(world.Revisions[1], null, "b"),
                Member(world.Revisions[2], null, "c"), Member(world.Revisions[3], null, "d"),
            },
            edges = new[]
            {
                new { parent = "a", child = "b" }, new { parent = "a", child = "c" },
                new { parent = "b", child = "d" }, new { parent = "c", child = "d" },
            },
        });

        var dependants = await DependantsAsync(world, revision, world.Revisions[0], await VariantHashAsync(world, revision, world.Revisions[0]));

        dependants.Select(member => member.CaseRevisionId).ShouldBe([world.Revisions[1], world.Revisions[2], world.Revisions[3]], ignoreOrder: true);
        dependants.Count.ShouldBe(3);
    }

    [Fact]
    public async Task GetDependantsAsync_NeverReturnsTheRoot()
    {
        await using var world = await StartAsync();
        var chain = await CreateRevisionAsync(world, new
        {
            members = new[] { Member(world.Revisions[0], null, "a"), Member(world.Revisions[1], null, "b"), Member(world.Revisions[2], null, "c") },
            edges = new[] { new { parent = "a", child = "b" }, new { parent = "b", child = "c" } },
        });
        var lone = await CreateRevisionAsync(world, new { members = new[] { Member(world.Revisions[0], null, "a") } });

        var dependants = await DependantsAsync(world, chain, world.Revisions[0], await VariantHashAsync(world, chain, world.Revisions[0]));

        dependants.ShouldNotContain(member => member.CaseRevisionId == world.Revisions[0]);

        // A member with no dependants has an empty answer.
        (await DependantsAsync(world, lone, world.Revisions[0], await VariantHashAsync(world, lone, world.Revisions[0]))).ShouldBeEmpty();
    }

    [Fact]
    public async Task GetDependantsAsync_LeavesAnIndependentMemberOut()
    {
        await using var world = await StartAsync();
        var revision = await CreateRevisionAsync(world, new
        {
            members = new[] { Member(world.Revisions[0], null, "a"), Member(world.Revisions[1], null, "b"), Member(world.Revisions[2], null, "c") },
            edges = new[] { new { parent = "a", child = "b" } },
        });

        var dependants = await DependantsAsync(world, revision, world.Revisions[0], await VariantHashAsync(world, revision, world.Revisions[0]));

        dependants.Select(member => member.CaseRevisionId).ShouldBe([world.Revisions[1]]);
        dependants.ShouldNotContain(member => member.CaseRevisionId == world.Revisions[2]);
    }

    // ------------------------------------------------------------------ scoping

    [Fact]
    public async Task GetDependantsAsync_FollowsOnlyTheRequestedVariantOfTheSameCaseRevision()
    {
        await using var world = await StartAsync();
        var revision = await CreateRevisionAsync(world, new
        {
            members = new[]
            {
                Member(world.Revisions[0], new { browser = "chromium" }, "a"),
                Member(world.Revisions[0], new { browser = "firefox" }, "b"),
                Member(world.Revisions[1], null, "x"),
                Member(world.Revisions[2], null, "y"),
            },
            edges = new[] { new { parent = "a", child = "x" }, new { parent = "b", child = "y" } },
        });

        var chromium = await DependantsAsync(world, revision, world.Revisions[0], await VariantHashForBrowserAsync(world, revision, world.Revisions[0], "chromium"));
        var firefox = await DependantsAsync(world, revision, world.Revisions[0], await VariantHashForBrowserAsync(world, revision, world.Revisions[0], "firefox"));

        chromium.Select(member => member.CaseRevisionId).ShouldBe([world.Revisions[1]]);
        firefox.Select(member => member.CaseRevisionId).ShouldBe([world.Revisions[2]]);
    }

    [Fact]
    public async Task GetDependantsAsync_IgnoresAMemberOfAnotherSuiteRevision()
    {
        await using var world = await StartAsync();
        var mine = await CreateRevisionAsync(world, new
        {
            members = new[] { Member(world.Revisions[0], null, "a"), Member(world.Revisions[1], null, "b") },
            edges = new[] { new { parent = "a", child = "b" } },
        });
        var elsewhere = await CreateRevisionAsync(world, new
        {
            members = new[] { Member(world.Revisions[0], null, "a"), Member(world.Revisions[5], null, "e") },
            edges = new[] { new { parent = "a", child = "e" } },
        });

        var dependants = await DependantsAsync(world, mine, world.Revisions[0], await VariantHashAsync(world, mine, world.Revisions[0]));

        dependants.Select(member => member.CaseRevisionId).ShouldBe([world.Revisions[1]]);
        dependants.ShouldNotContain(member => member.CaseRevisionId == world.Revisions[5]);
        elsewhere.ShouldNotBe(mine);
    }

    [Fact]
    public async Task GetDependantsAsync_UnknownRevisionOrVariantHash_IsEmpty()
    {
        await using var world = await StartAsync();
        var revision = await CreateRevisionAsync(world, new
        {
            members = new[] { Member(world.Revisions[0], null, "a"), Member(world.Revisions[1], null, "b") },
            edges = new[] { new { parent = "a", child = "b" } },
        });
        var hash = await VariantHashAsync(world, revision, world.Revisions[0]);

        (await DependantsAsync(world, Guid.CreateVersion7(), world.Revisions[0], hash)).ShouldBeEmpty();
        (await DependantsAsync(world, revision, world.Revisions[0], "sha256:" + new string('9', 64))).ShouldBeEmpty();
    }

    [Fact]
    public async Task GetDependantsAsync_AnotherTenantsRevision_IsEmpty()
    {
        await using var world = await StartAsync();
        var mine = await CreateRevisionAsync(world, new
        {
            members = new[] { Member(world.Revisions[0], null, "a"), Member(world.Revisions[1], null, "b") },
            edges = new[] { new { parent = "a", child = "b" } },
        });

        var otherUser = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(world.Client, otherUser, "rival");
        var otherProject = await CreateProjectAsync(world.Client, otherUser, otherOrg, "theirs");
        var otherRevisions = await SeedCasesAsync(world.Api, otherOrg, otherProject, world.Database, 2, 300);
        var otherAdmin = TenancyApiFactory.Resolved(user: otherUser, orgHeader: otherOrg);
        var otherSuite = await world.Client.SendAsync(HttpMethod.Post, $"/v1/projects/{otherProject}/suites", otherAdmin, new { name = "Rival", slug = "rival" }, NewKey());
        otherSuite.Status.ShouldBe(HttpStatusCode.Created, otherSuite.ErrorMessage);
        var otherAnswer = await world.Client.SendAsync(
            HttpMethod.Post,
            $"/v1/suites/{otherSuite.Id()}/revisions",
            otherAdmin,
            new { members = new[] { Member(otherRevisions[0], null, "a"), Member(otherRevisions[1], null, "b") }, edges = new[] { new { parent = "a", child = "b" } } },
            NewKey());
        otherAnswer.Status.ShouldBe(HttpStatusCode.Created, otherAnswer.ErrorMessage);
        var otherRevision = otherAnswer.Id("suite_revision_id");
        var otherHash = await VariantHashAsync(world, otherRevision, otherRevisions[0]);

        // The other tenant's revision under this tenant's scope, and this tenant's revision under the other's: both empty.
        (await DependantsAsync(world, otherRevision, otherRevisions[0], otherHash)).ShouldBeEmpty();
        (await GetDependantsAsync(world, otherOrg, mine, world.Revisions[0], await VariantHashAsync(world, mine, world.Revisions[0])))
            .Members.ShouldBeEmpty();
    }

    // ------------------------------------------------------------------ a planted cycle and the statement budget

    [Fact]
    public async Task GetDependantsAsync_OverAPlantedCycle_TerminatesAndExcludesTheRoot()
    {
        await using var world = await StartAsync(cases: 3);
        var variant = Guid.CreateVersion7();
        var revision = Guid.CreateVersion7();
        var a = Guid.CreateVersion7();
        var b = Guid.CreateVersion7();
        var c = Guid.CreateVersion7();

        // The API refuses a cycle at write time (03.2.1-T3), so it is planted directly as the owner: one batch is one
        // transaction, which is what lets the insert guard accept members of a revision created in the same transaction.
        await _fixture.ExecuteAsync(
            world.Database,
            $"""
            insert into public.variant (id, org_id, project_id, variant_hash, params)
            values ('{variant}', '{world.Org}', '{world.Project}', 'sha256:{new string('e', 64)}', jsonb_build_object());
            insert into public.suite_revision (id, org_id, suite_id, revision_no, revision_hash, shard_strategy, retry_policy)
            values ('{revision}', '{world.Org}', '{world.Suite}', 1, 'sha256:{new string('f', 64)}', jsonb_build_object(), jsonb_build_object());
            insert into public.suite_member (id, org_id, suite_revision_id, case_revision_id, variant_id, ordinal)
            values ('{a}', '{world.Org}', '{revision}', '{world.Revisions[0]}', '{variant}', 0),
                   ('{b}', '{world.Org}', '{revision}', '{world.Revisions[1]}', '{variant}', 1),
                   ('{c}', '{world.Org}', '{revision}', '{world.Revisions[2]}', '{variant}', 2);
            insert into public.case_dependency (id, org_id, suite_revision_id, parent_member_id, child_member_id)
            values ('{Guid.CreateVersion7()}', '{world.Org}', '{revision}', '{a}', '{b}'),
                   ('{Guid.CreateVersion7()}', '{world.Org}', '{revision}', '{b}', '{c}'),
                   ('{Guid.CreateVersion7()}', '{world.Org}', '{revision}', '{c}', '{a}');
            """);

        var dependants = await DependantsAsync(world, revision, world.Revisions[0], "sha256:" + new string('e', 64));

        dependants.Select(member => member.CaseRevisionId).ShouldBe([world.Revisions[1], world.Revisions[2]], ignoreOrder: true);
    }

    [Fact]
    public async Task GetDependantsAsync_IssuesOneStatementRegardlessOfGraphSize()
    {
        await using var world = await StartAsync(cases: 41);
        var small = await CreateRevisionAsync(world, new
        {
            members = Enumerable.Range(0, 3).Select(i => Member(world.Revisions[i], null, $"m{i}")).ToArray(),
            edges = Enumerable.Range(1, 2).Select(i => new { parent = $"m{i - 1}", child = $"m{i}" }).ToArray(),
        });
        var large = await CreateRevisionAsync(world, new
        {
            members = Enumerable.Range(0, 40).Select(i => Member(world.Revisions[i], null, $"m{i}")).ToArray(),
            edges = Enumerable.Range(1, 39).Select(i => new { parent = $"m{i - 1}", child = $"m{i}" }).ToArray(),
        });

        var (smallMembers, smallStatements) = await GetDependantsAsync(world, world.Org, small, world.Revisions[0], await VariantHashAsync(world, small, world.Revisions[0]));
        var (largeMembers, largeStatements) = await GetDependantsAsync(world, world.Org, large, world.Revisions[0], await VariantHashAsync(world, large, world.Revisions[0]));

        smallMembers.Count.ShouldBe(2);
        largeMembers.Count.ShouldBe(39);
        smallStatements.ShouldBe(1);
        largeStatements.ShouldBe(smallStatements);
    }
}
