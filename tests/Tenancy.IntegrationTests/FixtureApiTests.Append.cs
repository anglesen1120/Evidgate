// The append rules a client can break: a replayed Idempotency-Key, a body that sits on the route's size boundary, an
// option outside its closed set, a pointer carrying an invisible space and a pin whose revision number is spelled as a
// decimal. Each refusal reads the committed rows back, so the assertion is about the store and not the answer alone.
using System.Net;
using System.Text;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

public sealed partial class FixtureApiTests
{
    /// <summary>The cap both fixture write routes declare; a body at it is read, one byte over it is refused.</summary>
    private const int RouteBodyCap = 64 * 1024;

    /// <summary>A revision body of exactly <paramref name="bytes"/> UTF-8 bytes, padded by an unknown member.</summary>
    private static string RevisionBodyOfSize(int bytes)
    {
        const string Prefix = "{\"seed_version\":\"v2\",\"body_ref\":\"fixtures/orders/seed.sql\",\"note\":\"";
        const string Suffix = "\"}";
        var json = Prefix + new string('x', bytes - Prefix.Length - Suffix.Length) + Suffix;

        Encoding.UTF8.GetByteCount(json).ShouldBe(bytes, "the padded body is the size the test names");

        return json;
    }

    /// <summary>Posts a suite revision body spelled as JSON text, so a number can keep the form the client sent.</summary>
    private static Task<ApiAnswer> CreateRevisionRawAsync(World world, Guid suite, string json) =>
        world.Client.SendRawAsync(
            HttpMethod.Post,
            $"/v1/suites/{suite:D}/revisions",
            headers =>
            {
                world.Admin(headers);
                headers.Add("Idempotency-Key", NewKey());
            },
            json);

    /// <summary>The revision number the head suite revision pins at the first fixture reference.</summary>
    /// <remarks>
    /// Read as a numeric and returned as a decimal: the store keeps the number as sent, so a pin written as
    /// <c>1.0</c> reads back as the text <c>1.0</c> and a text-to-int cast would fail instead of comparing.
    /// </remarks>
    private Task<decimal> StoredPinnedRevisionAsync(World world, Guid suite) =>
        _fixture.ScalarAsync<decimal>(
            world.Database,
            $"""
            select (environment_requirements->'fixtures'->0->>'revision')::numeric
              from public.suite_revision
             where org_id = '{world.Org:D}' and suite_id = '{suite:D}'
             order by revision_no desc
             limit 1
            """);

