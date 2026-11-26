using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Authoring.Application;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// One tenant with two projects on the shared database, and the host's real requirement writer driven inside
/// writable tenant scopes, so the writer's "the open scope's organization" guard runs as it does in a request.
/// </summary>
internal sealed record RequirementTestWorld(
    TenancyApiFactory Api,
    HttpClient Client,
    PostgresTenancyFixture Fixture,
    string Database,
    Guid User,
    Guid Org,
    Guid Project,
    Guid OtherProject) : IAsyncDisposable
{
    /// <summary>A body that is valid for every create in these tests.</summary>
    internal const string Body = "The shopper can pay by card.";

    internal static async Task<RequirementTestWorld> StartAsync(PostgresTenancyFixture fixture)
    {
        var database = await fixture.SharedDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(fixture.AppConnectionString(database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, Unique.Slug("acme"));
        var project = await CreateProjectAsync(client, user, org, "checkout");
        var other = await CreateProjectAsync(client, user, org, "billing");

        return new RequirementTestWorld(api, client, fixture, database, user, org, project, other);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Api.DisposeAsync();
    }

    /// <summary>The author membership the org creation seeded.</summary>
    internal Task<Guid> MembershipAsync() => Fixture.ScalarAsync<Guid>(
        Database,
        $"select id from public.membership where org_id = '{Org:D}' limit 1");

    /// <summary>
    /// Runs <paramref name="call"/> on the host's writer in one writable scope of <paramref name="project"/>
    /// (default: <see cref="Project"/>), committing afterwards unless <paramref name="commit"/> is false.
    /// </summary>
    internal async Task<T> InScopeAsync<T>(
        Func<RequirementWriter, CancellationToken, ValueTask<T>> call,
        Guid? project = null,
        bool commit = true)
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var services = Api.Services.CreateAsyncScope();
        await using var scope = await services.ServiceProvider.GetRequiredService<ITenantScopeFactory>()
            .BeginAsync(Org, project ?? Project, false, cancellationToken);

        var result = await call(services.ServiceProvider.GetRequiredService<RequirementWriter>(), cancellationToken);
        if (commit)
        {
            await scope.CommitAsync(cancellationToken);
        }

        return result;
    }

    /// <summary>A minimal valid create in <paramref name="project"/> (default: <see cref="Project"/>).</summary>
    internal RequirementCreate Create(
        string kind = "user_story",
        string title = "Pay by card",
        string body = Body,
        Guid? parent = null,
        Guid? featureNode = null,
        string? externalRef = null,
        Guid? project = null) =>
        new(project ?? Project, kind, title, externalRef, featureNode, parent, body, "manual", null, null);

    /// <summary>Creates a requirement through the writer and returns its identity; the create must succeed.</summary>
    internal async Task<RequirementHead> CreatedAsync(RequirementCreate create)
    {
        var result = await InScopeAsync((writer, token) => writer.CreateAsync(Org, create, token), create.ProjectId);
        result.IsSuccess.ShouldBeTrue(result.Error?.Message);

        return result.Value;
    }
}
