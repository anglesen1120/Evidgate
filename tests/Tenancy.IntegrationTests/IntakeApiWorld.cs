using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

internal sealed record IntakeApiWorld(TenancyApiFactory Api, HttpClient Client, PostgresTenancyFixture Fixture,
    string Database, Guid User, Guid Org, Guid Project, Guid Node) : IAsyncDisposable
{
    internal string Path => $"/v1/projects/{Project}/authoring/intakes";
    internal Action<HttpRequestHeaders> As => TenancyApiFactory.Resolved(user: User, orgHeader: Org);

    internal static async Task<IntakeApiWorld> StartAsync(PostgresTenancyFixture fixture, Action<IServiceCollection>? services = null)
    {
        var database = await fixture.SharedDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(fixture.AppConnectionString(database), services: services);
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, Unique.Slug("intake"));
        var project = await CreateProjectAsync(client, user, org, Unique.Slug("docs"));
        var node = await client.SendAsync(HttpMethod.Post, $"/v1/projects/{project}/authoring/nodes",
            TenancyApiFactory.Resolved(user: user, orgHeader: org), new { kind = "module", title = "Specifications" }, NewKey());
        node.Status.ShouldBe(HttpStatusCode.Created, node.ErrorMessage);
        return new(api, client, fixture, database, user, org, project, node.Id("node_id"));
    }

    internal Task<ApiAnswer> PostAsync(object body, string? key = null) =>
        Client.SendAsync(HttpMethod.Post, Path, As, body, key ?? NewKey());

    internal async Task<Guid> ArtifactAsync(string kind, string mediaType, long bytes)
    {
        var run = await RunRowSeeder.SeedRunAsync(Fixture, Database, Org);
        var id = Guid.CreateVersion7();
        var execution = new string('a', 64);
        var key = $"org/{Org:D}/project/{Project:D}/run/{run:D}/attempt/{execution}/1/{id:D}";
        await Fixture.ExecuteAsync(Database, $"""
            insert into public.artifact(id,org_id,project_id,run_id,execution_key,attempt_no,kind,object_key,
                media_type,bytes,checksum,sensitivity,scan_state,state)
            values ('{id}','{Org}','{Project}','{run}','{execution}',1,'{kind}','{key}',
                '{mediaType}',{bytes},'sha256:{execution}','normal','clean','ready')
            """);
        return id;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Api.DisposeAsync();
    }
}