    /// <summary>Asserts the 422 <c>request_invalid</c> that blames one member, so the refusal names what broke.</summary>
    private static void ShouldBeInvalid(ApiAnswer answer, string member, string reason)
    {
        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("request_invalid");
        answer.Body.GetProperty("error").GetProperty("details").EnumerateArray().ShouldContain(
            detail => detail.GetProperty("reason").GetString() == reason
                && (detail.GetProperty("path").GetString() ?? string.Empty).EndsWith(member, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Append_ReplayingTheSameKeyAndBody_AnswersTheStoredResponse()
    {
        await using var world = await StartAsync();
        var fixtureId = await CreateFixtureAsync(world, "orders");
        var key = NewKey();
        var body = RevisionBody(seedVersion: "v2");

        var first = await AppendAsync(world, fixtureId, body, idempotencyKey: key);
        first.Status.ShouldBe(HttpStatusCode.Created, first.ErrorMessage);
        first.Body.GetProperty("revision_no").GetInt32().ShouldBe(2);
        (await RevisionCountAsync(world, fixtureId)).ShouldBe(2);

        var replay = await AppendAsync(world, fixtureId, body, idempotencyKey: key);

        replay.Status.ShouldBe(first.Status, "a completed key answers with the stored status");
        replay.Id("revision_id").ShouldBe(first.Id("revision_id"));
        replay.Body.GetProperty("revision_no").GetInt32().ShouldBe(2);
        replay.Text("revision_hash").ShouldBe(first.Text("revision_hash"));
        replay.Body.GetProperty("created").GetBoolean().ShouldBeTrue("the stored response is replayed verbatim");
        (await RevisionCountAsync(world, fixtureId)).ShouldBe(2, "the replay allocated no third revision");
    }

    [Fact]
    public async Task Append_ReusingTheKeyForAnotherBody_409IdempotencyConflict()
    {
        await using var world = await StartAsync();
        var fixtureId = await CreateFixtureAsync(world, "orders");
        var key = NewKey();

        var first = await AppendAsync(world, fixtureId, RevisionBody(seedVersion: "v2"), idempotencyKey: key);
        first.Status.ShouldBe(HttpStatusCode.Created, first.ErrorMessage);

        var conflicting = await AppendAsync(world, fixtureId, RevisionBody(seedVersion: "v3"), idempotencyKey: key);

        conflicting.Status.ShouldBe(HttpStatusCode.Conflict, conflicting.ErrorMessage);
        conflicting.ErrorCode.ShouldBe("idempotency_conflict");
        (await RevisionCountAsync(world, fixtureId)).ShouldBe(2, "the refused append wrote nothing");

        var fresh = await AppendAsync(world, fixtureId, RevisionBody(seedVersion: "v3"), idempotencyKey: NewKey());

        fresh.Status.ShouldBe(HttpStatusCode.Created, "the same body under a new key is appended normally");
        fresh.Body.GetProperty("revision_no").GetInt32().ShouldBe(3);
    }

    [Fact]
    public async Task Append_WithoutAnIdempotencyKey_400RequestInvalid_AndNoRevision()
    {
        await using var world = await StartAsync();
        var fixtureId = await CreateFixtureAsync(world, "orders");

        var answer = await world.Client.SendRawAsync(
            HttpMethod.Post,
            World.Revisions(fixtureId),
            world.Admin,
            """{"seed_version":"v2","body_ref":"fixtures/orders/seed.sql"}""");

        answer.Status.ShouldBe(HttpStatusCode.BadRequest, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("request_invalid");
        answer.Body.GetProperty("error").GetProperty("details").EnumerateArray().ShouldContain(
            detail => detail.GetProperty("path").GetString() == "/Idempotency-Key"
                && detail.GetProperty("reason").GetString() == "required");
        (await RevisionCountAsync(world, fixtureId)).ShouldBe(1, "a request without its key never reaches the store");

        // The control: the same body carrying a key is appended normally.
        var control = await AppendAsync(world, fixtureId, RevisionBody(seedVersion: "v2"));

        control.Status.ShouldBe(HttpStatusCode.Created, control.ErrorMessage);
        control.Body.GetProperty("revision_no").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task Create_ADuplicateKeyWithoutAnIdempotencyKey_400RequestInvalid_AndTheRowsStayOne()
    {
        await using var world = await StartAsync();
        var fixtureId = await CreateFixtureAsync(world, "orders");

        var withoutKey = await world.Client.SendRawAsync(
            HttpMethod.Post,
            World.Fixtures(world.Project),
            world.Admin,
            """{"fixture_key":"orders","seed_version":"v1","body_ref":"fixtures/orders/seed.sql"}""");

        withoutKey.Status.ShouldBe(HttpStatusCode.BadRequest, withoutKey.ErrorMessage);
        withoutKey.ErrorCode.ShouldBe("request_invalid", "the missing key is refused before the key is looked up");
        (await FixtureCountAsync(world)).ShouldBe(1);
        (await RevisionCountAsync(world, fixtureId)).ShouldBe(1);

        // The control: the same body with a key reaches the store and is refused as a taken key, not as a bad request.
        var withKey = await CreateAsync(world, Body("orders"));

        withKey.Status.ShouldBe(HttpStatusCode.Conflict, withKey.ErrorMessage);
        withKey.ErrorCode.ShouldBe("fixture_key_taken");
        (await FixtureCountAsync(world)).ShouldBe(1);
        (await RevisionCountAsync(world, fixtureId)).ShouldBe(1);
    }

    [Fact]
    public async Task Append_ABodyOnTheRouteCap_IsRefusedForItsMember_AndOneByteMoreIs413()
    {
        await using var world = await StartAsync();
        var fixtureId = await CreateFixtureAsync(world, "orders");

        var over = await world.Client.SendRawAsync(
            HttpMethod.Post, World.Revisions(fixtureId), world.Admin, RevisionBodyOfSize(RouteBodyCap + 1));

        over.Status.ShouldBe(HttpStatusCode.RequestEntityTooLarge, over.ErrorMessage);
        over.ErrorCode.ShouldBe("payload_too_large");
        (await RevisionCountAsync(world, fixtureId)).ShouldBe(1, "an oversize body is refused before it is parsed");

        var atCap = await world.Client.SendRawAsync(
            HttpMethod.Post, World.Revisions(fixtureId), world.Admin, RevisionBodyOfSize(RouteBodyCap));

        atCap.Status.ShouldNotBe(
            HttpStatusCode.RequestEntityTooLarge, "a body of exactly the cap is not oversize, so the cap refused nothing");
        ShouldBeInvalid(atCap, "note", "unknown_field");
        (await RevisionCountAsync(world, fixtureId)).ShouldBe(1);

        var accepted = await AppendAsync(world, fixtureId, RevisionBody(seedVersion: "v2"));

        accepted.Status.ShouldBe(HttpStatusCode.Created, "the fixture still accepts a normal append");
        (await RevisionCountAsync(world, fixtureId)).ShouldBe(2);
    }

    [Fact]
    public async Task Revision_PinningAFixtureOfAnotherOrg_Is422_AndTheSameKeyInItsOwnProjectIsAccepted()
    {
        await using var owner = await StartAsync(slug: "owner");
        await using var rival = await StartAsync(slug: "rival");
        await CreateFixtureAsync(rival, "orders");
        var suite = await CreateSuiteAsync(owner, "smoke");

        var refused = await CreateRevisionAsync(owner, suite, Pin("orders", 1));

        ShouldBeCrossProject(refused);
        (await SuiteRevisionCountAsync(owner, suite)).ShouldBe(0, "another org's fixture is invisible, never pinned");

        // The control: the same key created in the suite's own project makes the identical body acceptable.
        await CreateFixtureAsync(owner, "orders");
        var accepted = await CreateRevisionAsync(owner, suite, Pin("orders", 1));

        accepted.Status.ShouldBe(HttpStatusCode.Created, accepted.ErrorMessage);
        (await SuiteRevisionCountAsync(owner, suite)).ShouldBe(1);
    }

    [Fact]
    public async Task Revision_PinningARevisionSpelledAsADecimalInteger_IsAccepted()
    {
        await using var world = await StartAsync();
        await CreateFixtureAsync(world, "orders");
        var suite = await CreateSuiteAsync(world, "smoke");

        var answer = await CreateRevisionRawAsync(
            world, suite, """{"environment_requirements":{"fixtures":[{"name":"orders","revision":1.0}]}}""");

        answer.Status.ShouldBe(HttpStatusCode.Created, answer.ErrorMessage);
        (await SuiteRevisionCountAsync(world, suite)).ShouldBe(1);
        (await StoredPinnedRevisionAsync(world, suite)).ShouldBe(1m, "1.0 is the integer 1 the schema describes");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public async Task Revision_PinningARevisionBelowOne_Is422(string revision)
    {
        await using var world = await StartAsync();
        await CreateFixtureAsync(world, "orders");
        var suite = await CreateSuiteAsync(world, "smoke");

        var refused = await CreateRevisionRawAsync(
            world,
            suite,
            $$$"""{"environment_requirements":{"fixtures":[{"name":"orders","revision":{{{revision}}}}]}}""");

        // The schema's minimum and the reference walk both refuse this number, so the path is asserted, not the reason.
        refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, refused.ErrorMessage);
        refused.ErrorCode.ShouldBe("request_invalid");
        refused.Body.GetProperty("error").GetProperty("details").EnumerateArray().ShouldContain(
            detail => (detail.GetProperty("path").GetString() ?? string.Empty)
                .Contains("fixtures/0", StringComparison.Ordinal));
        (await SuiteRevisionCountAsync(world, suite)).ShouldBe(0);

        var accepted = await CreateRevisionAsync(world, suite, Pin("orders", 1));

        accepted.Status.ShouldBe(HttpStatusCode.Created, "revision 1 of the same fixture is the positive control");
    }

    [Theory]
    [InlineData("\u2003")]
    [InlineData("\u3000")]
    [InlineData("\u2028")]
    public async Task Create_ABodyPointerCarryingAUnicodeSpace_422_AndNoRow(string space)
    {
        await using var world = await StartAsync();

        var answer = await CreateAsync(world, Body("orders", bodyRef: "fixtures/orders/seed.sql" + space));

        ShouldBeInvalid(answer, "BodyRef", "invalid_value");
        (await FixtureCountAsync(world)).ShouldBe(0, "a pointer the CHECK would refuse must never reach the insert");

        var control = await CreateAsync(world, Body("orders"));

        control.Status.ShouldBe(HttpStatusCode.Created, "the same pointer without the space is the positive control");
        (await FixtureCountAsync(world)).ShouldBe(1);
    }

    [Fact]
    public async Task Append_NonDefaultTimeoutAndCleanupPolicy_AreStoredAndChangeTheHeadHash()
    {
        await using var world = await StartAsync();
        var fixtureId = await CreateFixtureAsync(world, "orders");
        var defaultHash = (await StoredHeadAsync(world, fixtureId)).GetProperty("revision_hash").GetString();

        var answer = await AppendAsync(world, fixtureId, RevisionBody(seedVersion: "v1", timeoutS: 90, cleanupPolicy: "truncate"));

        answer.Status.ShouldBe(HttpStatusCode.Created, answer.ErrorMessage);
        answer.Body.GetProperty("revision_no").GetInt32().ShouldBe(2);

        var head = await StoredHeadAsync(world, fixtureId);
        head.GetProperty("timeout_s").GetInt32().ShouldBe(90);
        head.GetProperty("cleanup_policy").GetString().ShouldBe("truncate");
        head.GetProperty("seed_version").GetString().ShouldBe("v1", "only the two options differ from revision 1");
        head.GetProperty("body_ref").GetString().ShouldBe("fixtures/orders/seed.sql");
        var changedHash = head.GetProperty("revision_hash").GetString();
        changedHash.ShouldNotBe(defaultHash, "the hash covers the timeout and the policy");
        answer.Text("revision_hash").ShouldBe(changedHash);
    }

    [Theory]
    [InlineData("keep")]
    [InlineData("DROP")]
    public async Task Append_ACleanupPolicyOutsideTheList_422_AndNoRevision(string policy)
    {
        await using var world = await StartAsync();
        var fixtureId = await CreateFixtureAsync(world, "orders");

        var refused = await AppendAsync(world, fixtureId, RevisionBody(cleanupPolicy: policy));

        ShouldBeInvalid(refused, "CleanupPolicy", "invalid_value");
        (await RevisionCountAsync(world, fixtureId)).ShouldBe(1);

        var accepted = await AppendAsync(world, fixtureId, RevisionBody(cleanupPolicy: "truncate"));

        accepted.Status.ShouldBe(HttpStatusCode.Created, "a policy of the list is the positive control");
        (await StoredHeadAsync(world, fixtureId)).GetProperty("cleanup_policy").GetString().ShouldBe("truncate");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(86_401)]
    public async Task Append_ATimeoutOutsideTheRange_422_AndNoRevision(int timeoutS)
    {
        await using var world = await StartAsync();
        var fixtureId = await CreateFixtureAsync(world, "orders");

        var refused = await AppendAsync(world, fixtureId, RevisionBody(timeoutS: timeoutS));

        ShouldBeInvalid(refused, "TimeoutS", "out_of_range");
        (await RevisionCountAsync(world, fixtureId)).ShouldBe(1);

        var accepted = await AppendAsync(world, fixtureId, RevisionBody(timeoutS: 86_400));

        accepted.Status.ShouldBe(HttpStatusCode.Created, "the largest timeout in the range is the positive control");
        (await StoredHeadAsync(world, fixtureId)).GetProperty("timeout_s").GetInt32().ShouldBe(86_400);
    }
}
