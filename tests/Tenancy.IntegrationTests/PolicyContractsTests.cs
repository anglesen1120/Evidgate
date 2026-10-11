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
}
