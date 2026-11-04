using System.Net;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// AC10 through the routes: reading the project's settings and its history is <c>authoring:read</c> — every member role
/// holds it — while saving is <c>project:write</c>, so only the tenant administrator may, and a service principal is
/// refused both because the service ceiling never carries a member's permission.
/// </summary>
/// <remarks>
/// The roles are the built-in ones granted through the membership route, so this suite proves the seeded grant table as
/// well as the handler's gate. A viewer or a release approver can still read, which is the point of separating the codes.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class AiSettingsAuthorizationTests
{
    private readonly PostgresTenancyFixture _fixture;

    public AiSettingsAuthorizationTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    /// <summary>AC10: a qa engineer reads the head and is refused a save; the founder's save is a 201.</summary>
    [Fact]
    public async Task QaEngineer_Reads_ButCannotSave()
    {
        await using var world = await AiSettingsCalls.StartAsync(_fixture);
        var qa = await AiSettingsCalls.AddMemberAsync(_fixture, world, "qa_engineer");

        var read = await AiSettingsCalls.GetAsync(world.Client, world.CallerFor(qa), world.Settings);

        read.Status.ShouldBe(HttpStatusCode.OK, "a qa engineer holds authoring:read");
        read.ETag.ShouldBe("\"0\"");

        var refused = await AiSettingsCalls.SaveAsync(
            world.Client, world.CallerFor(qa), world, AiSettingsCalls.Body(AiSettingsCalls.Document()), read.ETag!);

        refused.Status.ShouldBe(HttpStatusCode.Forbidden, "a save needs project:write");
        refused.ErrorCode.ShouldBe("forbidden");
        (await AiSettingsCalls.SettingsCountAsync(_fixture, world)).ShouldBe(0);

        var admin = await AiSettingsCalls.SaveAsync(
            world.Client, world.As, world, AiSettingsCalls.Body(AiSettingsCalls.Document()), "\"0\"");

        admin.Status.ShouldBe(HttpStatusCode.Created, "the tenant administrator holds project:write");
    }

    /// <summary>AC10: the viewer and the release approver read the head and the history and cannot save.</summary>
    [Fact]
    public async Task ReadOnlyRoles_Read_AndCannotSave()
    {
        await using var world = await AiSettingsCalls.StartAsync(_fixture);

        foreach (var role in new[] { "viewer", "release_approver" })
        {
            var member = await AiSettingsCalls.AddMemberAsync(_fixture, world, role);
            var caller = world.CallerFor(member);

            foreach (var url in new[] { world.Settings, world.Versions, world.Templates })
            {
                var read = await AiSettingsCalls.GetAsync(world.Client, caller, url);

                read.Status.ShouldBe(HttpStatusCode.OK, $"{role} reads {url}: {read.ErrorMessage}");
            }

            var refused = await AiSettingsCalls.SaveAsync(
                world.Client, caller, world, AiSettingsCalls.Body(AiSettingsCalls.Document()), "\"0\"");

            refused.Status.ShouldBe(HttpStatusCode.Forbidden, $"{role} may not save");
        }

        (await AiSettingsCalls.SettingsCountAsync(_fixture, world)).ShouldBe(0);
    }

    /// <summary>AC10: a service principal is refused both reads and the save — the ceiling never grants a member code.</summary>
    [Fact]
    public async Task ServicePrincipal_IsRefused()
    {
        await using var world = await AiSettingsCalls.StartAsync(_fixture);

        Action<System.Net.Http.Headers.HttpRequestHeaders> service = headers =>
            TenancyApiFactory.Resolved(
                serviceOrg: world.Org, serviceProject: world.Project, servicePermissions: ["authoring:read"]);

        var read = await AiSettingsCalls.GetAsync(world.Client, service, world.Settings);

        read.Status.ShouldBe(HttpStatusCode.Forbidden, "a member permission is not a service permission");

        var refused = await AiSettingsCalls.SaveAsync(
            world.Client, service, world, AiSettingsCalls.Body(AiSettingsCalls.Document()), "\"0\"");

        refused.Status.ShouldBe(HttpStatusCode.Forbidden);
        (await AiSettingsCalls.SettingsCountAsync(_fixture, world)).ShouldBe(0);
    }

    /// <summary>AC10: another org's member cannot reach the project at all — the answer is 404, never a 403 oracle.</summary>
    [Fact]
    public async Task ForeignOrgMember_Sees404()
    {
        await using var world = await AiSettingsCalls.StartAsync(_fixture);
        var otherOrg = await RegistryApi.CreateOrgAsync(world.Client, world.User, Unique.Slug("globex"));
        var otherProject = await RegistryApi.CreateProjectAsync(world.Client, world.User, otherOrg, Unique.Slug("billing"));

        foreach (var url in new[] { world.Settings, world.Versions, world.Templates })
        {
            var foreign = await AiSettingsCalls.GetAsync(world.Client, world.As, url.Replace(world.Project.ToString("D"), otherProject.ToString("D"), StringComparison.Ordinal));

            foreign.Status.ShouldBe(HttpStatusCode.NotFound, $"another org's project is invisible: {url}");
        }
    }
}
