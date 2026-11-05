// A suite revision pins the per-run data it needs by fixture key and revision number, and both must be held by the
// suite's own project at exactly that number. These cases drive the real suite revision route over real fixture rows.
#pragma warning disable CA1861
using System.Net;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

public sealed partial class FixtureApiTests
{
    private static Task<ApiAnswer> CreateRevisionAsync(World world, Guid suite, object body) =>
        world.Client.SendAsync(HttpMethod.Post, $"/v1/suites/{suite:D}/revisions", world.Admin, body, NewKey());

    /// <summary>A revision body whose only requirement is one pinned fixture.</summary>
    private static object Pin(string name, int revision) =>
        new { environment_requirements = new { fixtures = new[] { new { name, revision } } } };

    private static async Task<Guid> CreateSuiteAsync(World world, string slug)
    {
        var created = await world.Client.SendAsync(
            HttpMethod.Post,
            $"/v1/projects/{world.Project:D}/suites",
            world.Admin,
            new { name = slug, slug },
            NewKey());
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);

        return created.Id();
    }

    /// <summary>The 422 a reference to a fixture the suite's project does not hold at that number earns.</summary>
    private static void ShouldBeCrossProject(ApiAnswer answer)
    {
        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("cross_project_member");
        answer.Body.GetProperty("error").GetProperty("details").EnumerateArray().ShouldContain(
            detail => detail.GetProperty("path").GetString() == "/environment_requirements/fixtures/0"
                && detail.GetProperty("reason").GetString() == "unknown_fixture_revision");
    }

    private Task<long> SuiteRevisionCountAsync(World world, Guid suite) =>
        _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.suite_revision where org_id = '{world.Org:D}' and suite_id = '{suite:D}'");

    private Task<string> StoredRequirementsAsync(World world, Guid suite) =>
        _fixture.ScalarAsync<string>(
            world.Database,
            $"""
            select environment_requirements::text
              from public.suite_revision
             where org_id = '{world.Org:D}' and suite_id = '{suite:D}'
             order by revision_no desc
             limit 1
            """);

    [Fact]
    public async Task Revision_PinningAFixtureOfItsOwnProject_IsAccepted()
    {
        await using var world = await StartAsync();
        await CreateFixtureAsync(world, "orders");
        var suite = await CreateSuiteAsync(world, "smoke");

        var answer = await CreateRevisionAsync(world, suite, Pin("orders", 1));

        answer.Status.ShouldBe(HttpStatusCode.Created, answer.ErrorMessage);
        answer.Body.GetProperty("created").GetBoolean().ShouldBeTrue();
        (await SuiteRevisionCountAsync(world, suite)).ShouldBe(1);
        (await StoredRequirementsAsync(world, suite)).ShouldContain("\"fixtures\"");
    }

    [Fact]
    public async Task Revision_PinningAFixtureOfAnotherProject_Is422_AndItsOwnProjectReferenceIsAccepted()
    {
        await using var world = await StartAsync();
        var other = await AddProjectAsync(world, "other");
        var inOther = await CreateAsync(world, Body("orders"), project: other);
        inOther.Status.ShouldBe(HttpStatusCode.Created, inOther.ErrorMessage);
        var suite = await CreateSuiteAsync(world, "smoke");

        var refused = await CreateRevisionAsync(world, suite, Pin("orders", 1));

        ShouldBeCrossProject(refused);
        (await SuiteRevisionCountAsync(world, suite)).ShouldBe(0, "a refused reference writes no revision");

        // The same body with a fixture of the suite's own project is the control: only the project changed.
        await CreateFixtureAsync(world, "orders");
        var accepted = await CreateRevisionAsync(world, suite, Pin("orders", 1));

        accepted.Status.ShouldBe(HttpStatusCode.Created, accepted.ErrorMessage);
        (await SuiteRevisionCountAsync(world, suite)).ShouldBe(1);
    }

    [Fact]
    public async Task Revision_PinningAnUnknownFixture_Is422()
    {
        await using var world = await StartAsync();
        await CreateFixtureAsync(world, "orders");
        var suite = await CreateSuiteAsync(world, "smoke");

        var refused = await CreateRevisionAsync(world, suite, Pin("ghost", 1));

        ShouldBeCrossProject(refused);
        (await SuiteRevisionCountAsync(world, suite)).ShouldBe(0);

        (await CreateRevisionAsync(world, suite, Pin("orders", 1))).Status.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Revision_PinningARevisionNumberTheFixtureDoesNotHold_Is422()
    {
        await using var world = await StartAsync();
        await CreateFixtureAsync(world, "orders");
        var suite = await CreateSuiteAsync(world, "smoke");

        var refused = await CreateRevisionAsync(world, suite, Pin("orders", 2));

        ShouldBeCrossProject(refused);
        (await SuiteRevisionCountAsync(world, suite)).ShouldBe(0);

        var accepted = await CreateRevisionAsync(world, suite, Pin("orders", 1));

        accepted.Status.ShouldBe(HttpStatusCode.Created, accepted.ErrorMessage);
    }

    [Fact]
    public async Task Revision_WithoutFixtures_IsAccepted_AndRecordsNone()
    {
        await using var world = await StartAsync();
        var suite = await CreateSuiteAsync(world, "smoke");

        var answer = await CreateRevisionAsync(world, suite, new { members = Array.Empty<object>() });

        answer.Status.ShouldBe(HttpStatusCode.Created, answer.ErrorMessage);
        (await SuiteRevisionCountAsync(world, suite)).ShouldBe(1);
        (await StoredRequirementsAsync(world, suite)).ShouldNotContain(
            "fixtures", Case.Sensitive, "a suite that needs no fixture is unaffected");
    }
}
