// Create, idempotency, name uniqueness and scope coverage for POST /v1/projects/{p}/releases.
#pragma warning disable CA1861
using System.Net;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

public sealed partial class ReleaseIntegrationTests
{
    [Fact]
    public async Task Create_StoresExactDigests_AndGetReturnsTheDocumentedShape()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var digestApi = Digest('b');
        var digestWeb = Digest('c');
        var body = Body(
            "Checkout build",
            [
                new("web", digestWeb, world.EnvironmentRevision),
                new("api", digestApi, world.EnvironmentRevision),
            ]);

        var created = await CreateAsync(world, body);
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
        created.Body.EnumerateObject().Select(member => member.Name).ShouldBe(["release_id"]);
        var releaseId = created.Id("release_id");

        (await ReleaseCountAsync(world)).ShouldBe(1);
        (await ComponentCountAsync(world, releaseId)).ShouldBe(2);

        var got = await GetReleaseAsync(world, releaseId);
        got.Status.ShouldBe(HttpStatusCode.OK, got.ErrorMessage);
        var release = got.Body;
        release.GetProperty("release_id").GetGuid().ShouldBe(releaseId);
        release.GetProperty("project_id").GetGuid().ShouldBe(world.Project);
        release.GetProperty("name").GetString().ShouldBe("Checkout build");
        release.GetProperty("state").GetString().ShouldBe("draft");
        release.GetProperty("created_by").GetString().ShouldBe($"user:{world.User:D}");
        release.GetProperty("created_at").GetDateTimeOffset().ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-15));
        release.GetProperty("version").GetInt32().ShouldBe(1);
        release.GetProperty("bound_runs").GetArrayLength().ShouldBe(0, "a release with no bind has an empty bound-run list");
        release.TryGetProperty("runs", out _).ShouldBeFalse("the bound runs are `bound_runs`, not a second spelling");

        var components = release.GetProperty("components").EnumerateArray()
            .ToDictionary(
                component => component.GetProperty("component_key").GetString()!,
                component => (
                    Digest: component.GetProperty("digest").GetString(),
                    EnvironmentRevision: component.GetProperty("environment_revision_id").GetGuid()),
                StringComparer.Ordinal);
        components.Keys.ShouldBe(["api", "web"], ignoreOrder: false);
        components["api"].Digest.ShouldBe(digestApi);
        components["web"].Digest.ShouldBe(digestWeb);
        components["web"].EnvironmentRevision.ShouldBe(world.EnvironmentRevision);
    }

    [Fact]
    public async Task Create_DefaultStateIsDraft_AndActiveIsStored()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);

        var draftId = await CreateReleaseAsync(world, "release-default", state: null, OneComponent(world));
        var activeId = await CreateReleaseAsync(world, "release-active", "active", OneComponent(world, fill: 'b'));

        (await GetReleaseAsync(world, draftId)).Body.GetProperty("state").GetString().ShouldBe("draft");
        (await GetReleaseAsync(world, activeId)).Body.GetProperty("state").GetString().ShouldBe("active");
    }

    [Fact]
    public async Task Create_DuplicateName_409ReleaseNameConflict_AndOtherProjectNameIsFree()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var first = await CreateAsync(world, Body("release-one", OneComponent(world)));
        first.Status.ShouldBe(HttpStatusCode.Created, first.ErrorMessage);

        var duplicate = await CreateAsync(world, Body("release-one", OneComponent(world, fill: 'b')));
        duplicate.Status.ShouldBe(HttpStatusCode.Conflict, duplicate.ErrorMessage);
        duplicate.ErrorCode.ShouldBe("release_name_conflict");

        (await ReleaseCountAsync(world)).ShouldBe(1);
        (await ComponentCountAsync(world, first.Id("release_id"))).ShouldBe(1);

        var distinct = await CreateAsync(world, Body("release-two", OneComponent(world)));
        distinct.Status.ShouldBe(HttpStatusCode.Created, distinct.ErrorMessage);

        // The name is unique per project, and only per project.
        var otherProject = await CreateProjectAsync(world.Client, world.User, world.Org, Unique.Slug("other"));
        var otherRevision = await SeedRevisionAsync(world, otherProject);
        var sibling = await CreateAsync(
            world,
            Body("release-one", [new("web", Digest('a'), otherRevision)]),
            project: otherProject);
        sibling.Status.ShouldBe(HttpStatusCode.Created, sibling.ErrorMessage);
    }

    [Fact]
    public async Task ReplaySameIdempotencyKey_ReturnsOriginalOneAuditRow()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var key = NewKey();
        var body = Body("release-replay", OneComponent(world), state: null);

        var first = await CreateAsync(world, body, idempotencyKey: key);
        first.Status.ShouldBe(HttpStatusCode.Created, first.ErrorMessage);
        var releaseId = first.Id("release_id");
        (await ReleaseAuditCountAsync(world, releaseId)).ShouldBe(1);

        var replay = await CreateAsync(world, body, idempotencyKey: key);
        replay.Status.ShouldBe(HttpStatusCode.Created, replay.ErrorMessage);
        replay.Id("release_id").ShouldBe(releaseId);

        (await ReleaseCountAsync(world)).ShouldBe(1);
        (await ComponentCountAsync(world, releaseId)).ShouldBe(1);
        (await IdempotencyCountAsync(world, key)).ShouldBe(1);
        (await ReleaseAuditCountAsync(world, releaseId)).ShouldBe(1);
    }

    [Fact]
    public async Task ReplayDifferentPayload_409IdempotencyConflict()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var key = NewKey();

        var first = await CreateAsync(world, Body("release-bound", OneComponent(world)), idempotencyKey: key);
        first.Status.ShouldBe(HttpStatusCode.Created, first.ErrorMessage);

        var conflicting = await CreateAsync(
            world,
            Body("release-bound", OneComponent(world, fill: 'b')),
            idempotencyKey: key);
        conflicting.Status.ShouldBe(HttpStatusCode.Conflict, conflicting.ErrorMessage);
        conflicting.ErrorCode.ShouldBe("idempotency_conflict");

        (await ReleaseCountAsync(world)).ShouldBe(1);
        (await IdempotencyCountAsync(world, key)).ShouldBe(1);
    }

    [Fact]
    public async Task Create_MissingIdempotencyKey_400RequestInvalid_NoRows()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);

        var answer = await CreateAsync(world, Body("release-keyless", OneComponent(world)), omitIdempotencyKey: true);

        ShouldBeRequestInvalid(answer);
        (await ReleaseCountAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task Create_CrossTenantProject_404NotFound_NoLeak()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var (foreignOrg, foreignProject, _) = await SeedForeignTenantAsync(world.Database);

        var answer = await CreateAsync(world, Body("release-cross-tenant", OneComponent(world)), project: foreignProject);

        answer.Status.ShouldBe(HttpStatusCode.NotFound, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("not_found");
        (await ReleaseCountAsync(world.Database, foreignOrg)).ShouldBe(0);
        (await ReleaseCountAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task Get_CrossTenantAndUnknownId_404SameEnvelope()
    {
        await using var worldA = await RunCreationTests.StartAsync(_fixture, slug: "alpha", hosts: _hosts);
        await using var worldB = await RunCreationTests.StartAsync(_fixture, slug: "beta", hosts: _hosts);
        var releaseId = await CreateReleaseAsync(worldA, "release-alpha", state: null, OneComponent(worldA));

        var crossTenant = await GetReleaseAsync(worldA, releaseId, caller: worldB.Admin);
        var unknown = await GetReleaseAsync(worldB, Guid.CreateVersion7());

        crossTenant.Status.ShouldBe(HttpStatusCode.NotFound, crossTenant.ErrorMessage);
        unknown.Status.ShouldBe(HttpStatusCode.NotFound, unknown.ErrorMessage);
        Envelope(crossTenant).ShouldBe(Envelope(unknown));
        (await ReleaseCountAsync(worldB)).ShouldBe(0);
    }

    [Fact]
    public async Task Create_ServicePrincipalWithMatchingProjectScope_201()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var caller = TenancyApiFactory.Resolved(
            serviceOrg: world.Org,
            serviceProject: world.Project,
            servicePermissions: ["run:create"]);

        var answer = await CreateAsync(world, Body("release-service", OneComponent(world)), caller: caller);

        answer.Status.ShouldBe(HttpStatusCode.Created, answer.ErrorMessage);
        var got = await GetReleaseAsync(world, answer.Id("release_id"));
        got.Status.ShouldBe(HttpStatusCode.OK, got.ErrorMessage);
        got.Body.GetProperty("created_by").GetString().ShouldNotBeNullOrEmpty();
        got.Body.GetProperty("created_by").GetString()!.ShouldContain(":");
    }

    [Fact]
    public async Task Create_ServiceSiblingProjectScope_403ProjectScopeMismatch()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var sibling = await CreateProjectAsync(world.Client, world.User, world.Org, Unique.Slug("sibling"));
        var caller = TenancyApiFactory.Resolved(
            serviceOrg: world.Org,
            serviceProject: sibling,
            servicePermissions: ["run:create"]);

        var answer = await CreateAsync(world, Body("release-sibling", OneComponent(world)), caller: caller, project: world.Project);

        answer.Status.ShouldBe(HttpStatusCode.Forbidden, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("project_scope_mismatch");
        (await ReleaseCountAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task Create_UnknownSoftDeletedAndForeignProject_404SameEnvelope()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var (_, foreignProject, foreignRevision) = await SeedForeignTenantAsync(world.Database);

        var softDeleted = await CreateProjectAsync(world.Client, world.User, world.Org, Unique.Slug("gone"));
        await _fixture.ExecuteAsync(
            world.Database,
            $"update public.project set status = 'deleted' where id = '{softDeleted:D}'");

        // The body names a revision no project of this tenant owns, so a 422 would prove the project id was recognised.
        var unknownRevision = Guid.CreateVersion7();
        var foreign = await CreateAsync(
            world, Body("release-404", [new("web", Digest('a'), unknownRevision)]), project: foreignProject);
        var missing = await CreateAsync(
            world, Body("release-404", [new("web", Digest('a'), unknownRevision)]), project: Guid.CreateVersion7());
        var deleted = await CreateAsync(
            world, Body("release-404", [new("web", Digest('a'), unknownRevision)]), project: softDeleted);

        foreach (var answer in new[] { foreign, missing, deleted })
        {
            answer.Status.ShouldBe(HttpStatusCode.NotFound, answer.ErrorMessage);
            answer.ErrorCode.ShouldBe("not_found");
        }

        Envelope(deleted).ShouldBe(Envelope(missing));
        Envelope(foreign).ShouldBe(Envelope(missing));
        foreignRevision.ShouldNotBe(unknownRevision);

        // The probe runs before the body is parsed at all, so a body that would be 422/400 against a project the caller
        // can see answers the same 404 for a project it cannot: an untrimmed name, an empty component list and bytes
        // that are not JSON are indistinguishable from the unknown-project case.
        string[] invalidBodies =
        [
            Body(" release-untrimmed ", [new("web", Digest('a'), unknownRevision)]),
            Body("release-empty-components", []),
            "{\"name\":",
        ];
        foreach (var invalid in invalidBodies)
        {
            foreach (var project in new[] { foreignProject, Guid.CreateVersion7(), softDeleted })
            {
                var answer = await CreateAsync(world, invalid, project: project);
                answer.Status.ShouldBe(HttpStatusCode.NotFound, answer.ErrorMessage);
                Envelope(answer).ShouldBe(Envelope(missing));
            }
        }

        (await ReleaseCountAsync(world)).ShouldBe(0);
    }
}
