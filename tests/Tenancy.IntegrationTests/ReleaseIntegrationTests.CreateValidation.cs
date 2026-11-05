// Body validation and parse-shape coverage for POST /v1/projects/{p}/releases: every refusal a malformed
// body earns (name, state, digest, component key, environment revision, component count) and the grammar
// boundaries each rule still accepts.
#pragma warning disable CA1861
using System.Net;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class ReleaseIntegrationTests
{
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
    public async Task Create_NonObjectComponentItems_422ReleaseInvalid_WithIndex()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var revision = $"{world.EnvironmentRevision:D}";
        string valid = $$"""{"component_key":"web","digest":"{{Digest('a')}}","environment_revision_id":"{{revision}}"}""";
        (string Body, string Path)[] cases =
        [
            ($$"""{"name":"release-items","components":[{{valid}},7]}""", "/components/1"),
            ($$"""{"name":"release-items","components":[null]}""", "/components/0"),
            ($$"""{"name":"release-items","components":["x"]}""", "/components/0"),
        ];

        foreach (var (body, path) in cases)
        {
            var answer = await CreateAsync(world, body);
            ShouldBeReleaseInvalid(answer, path);
            answer.Body.GetProperty("error").GetProperty("details")[0].GetProperty("reason").GetString()
                .ShouldBe("invalid_type");
        }

        (await ReleaseCountAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task Create_NameWithInvisibleOrFormatCharacters_422ReleaseInvalid_AndUnicodeLettersAccepted()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);

        foreach (var name in new[] { "release\u2028line", "release\u200Bzero", "release\u202Ebidi" })
        {
            ShouldBeReleaseInvalid(await CreateAsync(world, Body(name, OneComponent(world))), "/name");
        }

        (await ReleaseCountAsync(world)).ShouldBe(0);

        // A name made of real Unicode letters and marks is not a formatting character: it is stored as sent.
        var accepted = await CreateAsync(world, Body("Rilis ñandú 1.0", OneComponent(world)));
        accepted.Status.ShouldBe(HttpStatusCode.Created, accepted.ErrorMessage);
        (await ReleaseCountAsync(world)).ShouldBe(1);
    }
}
