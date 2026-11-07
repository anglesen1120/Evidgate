// Request URLs are written inline so each test reads as the request it sends.
#pragma warning disable CA1861
using System.Net;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

public sealed partial class ReprocessEndpointTests
{
    // ------------------------------------------------------------------ refusals

    [Fact]
    public async Task Another_orgs_run_is_the_one_not_found()
    {
        await using var world = await StartAsync();
        await using var other = await StartAsync();
        var foreignRun = await SeedRunAsync(other);
        var reportId = await SeedReportAsync(other, foreignRun);

        var answer = await PostAsync(world, foreignRun, reportId);

        answer.Status.ShouldBe(HttpStatusCode.NotFound);
        answer.ErrorCode.ShouldBe("not_found");
        (await ReprocessTasksAsync(world)).ShouldBe(0);

        // Positive control: the same request shape against the caller's own report is accepted.
        var ownRun = await SeedRunAsync(world);
        var ownReport = await SeedReportAsync(world, ownRun);
        (await PostAsync(world, ownRun, ownReport)).Status.ShouldBe(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task A_report_of_another_run_is_the_one_not_found()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        var otherRun = await SeedRunAsync(world);
        var reportId = await SeedReportAsync(world, otherRun);

        var answer = await PostAsync(world, runId, reportId);

        answer.Status.ShouldBe(HttpStatusCode.NotFound);
        answer.ErrorCode.ShouldBe("not_found");
        (await ReprocessTasksAsync(world)).ShouldBe(0);

        // Positive control: the report of the run the route names is accepted.
        (await PostAsync(world, otherRun, reportId)).Status.ShouldBe(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task An_unknown_report_is_the_one_not_found()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        var reportId = await SeedReportAsync(world, runId);

        var answer = await PostAsync(world, runId, Guid.CreateVersion7());

        answer.Status.ShouldBe(HttpStatusCode.NotFound);
        answer.ErrorCode.ShouldBe("not_found");

        // Positive control: the report that does exist under the same run is accepted.
        (await PostAsync(world, runId, reportId)).Status.ShouldBe(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task An_unsupported_report_is_unprocessable()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        var reportId = await SeedReportAsync(world, runId, outcome: "unsupported");
        var supported = await SeedReportAsync(world, runId);

        var answer = await PostAsync(world, runId, reportId);

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("raw_report_unprocessable");
        (await ReprocessTasksAsync(world)).ShouldBe(0);

        // Positive control: a report a release did judge is accepted under the same route.
        (await PostAsync(world, runId, supported)).Status.ShouldBe(HttpStatusCode.Accepted);
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("0.9.0")]
    [InlineData("2.0.0-rc.1")]
    [InlineData("9.9.9")]
    public async Task An_equal_older_unorderable_or_unregistered_version_is_unknown(string version)
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        var reportId = await SeedReportAsync(world, runId);

        var answer = await PostAsync(world, runId, reportId, version: version);

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("parser_version_unknown");
        (await ReprocessTasksAsync(world)).ShouldBe(0);

        // Positive control: the registered newer release of the same parser is accepted.
        (await PostAsync(world, runId, reportId)).Status.ShouldBe(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task A_request_without_a_key_is_refused_before_the_handler()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        var reportId = await SeedReportAsync(world, runId);

        var answer = await world.Client.SendAsync(
            HttpMethod.Post,
            $"/v1/runs/{runId}/reprocess",
            world.Admin,
            new { raw_report_id = reportId, parser_version = NewerVersion });

        answer.Status.ShouldBe(HttpStatusCode.BadRequest);
        answer.ErrorCode.ShouldBe("request_invalid");
        (await ReprocessTasksAsync(world)).ShouldBe(0);

        // Positive control: the identical body with a key is accepted.
        (await PostAsync(world, runId, reportId)).Status.ShouldBe(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task A_caller_without_run_create_is_forbidden()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        var reportId = await SeedReportAsync(world, runId);

        // A user the org never admitted holds no role at all, so the C2 stage refuses before the handler's read.
        var stranger = Guid.CreateVersion7();
        var answer = await PostAsync(
            world,
            runId,
            reportId,
            caller: TenancyApiFactory.Resolved(user: stranger, orgHeader: world.Org));

        answer.Status.ShouldBe(HttpStatusCode.Forbidden);
        (await ReprocessTasksAsync(world)).ShouldBe(0);

        // Positive control: the org's founder holds run:create and is accepted with the same body.
        (await PostAsync(world, runId, reportId)).Status.ShouldBe(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task A_project_bound_service_credential_is_accepted_for_its_own_project_and_not_found_for_another()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        var reportId = await SeedReportAsync(world, runId);
        var sibling = await CreateProjectAsync(world.Client, world.User, world.Org, Unique.Slug("sibling"));

        // run:create is inside the service-role ceiling, so the permission stage admits a project-bound credential.
        var own = TenancyApiFactory.Resolved(
            serviceOrg: world.Org, serviceProject: world.Project, servicePermissions: ["run:create"]);
        var elsewhere = TenancyApiFactory.Resolved(
            serviceOrg: world.Org, serviceProject: sibling, servicePermissions: ["run:create"]);

        var denied = await PostAsync(world, runId, reportId, caller: elsewhere);

        denied.Status.ShouldBe(HttpStatusCode.NotFound);
        denied.ErrorCode.ShouldBe("not_found");
        (await ReprocessTasksAsync(world)).ShouldBe(0);

        // Positive control: the same credential bound to the report's own project is accepted.
        (await PostAsync(world, runId, reportId, caller: own)).Status.ShouldBe(HttpStatusCode.Accepted);
        (await ReprocessTasksAsync(world)).ShouldBe(1);
    }
}
