// Who may bind, and the one answer a project-narrowed caller gets: a service credential is granted `run:create` only
// when its token scope is the request's project, so a credential of another project is refused inside the handler as
// the shared unknown-id 404 - never as a scope-mismatch hint, and never with a row written anywhere.
#pragma warning disable CA1861
using System.Net;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

public sealed partial class ReleaseIntegrationTests
{
    /// <summary>
    /// The same credential type, two scopes. Scoped to the release's own project the service credential binds and one
    /// row lands; scoped to a sibling project it may not even learn that the release exists: the bind answers the
    /// unknown-id 404 byte for byte, and <c>release_run</c> still holds only the row the positive control wrote.
    /// </summary>
    [Fact]
    public async Task BindServiceScope_OwnProjectBinds_SiblingScopeGetsTheUnknownId404()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var releaseId = await ReleaseWithAsync(world, "bind-scope", "active", ("web", 'a'));
        var runId = await RunWithAsync(world, Components(("web", 'a')));
        var sibling = await SiblingProjectAsync(world);

        await using var ownProject = ServiceHost(world, world.Project);
        var bound = await BindAsync(
            world,
            releaseId,
            runId,
            caller: ServiceCaller(world.Project),
            client: ownProject.CreateClient());

        bound.Status.ShouldBe(HttpStatusCode.Created, bound.ErrorMessage);
        BoundOf(bound).ShouldContainKey("web");
        (await BindingCountAsync(world, releaseId, runId)).ShouldBe(1);

        await using var otherProject = ServiceHost(world, sibling);
        using var otherClient = otherProject.CreateClient();
        var foreign = await BindAsync(
            world,
            releaseId,
            runId,
            caller: ServiceCaller(sibling),
            client: otherClient);
        var unknown = await BindAsync(
            world,
            Guid.CreateVersion7(),
            runId,
            caller: ServiceCaller(sibling),
            client: otherClient);

        foreign.Status.ShouldBe(HttpStatusCode.NotFound, foreign.ErrorMessage);
        foreign.ErrorCode.ShouldBe("not_found");
        Envelope(foreign).ShouldBe(
            Envelope(unknown),
            "a sibling project's release answers the unknown-id body, not a scope-mismatch hint");
        (await BindingCountAsync(world, releaseId, runId)).ShouldBe(1, "the refused bind wrote nothing");
        (await BindingCountAsync(world)).ShouldBe(1);
    }

    /// <summary>
    /// A service credential whose token scope is the request's project but which does not claim <c>run:create</c> is
    /// refused before the handler: 403 with no body hint about the release, and no row. The claimable set is the
    /// service ceiling, so a token that claims a member-only permission holds nothing at all.
    /// </summary>
    [Fact]
    public async Task BindServiceScope_GrantedScopeWithoutThePermission_403NoRows()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var releaseId = await ReleaseWithAsync(world, "bind-scope-denied", "active", ("web", 'a'));
        var runId = await RunWithAsync(world, Components(("web", 'a')));

        await using var host = ServiceHost(world, world.Project);
        using var client = host.CreateClient();
        var refused = await BindAsync(
            world,
            releaseId,
            runId,
            caller: TenancyApiFactory.Resolved(
                serviceOrg: world.Org,
                serviceProject: world.Project,
                servicePermissions: ["run:read"]),
            client: client);

        refused.Status.ShouldBe(HttpStatusCode.Forbidden, refused.ErrorMessage);
        (await BindingCountAsync(world, releaseId, runId)).ShouldBe(0);
    }

    /// <summary>A host whose published principal and request tenant scope are both <paramref name="project"/>.</summary>
    /// <param name="world">The suite's tenant.</param>
    /// <param name="project">The single project the credential and the request are bound to.</param>
    private TenancyApiFactory ServiceHost(RunCreationTests.World world, Guid project) =>
        TenancyApiFactory.WithPrincipal(
            _fixture.AppConnectionString(world.Database),
            world.Org,
            project,
            RequestPrincipal.Service(project, ["run:create"], credentialId: $"svc-bind-{project:D}"));

    /// <summary>The headers of a service credential scoped to <paramref name="project"/>.</summary>
    /// <param name="project">The project the credential is minted for.</param>
    private static Action<System.Net.Http.Headers.HttpRequestHeaders> ServiceCaller(Guid project) =>
        TenancyApiFactory.Resolved(serviceProject: project, servicePermissions: ["run:create"]);

    /// <summary>A second project of the same org, created through the API because the schema requires a real one.</summary>
    /// <param name="world">The suite's tenant.</param>
    private static async Task<Guid> SiblingProjectAsync(RunCreationTests.World world)
    {
        var slug = Unique.Slug("sibling");
        var created = await world.Client.SendAsync(
            HttpMethod.Post,
            $"/v1/orgs/{world.Org:D}/projects",
            TenancyApiFactory.Resolved(user: world.User),
            new { name = slug, slug },
            NewKey());
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);

        return created.Id();
    }
}
