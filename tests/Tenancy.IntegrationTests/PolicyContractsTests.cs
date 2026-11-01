using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Policy.Application;
using Evidgate.Modules.Policy.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 07.1.1-T4.2: <see cref="IPolicyContracts.GetActivePolicyAsync"/> against a real PostgreSQL — the in-process
/// surface run creation calls to pin a policy (FR-07-2, ruling T93). A project with no activation (or a
/// soft-deleted project) answers null and keeps the caller's defaults; an activated project answers the
/// revision, its hash and the two body fields run creation copies, with SQL null when the body omits a field.
/// <para>
/// 05.3.2-T2 reads the same surface for the body's <c>required_artifacts</c>: <see cref="IPolicyContracts.GetRequiredArtifactKindsAsync"/>
/// answers the kinds ordinal and de-duplicated, and an unknown, invisible (another tenant's) or field-less
/// revision answers an empty list rather than an error.
/// </para>
/// </summary>
/// <remarks>
/// The state is written through the real activation store inside its own tenant transaction (the write path
/// <c>PolicyActivationTests</c> drives over HTTP), and the read goes through the module's registered
/// <see cref="IPolicyContracts"/>, so both halves are the production ones.
/// </remarks>
[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class PolicyContractsTests
{
    /// <summary>The kinds the ordinal read orders <c>["trace","junit_xml","log"]</c> into (CA1861: no literal arrays as arguments).</summary>
    private static readonly string[] OrdinalKinds = ["junit_xml", "log", "trace"];

    /// <summary>The de-duplicated kinds a body with <c>["trace","log","trace"]</c> answers.</summary>
    private static readonly string[] DeduplicatedKinds = ["log", "trace"];

    /// <summary>The single kind the control revisions require.</summary>
    private static readonly string[] TraceOnly = ["trace"];

    private readonly PostgresTenancyFixture _fixture;

    public PolicyContractsTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private sealed record World(TenancyApiFactory Api, HttpClient Client, string Database, Guid User, Guid Org, Guid Project) : IAsyncDisposable
    {
        public Action<HttpRequestHeaders> Admin => TenancyApiFactory.Resolved(user: User, orgHeader: Org);

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
        }
    }

    private async Task<World> StartAsync()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, "acme");
        var project = await CreateProjectAsync(client, user, org, "checkout");

        return new World(api, client, database, user, org, project);
    }

    /// <summary>Appends a revision through the documented route and returns its id and stored hash.</summary>
    private static async Task<(Guid Id, string Hash)> CreateRevisionAsync(World world, string body)
    {
        var created = await SendRawAsync(
            world.Client,
            HttpMethod.Post,
            $"/v1/projects/{world.Project}/policies",
            world.Admin,
            body);
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);

        return (created.Id(), created.Body.GetProperty("hash").GetString()!);
    }

    /// <summary>Activates a revision through the real store, in its own tenant transaction.</summary>
    private static async Task ActivateAsync(World world, Guid revisionId)
    {
        await using var services = world.Api.Services.CreateAsyncScope();
        var scopes = services.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
        await using var scope = await scopes.BeginAsync(world.Org, null, false, TestContext.Current.CancellationToken);
        var store = services.ServiceProvider.GetRequiredService<PolicyActivationStore>();

        var result = await store.ActivateAsync(world.Project, revisionId, "advisory", world.User, TestContext.Current.CancellationToken);
        result.IsSuccess.ShouldBeTrue($"activation failed: {result.Error?.Status} {result.Error?.Message}");
        await scope.CommitAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Reads the project's active policy through the module's registered contracts surface.</summary>
    private static async Task<ActivePolicy?> ReadAsync(World world, Guid projectId)
    {
        await using var services = world.Api.Services.CreateAsyncScope();
        var scopes = services.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
        await using var scope = await scopes.BeginAsync(world.Org, null, false, TestContext.Current.CancellationToken);
        var contracts = services.ServiceProvider.GetRequiredService<IPolicyContracts>();

        var policy = await contracts.GetActivePolicyAsync(projectId, TestContext.Current.CancellationToken);
        await scope.CommitAsync(TestContext.Current.CancellationToken);

        return policy;
    }

    /// <summary>Reads a revision's required artifact kinds through the module's registered contracts surface.</summary>
    private static async Task<IReadOnlyList<string>> ReadKindsAsync(World world, Guid revisionId)
    {
        await using var services = world.Api.Services.CreateAsyncScope();
        var scopes = services.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
        await using var scope = await scopes.BeginAsync(world.Org, null, false, TestContext.Current.CancellationToken);
        var contracts = services.ServiceProvider.GetRequiredService<IPolicyContracts>();

        var kinds = await contracts.GetRequiredArtifactKindsAsync(revisionId, TestContext.Current.CancellationToken);
        await scope.CommitAsync(TestContext.Current.CancellationToken);

        return kinds;
    }

    /// <summary>
    /// Writes a revision straight into the table as owner, past the route's schema: the only way to store a body
    /// the contract refuses (duplicate items, a non-array <c>required_artifacts</c>) and prove the read still
    /// answers a list rather than raising (the owner policy of <c>policy_revision</c> allows it).
    /// </summary>
    private async Task<Guid> InsertRevisionAsOwnerAsync(World world, string body)
    {
        var revision = Guid.CreateVersion7();

        await using (var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database)))
        {
            await owner.ExecuteAsync(
                """
                insert into public.policy_revision (id, org_id, project_id, revision_no, hash, body, created_by)
                values (@id, @org_id, @project_id,
                        (select coalesce(max(revision_no), 0) + 1
                           from public.policy_revision
                          where org_id = @org_id and project_id = @project_id),
                        @hash, @body::jsonb, @created_by)
                """,
                ("id", revision),
                ("org_id", world.Org),
                ("project_id", world.Project),
                ("hash", "sha256:" + new string('b', 64)),
                ("body", body),
                ("created_by", world.User));
            await owner.CommitAsync();
        }

        return revision;
    }

    /// <summary>Sends a raw body verbatim, the shape the policy routes take.</summary>
    private static async Task<ApiAnswer> SendRawAsync(
        HttpClient client,
        HttpMethod method,
        string url,
        Action<HttpRequestHeaders> caller,
        string raw)
    {
        using var request = new HttpRequestMessage(method, url);
        caller(request.Headers);
        request.Content = new StringContent(raw, Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(string.IsNullOrEmpty(text) ? "{}" : text);

        return new ApiAnswer(response.StatusCode, document.RootElement.Clone(), response.Headers);
    }

    // ------------------------------------------------------------------ null

    [Fact]
    public async Task NoActivation_ReturnsNull()
    {
        await using var world = await StartAsync();
        await CreateRevisionAsync(world, """{"flaky_budget":3}""");

        (await ReadAsync(world, world.Project)).ShouldBeNull();
    }

    [Fact]
    public async Task DeletedProject_ReturnsNull()
    {
        await using var world = await StartAsync();
        var (revision, _) = await CreateRevisionAsync(world, """{"flaky_budget":3}""");
        await ActivateAsync(world, revision);

        await using (var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database)))
        {
            (await owner.ExecuteAsync($"update public.project set status = 'deleted' where id = '{world.Project}'")).ShouldBe(1);
            await owner.CommitAsync();
        }

        (await ReadAsync(world, world.Project)).ShouldBeNull();
        // The pointer row is untouched; the read's project guard is what answers null.
        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.policy_activation")).ShouldBe(1);
    }

    // ------------------------------------------------------------------ the values

    [Fact]
    public async Task BodyFieldsAndHash_AreReturned_WhenTheBodySetsThem()
    {
        await using var world = await StartAsync();
        var (revision, hash) = await CreateRevisionAsync(
            world,
            """{"flaky_budget":3,"collection_timeout_minutes":30,"late_window_minutes":0}""");
        await ActivateAsync(world, revision);

        var policy = await ReadAsync(world, world.Project);

        policy.ShouldNotBeNull();
        policy!.PolicyRevisionId.ShouldBe(revision);
        policy.Hash.ShouldBe(hash);
        policy.CollectionTimeoutMinutes.ShouldBe(30);
        // 0 is the widened bound of the 0033 CHECK (owner decision Q4): it is a value, not "unset".
        policy.LateWindowMinutes.ShouldBe(0);
    }

    [Fact]
    public async Task BodyFields_AreNull_WhenTheBodyOmitsThem()
    {
        await using var world = await StartAsync();
        var (revision, hash) = await CreateRevisionAsync(world, """{"flaky_budget":3}""");
        await ActivateAsync(world, revision);

        var policy = await ReadAsync(world, world.Project);

        policy.ShouldNotBeNull();
        policy!.PolicyRevisionId.ShouldBe(revision);
        policy.Hash.ShouldBe(hash);
        policy.CollectionTimeoutMinutes.ShouldBeNull();
        policy.LateWindowMinutes.ShouldBeNull();
    }

    // ------------------------------------------------------------------ the project boundary

    [Fact]
    public async Task NeverReturnsAnotherProjectsRevision()
    {
        await using var world = await StartAsync();
        var otherProject = await CreateProjectAsync(world.Client, world.User, world.Org, "other");
        var (mine, _) = await CreateRevisionAsync(world, """{"flaky_budget":3}""");
        await ActivateAsync(world, mine);

        // The other project has no activation of its own: the pointer is per project.
        (await ReadAsync(world, otherProject)).ShouldBeNull();

        // And activating there does not leak into the first project's answer.
        var theirs = await SendRawAsync(
            world.Client,
            HttpMethod.Post,
            $"/v1/projects/{otherProject}/policies",
            world.Admin,
            """{"flaky_budget":9}""");
        theirs.Status.ShouldBe(HttpStatusCode.Created, theirs.ErrorMessage);
        await ActivateAsync(world with { Project = otherProject }, theirs.Id());

        (await ReadAsync(world, world.Project))!.PolicyRevisionId.ShouldBe(mine);
        (await ReadAsync(world, otherProject))!.PolicyRevisionId.ShouldBe(theirs.Id());
    }

    /// <summary>
    /// Finding F3 (defence in depth): the pointer's foreign key is org-scoped, so a pointer to another project's
    /// revision is representable. The read still answers null for it, because the revision is joined on the pointer's
    /// own project. The pointer is inserted as owner, bypassing the store's own project predicate.
    /// </summary>
    [Fact]
    public async Task PointerToAnotherProjectsRevision_ReturnsNull()
    {
        await using var world = await StartAsync();
        var otherProject = await CreateProjectAsync(world.Client, world.User, world.Org, "other");

        // This project owns the revision; the other project's pointer is made to name it directly.
        var (mine, _) = await CreateRevisionAsync(world, """{"flaky_budget":3}""");

        await using (var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database)))
        {
            await owner.ExecuteAsync(
                """
                insert into public.policy_activation (id, org_id, project_id, policy_revision_id, mode, activated_by)
                values (gen_random_uuid(), @org_id, @project_id, @policy_revision_id, 'advisory', @activated_by)
                """,
                ("org_id", world.Org),
                ("project_id", otherProject),
                ("policy_revision_id", mine),
                ("activated_by", world.User));
            await owner.CommitAsync();
        }

        // The pointer is org-valid but project-mismatched, so the other project has no active policy.
        (await ReadAsync(world, otherProject)).ShouldBeNull();
        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.policy_activation")).ShouldBe(1);
    }

    // ------------------------------------------------------------------ the required artifact kinds

    [Fact]
    public async Task RequiredArtifactKinds_AreReturnedInOrdinalOrder()
    {
        await using var world = await StartAsync();
        var (revision, _) = await CreateRevisionAsync(
            world,
            """{"flaky_budget":3,"required_artifacts":["trace","junit_xml","log"]}""");

        // Written unsorted, answered ordinal: the caller compares the list as it comes.
        (await ReadKindsAsync(world, revision)).ShouldBe(OrdinalKinds, ignoreOrder: false);
    }

    /// <summary>
    /// The route refuses duplicate items (<c>uniqueItems</c>), so a body with a repeated kind is only representable
    /// when a row is written past the schema (owner insert here). The read still answers each kind once.
    /// </summary>
    [Fact]
    public async Task RequiredArtifactKinds_AreDeduplicated()
    {
        await using var world = await StartAsync();
        var revision = await InsertRevisionAsOwnerAsync(world, """{"required_artifacts":["trace","log","trace"]}""");

        (await ReadKindsAsync(world, revision)).ShouldBe(DeduplicatedKinds, ignoreOrder: false);
    }

    [Fact]
    public async Task RequiredArtifactKinds_AreEmpty_WhenTheBodyOmitsTheField()
    {
        await using var world = await StartAsync();
        var (revision, _) = await CreateRevisionAsync(world, """{"flaky_budget":3}""");

        (await ReadKindsAsync(world, revision)).ShouldBeEmpty();
    }

    /// <summary>
    /// The schema types <c>required_artifacts</c> as an array, but the body column has no such check: a row written
    /// past the route with a scalar must answer empty, not raise 22023 from <c>jsonb_array_elements_text</c>.
    /// </summary>
    [Fact]
    public async Task RequiredArtifactKinds_AreEmpty_WhenTheFieldIsNotAnArray()
    {
        await using var world = await StartAsync();
        var revision = await InsertRevisionAsOwnerAsync(world, """{"required_artifacts":"trace"}""");

        (await ReadKindsAsync(world, revision)).ShouldBeEmpty();
    }

    [Fact]
    public async Task RequiredArtifactKinds_AreEmpty_ForAnUnknownRevision()
    {
        await using var world = await StartAsync();
        var (revision, _) = await CreateRevisionAsync(world, """{"required_artifacts":["trace"]}""");

        (await ReadKindsAsync(world, Guid.CreateVersion7())).ShouldBeEmpty();
        // The known revision still answers, so the empty list is the unknown id and not a broken read.
        (await ReadKindsAsync(world, revision)).ShouldBe(TraceOnly, ignoreOrder: false);
    }

    [Fact]
    public async Task RequiredArtifactKinds_AreEmpty_ForAnotherTenantsRevision()
    {
        await using var world = await StartAsync();
        var otherOrg = await CreateOrgAsync(world.Client, world.User, "other");
        var otherProject = await CreateProjectAsync(world.Client, world.User, otherOrg, "checkout");
        var other = world with { Org = otherOrg, Project = otherProject };
        var (theirs, _) = await CreateRevisionAsync(other, """{"required_artifacts":["trace"]}""");

        // Read under the first tenant's scope: RLS hides the row and the read's own org_id match is the module guard.
        (await ReadKindsAsync(world, theirs)).ShouldBeEmpty();
        // The owning tenant reads it, so the empty list is the tenant boundary and not a missing row.
        (await ReadKindsAsync(other, theirs)).ShouldBe(TraceOnly, ignoreOrder: false);
    }

    // ------------------------------------------------------------------ the live evaluation of a revision

    [Fact]
    public async Task LiveEvaluations_AreReturnedForTheNamedRevisionsOnly()
    {
        await using var world = await StartAsync();
        var (revision, hash) = await CreateRevisionAsync(world, """{"flaky_budget":3}""");
        var (runId, first, second) = await SeedRunAndRevisionsAsync(world);
        var live = await InsertEvaluationAsync(world, runId, revision, hash, first, "live", "PASS");

        var found = await ReadLiveAsync(world, runId, [first, second]);

        // The one evaluated revision answers with its own verdict; the other is simply absent.
        found.ShouldHaveSingleItem();
        found[0].EvidenceRevision.ShouldBe(first);
        found[0].EvaluationId.ShouldBe(live);
    }

    /// <summary>
    /// A <c>shadow</c> row is a second opinion about the same evidence, so it is never named: the live row is the
    /// answer even when a shadow row for the same revision exists, and a shadow-only revision is absent.
    /// </summary>
    [Fact]
    public async Task LiveEvaluations_NeverNameAShadowRow()
    {
        await using var world = await StartAsync();
        var (revision, hash) = await CreateRevisionAsync(world, """{"flaky_budget":3}""");
        var (runId, first, second) = await SeedRunAndRevisionsAsync(world);
        var live = await InsertEvaluationAsync(world, runId, revision, hash, first, "live", "PASS");
        await InsertEvaluationAsync(world, runId, revision, hash, first, "shadow", "FAIL");
        await InsertEvaluationAsync(world, runId, revision, hash, second, "shadow", "FAIL");

        var found = await ReadLiveAsync(world, runId, [first, second]);

        found.ShouldHaveSingleItem();
        found[0].EvaluationId.ShouldBe(live, "the live verdict is the run's decision, never the shadow one");
    }

    /// <summary>The read is bound to its run and to the caller's tenant: another run's verdict and another org's are invisible.</summary>
    [Fact]
    public async Task LiveEvaluations_AreEmptyForAnotherRunsOrAnotherTenantsRevision()
    {
        await using var world = await StartAsync();
        var (revision, hash) = await CreateRevisionAsync(world, """{"flaky_budget":3}""");
        var (runId, first, _) = await SeedRunAndRevisionsAsync(world);
        await InsertEvaluationAsync(world, runId, revision, hash, first, "live", "PASS");

        // The same revision numbers asked about another run answer nothing.
        var otherRun = await PlannedExecutionSeeder.SeedPlanAsync(_fixture, world.Database, world.Org, 1);
        (await ReadLiveAsync(world, otherRun.RunId, [first])).ShouldBeEmpty();

        // And so does another tenant asking about the real run.
        var otherOrg = await CreateOrgAsync(world.Client, world.User, "other");
        var otherProject = await CreateProjectAsync(world.Client, world.User, otherOrg, "checkout");
        (await ReadLiveAsync(world with { Org = otherOrg, Project = otherProject }, runId, [first])).ShouldBeEmpty();
        // The owning tenant still reads it, so the empty answer is the boundary and not a broken read.
        (await ReadLiveAsync(world, runId, [first])).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task LiveEvaluations_AreEmpty_WithoutNamedRevisions()
    {
        await using var world = await StartAsync();

        (await ReadLiveAsync(world, Guid.CreateVersion7(), [])).ShouldBeEmpty();
    }

    /// <summary>Reads the live evaluations of a run's revisions through the module's registered contracts surface.</summary>
    private static async Task<IReadOnlyList<LiveEvaluation>> ReadLiveAsync(
        World world,
        Guid runId,
        IReadOnlyList<int> revisions)
    {
        await using var services = world.Api.Services.CreateAsyncScope();
        var scopes = services.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
        await using var scope = await scopes.BeginAsync(world.Org, null, false, TestContext.Current.CancellationToken);
        var contracts = services.ServiceProvider.GetRequiredService<IPolicyContracts>();

        var found = await contracts.GetLiveEvaluationsAsync(runId, revisions, TestContext.Current.CancellationToken);
        await scope.CommitAsync(TestContext.Current.CancellationToken);

        return found;
    }

    /// <summary>A run of the world's org plus two frozen revisions of it, written as the app role.</summary>
    private async Task<(Guid RunId, int First, int Second)> SeedRunAndRevisionsAsync(World world)
    {
        var run = await PlannedExecutionSeeder.SeedPlanAsync(_fixture, world.Database, world.Org, 1);

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            foreach (var revisionNo in (int[])[1, 2])
            {
                await scope.ExecuteAsync(
                    """
                    insert into public.evidence_revision
                      (id, org_id, run_id, revision_no, completeness, event_watermark, manifest_hash)
                    values (@id, @org_id, @run_id, @revision_no, 'complete', 0, @manifest_hash)
                    """,
                    ("id", Guid.CreateVersion7()),
                    ("org_id", world.Org),
                    ("run_id", run.RunId),
                    ("revision_no", revisionNo),
                    ("manifest_hash", "sha256:" + new string('c', 64)));
            }

            await scope.CommitAsync();
        }

        return (run.RunId, 1, 2);
    }

    /// <summary>One immutable <c>evaluation</c> row of the run's given revision, as the app role.</summary>
    private async Task<Guid> InsertEvaluationAsync(
        World world,
        Guid runId,
        Guid policyRevisionId,
        string policyHash,
        int evidenceRevision,
        string mode,
        string decision)
    {
        var id = Guid.CreateVersion7();
        var complete = string.Equals(decision, "PASS", StringComparison.Ordinal);

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            await scope.ExecuteAsync(
                """
                insert into public.evaluation
                  (id, org_id, run_id, evidence_revision, policy_revision_id, policy_hash, manifest_hash, completeness,
                   missing, outcome, decision, reasons, evaluator_version, mode, exceptions_hash, exceptions_as_of)
                values
                  (@id, @org_id, @run_id, @evidence_revision, @policy_revision_id, @policy_hash, @manifest_hash,
                   @completeness, array[]::text[], @outcome, @decision, array[]::text[], 'v1', @mode, @exceptions_hash, now())
                """,
                ("id", id),
                ("org_id", world.Org),
                ("run_id", runId),
                ("evidence_revision", evidenceRevision),
                ("policy_revision_id", policyRevisionId),
                ("policy_hash", policyHash),
                ("manifest_hash", "sha256:" + new string('c', 64)),
                ("completeness", complete ? "complete" : "incomplete"),
                ("outcome", complete ? "all_passed" : "has_failures"),
                ("decision", decision),
                ("mode", mode),
                ("exceptions_hash", "sha256:" + new string('d', 64)));
            await scope.CommitAsync();
        }

        return id;
    }
}
