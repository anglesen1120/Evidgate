// Create, validation, idempotency and scope coverage for POST /v1/projects/{p}/releases.
#pragma warning disable CA1861
using System.Net;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

public sealed partial class ReleaseIntegrationTests
{
    [Fact]
    public async Task Create_StoresExactDigests_AndGetReturnsTheDocumentedShape()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var digestApi = Digest('b');
        var digestWeb = Digest('c');
        var body = Body(
            "Checkout build",
            [
                new("web", digestWeb, world.EnvironmentRevision),
                new("api", digestApi, world.EnvironmentRevision),
            ]);

        var created = await CreateAsync(world, body);
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
        created.Body.EnumerateObject().Select(member => member.Name).ShouldBe(["release_id"]);
        var releaseId = created.Id("release_id");

        (await ReleaseCountAsync(world)).ShouldBe(1);
        (await ComponentCountAsync(world, releaseId)).ShouldBe(2);

        var got = await GetReleaseAsync(world, releaseId);
        got.Status.ShouldBe(HttpStatusCode.OK, got.ErrorMessage);
        var release = got.Body;
        release.GetProperty("release_id").GetGuid().ShouldBe(releaseId);
        release.GetProperty("project_id").GetGuid().ShouldBe(world.Project);
        release.GetProperty("name").GetString().ShouldBe("Checkout build");
        release.GetProperty("state").GetString().ShouldBe("draft");
        release.GetProperty("created_by").GetString().ShouldBe($"user:{world.User:D}");
        release.GetProperty("created_at").GetDateTimeOffset().ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-15));
        release.GetProperty("version").GetInt32().ShouldBe(1);
        release.TryGetProperty("bound_runs", out _).ShouldBeFalse("a release read carries no bound run");
        release.TryGetProperty("runs", out _).ShouldBeFalse("a release read carries no bound run");

        var components = release.GetProperty("components").EnumerateArray()
            .ToDictionary(
                component => component.GetProperty("component_key").GetString()!,
                component => (
                    Digest: component.GetProperty("digest").GetString(),
                    EnvironmentRevision: component.GetProperty("environment_revision_id").GetGuid()),
                StringComparer.Ordinal);
        components.Keys.ShouldBe(["api", "web"], ignoreOrder: false);
        components["api"].Digest.ShouldBe(digestApi);
        components["web"].Digest.ShouldBe(digestWeb);
        components["web"].EnvironmentRevision.ShouldBe(world.EnvironmentRevision);
    }

    [Fact]
    public async Task Create_DefaultStateIsDraft_AndActiveIsStored()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);

        var draftId = await CreateReleaseAsync(world, "release-default", state: null, OneComponent(world));
        var activeId = await CreateReleaseAsync(world, "release-active", "active", OneComponent(world, fill: 'b'));

        (await GetReleaseAsync(world, draftId)).Body.GetProperty("state").GetString().ShouldBe("draft");
        (await GetReleaseAsync(world, activeId)).Body.GetProperty("state").GetString().ShouldBe("active");
    }

    [Fact]
    public async Task Create_NameValidation_RefusesUntrimmed_AndAcceptsTheTrimmedBoundary()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var boundary = new string('n', 128);

        // The name is declared, not repaired: surrounding whitespace is refused rather than trimmed.
        ShouldBeReleaseInvalid(await CreateAsync(world, Body("  padded name  ", OneComponent(world), state: null)));
        var exact = await CreateAsync(world, Body("padded name", OneComponent(world)));
        exact.Status.ShouldBe(HttpStatusCode.Created, exact.ErrorMessage);
        (await GetReleaseAsync(world, exact.Id("release_id"))).Body.GetProperty("name").GetString().ShouldBe("padded name");

        var longest = await CreateAsync(world, Body(boundary, OneComponent(world, fill: 'b')));
        longest.Status.ShouldBe(HttpStatusCode.Created, longest.ErrorMessage);
        (await GetReleaseAsync(world, longest.Id("release_id"))).Body.GetProperty("name").GetString().ShouldBe(boundary);
    }

    [Fact]
    public async Task Create_OneHundredComponentsIsAccepted_OneHundredOneIsReleaseInvalid()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        static Component[] Build(RunCreationTests.World world, int count) =>
            [.. Enumerable.Range(0, count)
                .Select(index => new Component($"component-{index}", Digest((char)('a' + (index % 6))), world.EnvironmentRevision))];

        var hundred = await CreateAsync(world, Body("hundred", Build(world, 100)));
        hundred.Status.ShouldBe(HttpStatusCode.Created, hundred.ErrorMessage);
        (await ComponentCountAsync(world, hundred.Id("release_id"))).ShouldBe(100);

        var over = await CreateAsync(world, Body("hundred-and-one", Build(world, 101)));
        ShouldBeReleaseInvalid(over);
        (await ReleaseCountAsync(world)).ShouldBe(1);
    }

    [Fact]
    public async Task Create_MalformedDigest_422ReleaseInvalid()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        string[] malformed =
        [
            "sha256:" + new string('a', 63),
            "sha256:" + new string('a', 65),
            "sha256:" + new string('A', 64),
            "SHA256:" + new string('a', 64),
            "sha256" + new string('a', 64),
            "sha256:" + new string('g', 64),
            "sha256:" + new string('a', 63) + "\n",
        ];

        foreach (var digest in malformed)
        {
            var answer = await CreateAsync(
                world,
                Body("release-digest", [new("web", digest, world.EnvironmentRevision)]));
            ShouldBeReleaseInvalid(answer);
        }

        (await ReleaseCountAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task Create_ComponentKeyValidation_RefusesMalformed_AndAcceptsTheGrammarBoundaries()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        string[] malformed = ["Web", "-web", "web.x", "web space", new string('a', 65), string.Empty];

        foreach (var key in malformed)
        {
            var answer = await CreateAsync(
                world,
                Body("release-key", [new(key, Digest('d'), world.EnvironmentRevision)]));
            ShouldBeReleaseInvalid(answer);
        }

        // The grammar permits a trailing underscore and exactly 64 lowercase characters.
        var trailing = await CreateAsync(world, Body("release-key-valid", [new("web_", Digest('e'), world.EnvironmentRevision)]));
        trailing.Status.ShouldBe(HttpStatusCode.Created, trailing.ErrorMessage);
        var longest = await CreateAsync(
            world,
            Body("release-key-long", [new(new string('a', 64), Digest('f'), world.EnvironmentRevision)]));
        longest.Status.ShouldBe(HttpStatusCode.Created, longest.ErrorMessage);

        (await ReleaseCountAsync(world)).ShouldBe(2);
    }

    [Fact]
    public async Task Create_InvalidName_422ReleaseInvalid()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        string[] malformed = [string.Empty, "   ", new string('n', 129), "bad\u0001name", "bad\nname"];

        foreach (var name in malformed)
        {
            ShouldBeReleaseInvalid(await CreateAsync(world, Body(name, OneComponent(world))));
        }

        (await ReleaseCountAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task Create_InvalidState_422ReleaseInvalid()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);

        var answer = await CreateAsync(world, Body("release-state", OneComponent(world), state: "retired"));

        ShouldBeReleaseInvalid(answer);
        (await ReleaseCountAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task Create_EmptyComponents_422ReleaseInvalid()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);

        var answer = await CreateAsync(world, Body("release-empty", []));

        ShouldBeReleaseInvalid(answer);
        (await ReleaseCountAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task Create_DuplicateComponentKey_422ReleaseInvalid()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var body = Body(
            "release-duplicate",
            [
                new("web", Digest('a'), world.EnvironmentRevision),
                new("web", Digest('b'), world.EnvironmentRevision),
            ]);

        ShouldBeReleaseInvalid(await CreateAsync(world, body));
        (await ReleaseCountAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task Create_MissingOrForeignEnvironmentRevision_422ReleaseInvalid()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var (_, _, foreignRevision) = await SeedForeignTenantAsync(world.Database);

        var missing = await CreateAsync(
            world,
            Body("release-missing-revision", [new("web", Digest('a'), Guid.CreateVersion7())]));
        ShouldBeReleaseInvalid(missing, "/components/0/environment_revision_id");

        var foreign = await CreateAsync(
            world,
            Body("release-foreign-revision", [new("web", Digest('a'), foreignRevision)]));
        ShouldBeReleaseInvalid(foreign, "/components/0/environment_revision_id");

        (await ReleaseCountAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task Create_UnknownOrRepeatedMember_422RequestInvalid()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var topLevel = $$"""
            {"name":"release-extra","components":[],"note":"not a member"}
            """;
        var nested = $$"""
            {"name":"release-extra","components":[{"component_key":"web","digest":"{{Digest('a')}}","environment_revision_id":"{{world.EnvironmentRevision:D}}","arch":"x64"}]}
            """;

        var repeated = $$"""
            {"name":"release-extra","name":"release-repeated","components":[]}
            """;

        ShouldBeShapeInvalid(await CreateAsync(world, topLevel));
        ShouldBeShapeInvalid(await CreateAsync(world, nested));
        ShouldBeShapeInvalid(await CreateAsync(world, repeated));
        (await ReleaseCountAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task Create_WrongMemberType_422RequestInvalid_ForDeclaredShapeMembers()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var revision = $"{world.EnvironmentRevision:D}";
        string[] bodies =
        [
            $$"""{"name":7,"components":[]}""",
            $$"""{"name":"release-typed","components":"none"}""",
            $$"""{"name":"release-typed","components":[{"component_key":"web","digest":7,"environment_revision_id":"{{revision}}"}]}""",
            $$"""{"name":"release-typed","components":[{"component_key":"web","digest":"{{Digest('a')}}","environment_revision_id":7}]}""",
            $$"""{"name":"release-typed","components":[{"component_key":"web","digest":"{{Digest('a')}}","environment_revision_id":"{{revision}}"}],"state":true}""",
        ];

        foreach (var body in bodies)
        {
            // A declared member of the wrong JSON type is a shape failure of this body class.
            ShouldBeShapeInvalid(await CreateAsync(world, body));
        }

        (await ReleaseCountAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task Create_ComponentItemWrongType_422ReleaseInvalid()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var revision = $"{world.EnvironmentRevision:D}";
        string[] bodies =
        [
            $$"""{"name":"release-typed","components":[{"component_key":7,"digest":"{{Digest('a')}}","environment_revision_id":"{{revision}}"}]}""",
            $$"""{"name":"release-typed","components":[{"component_key":"web","digest":7,"environment_revision_id":"{{revision}}"}]}""",
            $$"""{"name":"release-typed","components":[{"component_key":"web","digest":"{{Digest('a')}}","environment_revision_id":7}]}""",
        ];

        foreach (var body in bodies)
        {
            ShouldBeReleaseInvalid(await CreateAsync(world, body));
        }

        (await ReleaseCountAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task Create_MalformedJson_400RequestInvalid()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);

        ShouldBeRequestInvalid(await CreateAsync(world, "{\"name\":"));
        (await ReleaseCountAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task Create_DuplicateName_409ReleaseNameConflict_AndOtherProjectNameIsFree()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var first = await CreateAsync(world, Body("release-one", OneComponent(world)));
        first.Status.ShouldBe(HttpStatusCode.Created, first.ErrorMessage);

        var duplicate = await CreateAsync(world, Body("release-one", OneComponent(world, fill: 'b')));
        duplicate.Status.ShouldBe(HttpStatusCode.Conflict, duplicate.ErrorMessage);
        duplicate.ErrorCode.ShouldBe("release_name_conflict");

        (await ReleaseCountAsync(world)).ShouldBe(1);
        (await ComponentCountAsync(world, first.Id("release_id"))).ShouldBe(1);

        var distinct = await CreateAsync(world, Body("release-two", OneComponent(world)));
        distinct.Status.ShouldBe(HttpStatusCode.Created, distinct.ErrorMessage);

        // The name is unique per project, and only per project.
        var otherProject = await CreateProjectAsync(world.Client, world.User, world.Org, Unique.Slug("other"));
        var sibling = await CreateAsync(
            world,
            Body("release-one", OneComponent(world)),
            project: otherProject);
        sibling.Status.ShouldBe(HttpStatusCode.Created, sibling.ErrorMessage);
    }

    [Fact]
    public async Task ReplaySameIdempotencyKey_ReturnsOriginalOneAuditRow()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var key = NewKey();
        var body = Body("release-replay", OneComponent(world), state: null);

        var first = await CreateAsync(world, body, idempotencyKey: key);
        first.Status.ShouldBe(HttpStatusCode.Created, first.ErrorMessage);
        var releaseId = first.Id("release_id");
        (await ReleaseAuditCountAsync(world, releaseId)).ShouldBe(1);

        var replay = await CreateAsync(world, body, idempotencyKey: key);
        replay.Status.ShouldBe(HttpStatusCode.Created, replay.ErrorMessage);
        replay.Id("release_id").ShouldBe(releaseId);

        (await ReleaseCountAsync(world)).ShouldBe(1);
        (await ComponentCountAsync(world, releaseId)).ShouldBe(1);
        (await IdempotencyCountAsync(world, key)).ShouldBe(1);
        (await ReleaseAuditCountAsync(world, releaseId)).ShouldBe(1);
    }

    [Fact]
    public async Task ReplayDifferentPayload_409IdempotencyConflict()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var key = NewKey();

        var first = await CreateAsync(world, Body("release-bound", OneComponent(world)), idempotencyKey: key);
        first.Status.ShouldBe(HttpStatusCode.Created, first.ErrorMessage);

        var conflicting = await CreateAsync(
            world,
            Body("release-bound", OneComponent(world, fill: 'b')),
            idempotencyKey: key);
        conflicting.Status.ShouldBe(HttpStatusCode.Conflict, conflicting.ErrorMessage);
        conflicting.ErrorCode.ShouldBe("idempotency_conflict");

        (await ReleaseCountAsync(world)).ShouldBe(1);
        (await IdempotencyCountAsync(world, key)).ShouldBe(1);
    }

    [Fact]
    public async Task Create_MissingIdempotencyKey_400RequestInvalid_NoRows()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);

        var answer = await CreateAsync(world, Body("release-keyless", OneComponent(world)), omitIdempotencyKey: true);

        ShouldBeRequestInvalid(answer);
        (await ReleaseCountAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task Create_CrossTenantProject_404NotFound_NoLeak()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var (foreignOrg, foreignProject, _) = await SeedForeignTenantAsync(world.Database);

        var answer = await CreateAsync(world, Body("release-cross-tenant", OneComponent(world)), project: foreignProject);

        answer.Status.ShouldBe(HttpStatusCode.NotFound, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("not_found");
        (await ReleaseCountAsync(world.Database, foreignOrg)).ShouldBe(0);
        (await ReleaseCountAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task Get_CrossTenantAndUnknownId_404SameEnvelope()
    {
        await using var worldA = await RunCreationTests.StartAsync(_fixture, slug: "alpha");
        await using var worldB = await RunCreationTests.StartAsync(_fixture, slug: "beta");
        var releaseId = await CreateReleaseAsync(worldA, "release-alpha", state: null, OneComponent(worldA));

        var crossTenant = await GetReleaseAsync(worldA, releaseId, caller: worldB.Admin);
        var unknown = await GetReleaseAsync(worldB, Guid.CreateVersion7());

        crossTenant.Status.ShouldBe(HttpStatusCode.NotFound, crossTenant.ErrorMessage);
        unknown.Status.ShouldBe(HttpStatusCode.NotFound, unknown.ErrorMessage);
        Envelope(crossTenant).ShouldBe(Envelope(unknown));
        (await ReleaseCountAsync(worldB)).ShouldBe(0);
    }

    [Fact]
    public async Task Create_ServicePrincipalWithMatchingProjectScope_201()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var caller = TenancyApiFactory.Resolved(
            serviceOrg: world.Org,
            serviceProject: world.Project,
            servicePermissions: ["run:create"]);

        var answer = await CreateAsync(world, Body("release-service", OneComponent(world)), caller: caller);

        answer.Status.ShouldBe(HttpStatusCode.Created, answer.ErrorMessage);
        var got = await GetReleaseAsync(world, answer.Id("release_id"));
        got.Status.ShouldBe(HttpStatusCode.OK, got.ErrorMessage);
        got.Body.GetProperty("created_by").GetString().ShouldNotBeNullOrEmpty();
        got.Body.GetProperty("created_by").GetString()!.ShouldContain(":");
    }

    [Fact]
    public async Task Create_ServiceSiblingProjectScope_403ProjectScopeMismatch()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var sibling = await CreateProjectAsync(world.Client, world.User, world.Org, Unique.Slug("sibling"));
        var caller = TenancyApiFactory.Resolved(
            serviceOrg: world.Org,
            serviceProject: sibling,
            servicePermissions: ["run:create"]);

        var answer = await CreateAsync(world, Body("release-sibling", OneComponent(world)), caller: caller, project: world.Project);

        answer.Status.ShouldBe(HttpStatusCode.Forbidden, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("project_scope_mismatch");
        (await ReleaseCountAsync(world)).ShouldBe(0);
    }
}
