// Request URLs are written inline so each test reads as the request it sends.
#pragma warning disable CA1861
using System.Globalization;
using System.Net;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

public sealed partial class ReportComparisonTests
{
    // ------------------------------------------------------------------ not found, forbidden

    /// <summary>
    /// AC: another tenant's report id and an unknown report id answer the one <c>not_found</c> — same code, same
    /// message — so the route is no existence oracle across tenants.
    /// </summary>
    [Fact]
    public async Task Comparison_OtherTenantsReport_IsTheSameNotFoundAsAnUnknownOne()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        var (otherOrg, otherProject, otherRunId) = await CreateOtherTenantAsync(world);
        var foreign = await SeedReportAsync(world, otherRunId, org: otherOrg, project: otherProject);

        var foreignAnswer = await GetAsync(world, runId, foreign.Id);
        var unknownAnswer = await GetAsync(world, runId, Guid.CreateVersion7());

        foreignAnswer.Status.ShouldBe(HttpStatusCode.NotFound, foreignAnswer.ErrorMessage);
        unknownAnswer.Status.ShouldBe(HttpStatusCode.NotFound, unknownAnswer.ErrorMessage);
        foreignAnswer.ErrorCode.ShouldBe("not_found");
        foreignAnswer.ErrorCode.ShouldBe(unknownAnswer.ErrorCode);
        foreignAnswer.ErrorMessage.ShouldBe(unknownAnswer.ErrorMessage);
    }

    /// <summary>AC: a report that exists but belongs to another run of the same org is <c>not_found</c> for this route.</summary>
    [Fact]
    public async Task Comparison_ReportOfAnotherRun_IsNotFound()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        var otherRunId = await SeedRunAsync(world);
        var report = await SeedReportAsync(world, otherRunId);

        var answer = await GetAsync(world, runId, report.Id);

        answer.Status.ShouldBe(HttpStatusCode.NotFound, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("not_found");
    }

    /// <summary>AC: a report id served under a run that does not exist is <c>not_found</c>.</summary>
    [Fact]
    public async Task Comparison_UnknownRun_IsNotFound()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        var report = await SeedReportAsync(world, runId);

        var answer = await GetAsync(world, Guid.CreateVersion7(), report.Id);

        answer.Status.ShouldBe(HttpStatusCode.NotFound, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("not_found");
    }

    /// <summary>AC: an unknown report id under a real run is <c>not_found</c>.</summary>
    [Fact]
    public async Task Comparison_UnknownReport_IsNotFound()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);

        var answer = await GetAsync(world, runId, Guid.CreateVersion7());

        answer.Status.ShouldBe(HttpStatusCode.NotFound, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("not_found");
    }

    /// <summary>AC: the permission stage refuses a caller without <c>run:read</c> with 403.</summary>
    [Fact]
    public async Task Comparison_WithoutRunRead_IsForbidden()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        var report = await SeedReportAsync(world, runId);

        // A service credential is outside the service-role ceiling for `run:read`, so the permission stage refuses it
        // before the handler reads a row — the 403 is the stage's, never the report's own scope check.
        var answer = await world.Client.SendAsync(
            HttpMethod.Get,
            ComparisonUrl(runId, report.Id),
            TenancyApiFactory.Resolved(serviceOrg: world.Org, servicePermissions: ["run:create"]));

        answer.Status.ShouldBe(HttpStatusCode.Forbidden, answer.ErrorMessage);
    }

    /// <summary>
    /// AC: a caller narrowed to a project that is not the artifact's project is <c>not_found</c>, the same answer an
    /// unknown report gives.
    /// </summary>
    [Fact]
    public async Task Comparison_ProjectScopedCallerOfAnotherProject_IsNotFound()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        var report = await SeedReportAsync(world, runId);
        var otherProject = await CreateProjectAsync(world.Client, world.User, world.Org, Unique.Slug("comparison-other"));
        using var narrowed = TenancyApiFactory.WithPrincipal(
            _fixture.AppConnectionString(world.Database),
            world.Org,
            otherProject,
            RequestPrincipal.Member(world.User));
        using var client = narrowed.CreateClient();

        var answer = await client.SendAsync(
            HttpMethod.Get,
            ComparisonUrl(runId, report.Id),
            TenancyApiFactory.Resolved(user: world.User, orgHeader: world.Org));

        answer.Status.ShouldBe(HttpStatusCode.NotFound, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("not_found");
    }

    private static string ComparisonUrl(Guid runId, Guid reportId) =>
        string.Create(CultureInfo.InvariantCulture, $"/v1/runs/{runId}/reports/{reportId}/comparison");

    /// <summary>A second org with one project and one run, for the cross-tenant refusals.</summary>
    private async Task<(Guid Org, Guid Project, Guid RunId)> CreateOtherTenantAsync(World world)
    {
        var org = await CreateOrgAsync(world.Client, world.User, Unique.Slug("comparison-tenant"));
        var project = await CreateProjectAsync(world.Client, world.User, org, Unique.Slug("comparison-project"));
        var runId = await RunRowSeeder.SeedRunAsync(_fixture, world.Database, org);
        return (org, project, runId);
    }
}
