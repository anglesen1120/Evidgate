// 05.4.1-T1 AC8: who may cancel, and the two 404s that keep the route from being an existence oracle.
#pragma warning disable CA1861
using System.Net;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

public sealed partial class CancelRunEndpointTests
{
    /// <summary>
    /// AC8/§5.5 C2: <c>run:cancel</c> gates the route. A member whose role carries the sibling read permission
    /// (<c>viewer</c> holds <c>run:read</c>) is refused <c>403</c> before the handler, so nothing is recorded and
    /// the run keeps its state; a member holding the permission (<c>qa_engineer</c>) then cancels the same run
    /// through the same route, which is this deny case's positive control.
    /// </summary>
    [Fact]
    public async Task CancelRun_NoPermission_403()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var runId = await CancelRunTestData.CreateQueuedRunAsync(world);
        var viewerId = await AddMemberAsync(_fixture, world.Database, world.Org, "viewer");
        var qaId = await AddMemberAsync(_fixture, world.Database, world.Org, "qa_engineer");
        var viewer = TenancyApiFactory.Resolved(user: viewerId, orgHeader: world.Org);
        var qa = TenancyApiFactory.Resolved(user: qaId, orgHeader: world.Org);

        var denied = await CancelRunTestData.CancelAsync(world, runId, viewer, reason: "no cancel permission");

        denied.Status.ShouldBe(HttpStatusCode.Forbidden);
        denied.ErrorCode.ShouldBe("forbidden");
        (await CancelRunTestData.StateAsync(_fixture, world, runId)).ShouldBe("queued");
        (await CancelRunTestData.RowCountAsync(_fixture, world, runId)).ShouldBe(0, "a refused caller writes nothing");

        var allowed = await CancelRunTestData.CancelAsync(world, runId, qa, reason: "no cancel permission");

        allowed.Status.ShouldBe(HttpStatusCode.Accepted, allowed.ErrorMessage);
        (await CancelRunTestData.StateAsync(_fixture, world, runId)).ShouldBe("canceled");
        (await CancelRunTestData.RowCountAsync(_fixture, world, runId)).ShouldBe(1);
        (await CancelRunTestData.RowAsync(_fixture, world, runId)).RequestedBy.ShouldBe(qaId);
    }

    /// <summary>
    /// AC8: a service credential never reaches the route even when it claims the sibling read permission —
    /// <c>run:cancel</c> sits outside the service-role ceiling — so the handler writes nothing; the member cancel
    /// that follows is the positive control.
    /// </summary>
    [Fact]
    public async Task CancelRun_ServiceCredentialWithoutCancel_403()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var runId = await CancelRunTestData.CreateQueuedRunAsync(world);

        var denied = await CancelRunTestData.CancelAsync(
            world,
            runId,
            TenancyApiFactory.Resolved(
                serviceOrg: world.Org, serviceProject: world.Project, servicePermissions: ["run:read"]));

        denied.Status.ShouldBe(HttpStatusCode.Forbidden);
        denied.ErrorCode.ShouldBe("forbidden");
        (await CancelRunTestData.StateAsync(_fixture, world, runId)).ShouldBe("queued");
        (await CancelRunTestData.RowCountAsync(_fixture, world, runId)).ShouldBe(0, "a refused caller writes nothing");

        (await CancelRunTestData.CancelAsync(world, runId)).Status.ShouldBe(HttpStatusCode.Accepted);
        (await CancelRunTestData.RowCountAsync(_fixture, world, runId)).ShouldBe(1);
    }

    /// <summary>
    /// AC8/00 §5.4: another tenant's run is the same <c>404 not_found</c> an unknown id gets — byte for byte, so
    /// the route is never an existence oracle — and no row is written for either attempt.
    /// </summary>
    [Fact]
    public async Task Cancel_CrossTenant_404()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var runId = await CancelRunTestData.CreateQueuedRunAsync(world);

        var otherUser = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(world.Client, otherUser, Unique.Slug("rival"));
        var foreign = await CancelRunTestData.CancelAsync(
            world,
            runId,
            TenancyApiFactory.Resolved(user: otherUser, orgHeader: otherOrg));
        var unknown = await CancelRunTestData.CancelAsync(world, Guid.CreateVersion7());

        foreign.Status.ShouldBe(HttpStatusCode.NotFound);
        unknown.Status.ShouldBe(HttpStatusCode.NotFound);
        foreign.ErrorCode.ShouldBe("not_found");
        CancelRunTestData.Envelope(foreign).ShouldBe(
            CancelRunTestData.Envelope(unknown),
            "an existence oracle is a leak even when only the code matches");
        (await CancelRunTestData.StateAsync(_fixture, world, runId))
            .ShouldBe("queued", "another tenant's cancel changes nothing");
        (await CancelRunTestData.RowCountAsync(_fixture, world, runId)).ShouldBe(0);
    }

    /// <summary>
    /// AC8: a request tenant scope narrowed to a sibling project of the same org gets the shared <c>404
    /// not_found</c> for this project's run — the handler's scope check (<c>OrchestratorScope.Project</c>) is
    /// mapped to that 404, never to <c>project_scope_mismatch</c> — and the body an unknown id gets.
    /// </summary>
    [Fact]
    public async Task Cancel_CrossProject_404SharedNotFoundBody()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var runId = await CancelRunTestData.CreateQueuedRunAsync(world);
        var sibling = await CreateProjectAsync(world.Client, world.User, world.Org, "other");

        // A member session scoped to the sibling project, the shape a project-scoped web URL publishes: the
        // caller itself holds run:cancel, so only the project scope can refuse the request.
        await using var host = TenancyApiFactory.WithPrincipal(
            _fixture.AppConnectionString(world.Database),
            world.Org,
            sibling,
            RequestPrincipal.Member(world.User));
        using var scoped = host.CreateClient();
        var admin = TenancyApiFactory.Resolved(user: world.User, orgHeader: world.Org);

        var foreign = await CancelRunTestData.CancelAsync(world, runId, admin, client: scoped);
        var unknown = await CancelRunTestData.CancelAsync(world, Guid.CreateVersion7(), admin, client: scoped);

        foreign.Status.ShouldBe(HttpStatusCode.NotFound);
        unknown.Status.ShouldBe(HttpStatusCode.NotFound);
        foreign.ErrorCode.ShouldBe("not_found");
        CancelRunTestData.Envelope(foreign).ShouldBe(
            CancelRunTestData.Envelope(unknown),
            "a sibling project's run answers the unknown-id body, not a scope-mismatch hint");
        (await CancelRunTestData.StateAsync(_fixture, world, runId)).ShouldBe("queued");
        (await CancelRunTestData.RowCountAsync(_fixture, world, runId)).ShouldBe(0);
    }
}
