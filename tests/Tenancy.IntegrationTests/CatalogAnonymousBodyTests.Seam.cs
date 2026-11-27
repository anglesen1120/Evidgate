using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;
using static Tenancy.IntegrationTests.BodyCountSeam;

namespace Tenancy.IntegrationTests;

public sealed partial class CatalogAnonymousBodyTests
{
    [Theory]
    [InlineData("suite-revision")]
    [InlineData("requirement-update")]
    [InlineData("case-update")]
    [InlineData("fixture-create")]
    [InlineData("fixture-revision")]
    public async Task CatalogWrite_AnonymousRequest_ReadsNoBodyBytes(string route)
    {
        await using var world = await StartAsync();
        var target = await TargetAsync(world, route);
        var api = TenancyApiFactory.RealResolver(
            fixture.AppConnectionString(world.Database),
            services: services => services.AddSingleton<IStartupFilter>(new CountingBodyStartupFilter()));
        using var client = api.CreateClient();

        try
        {
            var anonymous = await SendBodyAsync(world, target, "{", Anonymous, client: client, extraHeaders: MarkBody);
            AssertUnauthenticated(anonymous.Answer);
            BytesRead(anonymous.Answer).ShouldBe(0, "an anonymous request is refused before its body is read");

            var authenticated = await SendBodyAsync(world, target, "{", world.Admin, client: client, extraHeaders: MarkBody);
            authenticated.Answer.Status.ShouldBe(HttpStatusCode.BadRequest, authenticated.Text);
            BytesRead(authenticated.Answer).ShouldBeGreaterThan(0, "the counter observes the read the positive control makes");
        }
        finally
        {
            await api.DisposeAsync();
        }
    }

    [Fact]
    public async Task SuiteRevision_MemberWithoutTenant_ReadsNoBodyBytesAndReturnsUnauthenticated()
    {
        await using var world = await StartAsync();
        var target = await TargetAsync(world, "suite-revision");
        var member = await AddMemberAsync(fixture, world.Database, world.Org, roleCode: null);
        var caller = TenancyApiFactory.Resolved(user: member);
        var api = TenancyApiFactory.RealResolver(
            fixture.AppConnectionString(world.Database),
            services: services => services.AddSingleton<IStartupFilter>(new CountingBodyStartupFilter()));
        using var client = api.CreateClient();

        try
        {
            // The host publishes the member, but the invalid org header leaves its tenant unresolved.
            var refused = await SendBodyAsync(world, target, "{", caller, client: client, extraHeaders: headers =>
            {
                MarkBody(headers);
                headers.Add("X-Evidgate-Org", "not-a-uuid");
            });

            AssertUnauthenticated(refused.Answer);
            BytesRead(refused.Answer).ShouldBe(0, "a principal without a tenant is refused before its body is read");

            var resolved = await SendBodyAsync(world, target, "{", caller, client: client, extraHeaders: headers =>
            {
                MarkBody(headers);
                headers.Add("X-Evidgate-Org", world.Org.ToString());
            });

            resolved.Answer.Status.ShouldBe(HttpStatusCode.BadRequest, resolved.Text);
            resolved.Answer.ErrorCode.ShouldBe("request_invalid");
            BytesRead(resolved.Answer).ShouldBeGreaterThan(0, "resolving only the tenant permits the same body to be read");
        }
        finally
        {
            await api.DisposeAsync();
        }
    }
}
