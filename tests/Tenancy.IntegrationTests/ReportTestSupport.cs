using System.Text.Json;
using System.Text.Json.Nodes;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Authoring.Application.Reports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// A section builder for the tests: it lists what the source holds, so the tests can prove what a snapshot was built
/// from. A strategy of <c>oversize</c> makes it emit more than one report may hold.
/// </summary>
internal sealed class TestReportSectionBuilder : IReportSectionBuilder
{
    internal const string Id = "traceability";

    public string SectionId => Id;

    public JsonNode Build(ReportSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var label = source.Request.Language == "vi" ? "Truy vết" : "Traceability";
        var section = new JsonObject
        {
            ["label"] = label,
            ["nodes"] = new JsonArray([.. source.Nodes.Select(node => (JsonNode)node.Id.ToString("D"))]),
            ["items"] = new JsonArray([.. source.Items.Select(item => (JsonNode)item.Id.ToString("D"))]),
            ["cases"] = new JsonArray([.. source.Cases.Select(testCase => (JsonNode)$"{testCase.Code}@{testCase.VersionNo}")]),
            ["requirements"] = new JsonArray([.. source.Requirements.Select(requirement => (JsonNode)$"{requirement.Id:D}@{requirement.VersionNo}")]),
            ["links"] = new JsonArray([.. source.Links.Select(link => (JsonNode)link.Id.ToString("D"))]),
        };

        if (source.Request.Strategy == "oversize")
        {
            section["filler"] = new string('x', TestReportLimits.MaxPayloadBytes + 1);
        }

        return section;
    }
}

/// <summary>A host with the test builder registered and a way to run the build job the way the task consumer does.</summary>
internal static class ReportTestWorld
{
    private static readonly JsonSerializerOptions WireOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    internal static async Task<RequirementTestWorld> StartAsync(PostgresTenancyFixture fixture)
    {
        var database = await fixture.SharedDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(
            fixture.AppConnectionString(database),
            services: services =>
            {
                // Only the test builder: a production builder under the same id would collide with it.
                services.RemoveAll<IReportSectionBuilder>();
                services.AddSingleton<IReportSectionBuilder, TestReportSectionBuilder>();
                services.AddScoped<TestReportBuildJob>();
            });
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, Unique.Slug("acme"));
        var project = await CreateProjectAsync(client, user, org, "checkout");
        var other = await CreateProjectAsync(client, user, org, "billing");

        return new RequirementTestWorld(api, client, fixture, database, user, org, project, other);
    }

    /// <summary>Runs one attempt of the job in a tenant scope; without <paramref name="commit"/> the scope is rolled back, like an interrupted run.</summary>
    internal static async Task RunJobAsync(RequirementTestWorld world, ReportJobPayload payload, bool commit = true)
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var services = world.Api.Services.CreateAsyncScope();
        await using var scope = await services.ServiceProvider.GetRequiredService<ITenantScopeFactory>()
            .BeginAsync(payload.OrgId, payload.ProjectId, false, cancellationToken);

        await services.ServiceProvider.GetRequiredService<TestReportBuildJob>().RunAsync(payload, cancellationToken);
        if (commit)
        {
            await scope.CommitAsync(cancellationToken);
        }
    }

    /// <summary>Reads the source of a request in a tenant scope with the byte cap of the reader lowered to <paramref name="maxBytes"/>.</summary>
    internal static async Task<ReportSource> ReadSourceAsync(RequirementTestWorld world, ReportRequest request, long maxBytes)
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var services = world.Api.Services.CreateAsyncScope();
        await using var scope = await services.ServiceProvider.GetRequiredService<ITenantScopeFactory>()
            .BeginAsync(world.Org, world.Project, false, cancellationToken);
        var reader = new TestReportSourceReader(services.ServiceProvider.GetRequiredService<ITenantDb>()) { MaxSourceBytes = maxBytes };

        return await reader.ReadAsync(world.Org, world.Project, request, cancellationToken);
    }

    /// <summary>The task payload the create queued for a job, read back from the outbox row and parsed like the consumer does.</summary>
    internal static async Task<ReportJobPayload> QueuedPayloadAsync(RequirementTestWorld world, Guid jobId)
    {
        var json = await world.Fixture.ScalarAsync<string>(
            world.Database,
            $"select payload::text from public.outbox where org_id = '{world.Org:D}' and idempotency_key = '{TestReportTasks.OutboxKey(world.Project, jobId)}'");

        return JsonSerializer.Deserialize<ReportJobPayload>(
            json,
            WireOptions)!;
    }

    internal static Action<System.Net.Http.Headers.HttpRequestHeaders> Caller(RequirementTestWorld world) =>
        TenancyApiFactory.Resolved(user: world.User, orgHeader: world.Org);

    internal static string ReportPath(Guid project, string tail = "") => $"/v1/projects/{project:D}/authoring/reports{tail}";
}
