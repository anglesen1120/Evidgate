using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The create route: one draft row, an ETag over its version, the author taken from the caller's membership,
/// idempotent replay through the request hash, and the content bounds the contract closes.
/// </summary>
public sealed partial class DraftTests
{
    [Fact]
    public async Task Create_StoresTheDraft_AndAnswers201WithTheVersionAsETag()
    {
        await using var world = await StartAsync();
        var membership = await MembershipAsync(world);

        var created = await CreateAsync(world);

        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
        created.ETag.ShouldBe("\"1\"", "the ETag is the draft's version, quotes included");
        created.Id().ShouldNotBe(Guid.Empty);
        created.Body.GetProperty("project_id").GetGuid().ShouldBe(world.Project);
        created.Body.GetProperty("author_membership_id").GetGuid().ShouldBe(membership);
        created.Body.GetProperty("kind").GetString().ShouldBe("template");
        created.Body.GetProperty("base_commit").GetString().ShouldBe("abcdef1234567");
        created.Body.GetProperty("status").GetString().ShouldBe("draft");
        created.Body.GetProperty("version").GetInt32().ShouldBe(1);
        created.Body.GetProperty("content").GetProperty("files")[0].GetProperty("path").GetString()
            .ShouldBe("tests/checkout.spec.ts");
        created.Body.GetProperty("allow_unverified").GetBoolean().ShouldBeFalse();
        created.Body.GetProperty("created_at").ValueKind.ShouldBe(JsonValueKind.String);
        foreach (var nullMember in new[] { "provenance", "ai_run_id", "dry_run_id", "unverified_reason", "pr_branch",
                     "pr_commit_sha", "pr_url", "pr_number", "linked_case_id", "linked_revision_id" })
        {
            created.Body.GetProperty(nullMember).ValueKind.ShouldBe(JsonValueKind.Null, $"{nullMember} starts absent");
        }

        (await DraftCountAsync(world)).ShouldBe(1);
        (await StoredAsync<int>(world, created.Id(), "version")).ShouldBe(1);
        (await AuditCountAsync(world, created.Id(), "draft.create")).ShouldBe(
            1, "the create and its audit row commit together");
    }

    [Fact]
    public async Task Create_ReplayingTheSameKeyAndBody_AnswersTheStoredResponseAndWritesNoSecondRow()
    {
        await using var world = await StartAsync();
        var key = NewKey();
        var body = Body();

        var first = await CreateAsync(world, body, key);
        first.Status.ShouldBe(HttpStatusCode.Created, first.ErrorMessage);

        var replay = await CreateAsync(world, body, key);

        replay.Status.ShouldBe(HttpStatusCode.Created, "a replay answers the original response");
        replay.Id().ShouldBe(first.Id());
        replay.ETag.ShouldBe(first.ETag);
        (await DraftCountAsync(world)).ShouldBe(1, "one key names one draft");
        (await AuditCountAsync(world, first.Id(), "draft.create")).ShouldBe(1, "the replay is not a second mutation");
    }

    [Fact]
    public async Task Create_ReusingTheKeyForAnotherBody_Is409IdempotencyConflict_AndWritesNothing()
    {
        await using var world = await StartAsync();
        var key = NewKey();
        (await CreateAsync(world, Body(), key)).Status.ShouldBe(HttpStatusCode.Created);

        var conflicting = await CreateAsync(world, Body("tests/other.spec.ts"), key);

        conflicting.Status.ShouldBe(HttpStatusCode.Conflict, conflicting.ErrorMessage);
        conflicting.ErrorCode.ShouldBe("idempotency_conflict");
        (await DraftCountAsync(world)).ShouldBe(1);
    }

    [Fact]
    public async Task Create_EachContentBound_Is422DraftInvalidNamingItsField()
    {
        await using var world = await StartAsync();
        var longPath = new string('a', 513);
        var longRef = new string('b', 513);
        var controlPath = "tests/" + (char)7 + ".ts";

        var invalid = new (string Scenario, string Field, JsonObject Body)[]
        {
            ("no files", "files", new JsonObject { ["kind"] = "template", ["base_commit"] = "abcdef1", ["content"] = Content() }),
            ("201 files", "files", new JsonObject
            {
                ["kind"] = "template", ["base_commit"] = "abcdef1",
                ["content"] = Content([.. Enumerable.Range(0, 201).Select(i => (JsonNode)File($"tests/{i}.ts"))]),
            }),
            ("path over 512 characters", "path", Body(longPath)),
            ("absolute path", "path", Body("/etc/passwd")),
            ("parent segment", "path", Body("tests/../secrets.ts")),
            ("backslash", "path", Body("tests\\checkout.ts")),
            ("control character in the path", "path", Body(controlPath)),
            ("duplicate path", "path", new JsonObject
            {
                ["kind"] = "template", ["base_commit"] = "abcdef1",
                ["content"] = Content(File("tests/one.ts"), File("tests/one.ts", bodyRef: "drafts/two.ts")),
            }),
            ("body_sha without the sha256 prefix", "body_sha", Body(sha: new string('a', 64))),
            ("body_sha with a non-hex character", "body_sha", Body(sha: "sha256:" + new string('g', 64))),
            ("body_ref over 512 characters", "body_ref", Body(bodyRef: longRef)),
            ("base_commit with a non-hex character", "base_commit", new JsonObject
            {
                ["kind"] = "template", ["base_commit"] = "zzzzzzz", ["content"] = Content(File()),
            }),
            ("base_commit shorter than 7", "base_commit", new JsonObject
            {
                ["kind"] = "template", ["base_commit"] = "abc123", ["content"] = Content(File()),
            }),
        };

        foreach (var (scenario, field, body) in invalid)
        {
            var refused = await CreateAsync(world, body);

            ShouldNameField(refused, field, scenario);
        }

        (await DraftCountAsync(world)).ShouldBe(0, "no refused body writes a row");

        // Positive control: the same shape with every member inside its bound is accepted.
        (await CreateAsync(world)).Status.ShouldBe(HttpStatusCode.Created);
    }

    [Theory]
    [InlineData("missing content", """{"kind":"template","base_commit":"abcdef1234567"}""", "content")]
    [InlineData("null content", """{"kind":"template","base_commit":"abcdef1234567","content":null}""", "content")]
    [InlineData("null files", """{"kind":"template","base_commit":"abcdef1234567","content":{"files":null}}""", "files")]
    [InlineData("null file", """{"kind":"template","base_commit":"abcdef1234567","content":{"files":[null]}}""", "files")]
    public async Task Create_NullContentShape_Is422DraftInvalid(string scenario, string json, string field)
    {
        await using var world = await StartAsync();
        var body = JsonNode.Parse(json)!.AsObject();

        ShouldNameField(await CreateAsync(world, body), field, scenario);
        (await DraftCountAsync(world)).ShouldBe(0);
        (await CreateAsync(world)).Status.ShouldBe(HttpStatusCode.Created);
    }

    [Theory]
    [InlineData("tests/./checkout.ts")]
    [InlineData("tests//checkout.ts")]
    [InlineData("tests/checkout.ts/")]
    public async Task Create_NonCanonicalPath_Is422DraftInvalid(string path)
    {
        await using var world = await StartAsync();
        ShouldNameField(await CreateAsync(world, Body(path)), "path", path);
        (await DraftCountAsync(world)).ShouldBe(0);
        (await CreateAsync(world)).Status.ShouldBe(HttpStatusCode.Created);
    }

    [Theory]
    [InlineData("../body.ts")]
    [InlineData("drafts/./body.ts")]
    [InlineData("drafts//body.ts")]
    [InlineData("drafts/body.ts/")]
    [InlineData("drafts/body name.ts")]
    public async Task Create_InvalidBodyPointer_Is422DraftInvalid(string bodyRef)
    {
        await using var world = await StartAsync();
        ShouldNameField(await CreateAsync(world, Body(bodyRef: bodyRef)), "body_ref", bodyRef);
        (await DraftCountAsync(world)).ShouldBe(0);
        (await CreateAsync(world)).Status.ShouldBe(HttpStatusCode.Created);
    }

    /// <summary>
    /// The 4096-byte provenance bound is the one byte bound a request can reach, and the column's CHECK counts
    /// <c>jsonb::text</c>, not the request: punctuation spaces and a re-rendered number make the stored value longer.
    /// Each seed pins one of those differences; the exact limit is accepted and the next byte is a 422, never a 500.
    /// </summary>
    [Theory]
    [InlineData("", "{}")]
    [InlineData("é", "{}")]
    [InlineData("", "{\"number\":1e5}")]
    public async Task Create_ProvenanceJsonbByteBoundary_AcceptsLimitAndRefusesTheNextByte(string prefix, string seed)
    {
        await using var world = await StartAsync();
        var body = Body();
        var provenance = JsonNode.Parse(seed)!.AsObject();
        provenance["text"] = prefix;
        var baseline = await _fixture.ScalarAsync<int>(
            world.Database, $"select octet_length(({SqlLiteral(provenance.ToJsonString())})::jsonb::text)");
        baseline.ShouldBeLessThan(4096, "the seed is far smaller than the bound");

        provenance["text"] = prefix + new string('a', 4096 - baseline);
        body["provenance"] = provenance;

        var created = await CreateAsync(world, body);
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
        (await StoredAsync<int>(world, created.Id(), "octet_length(provenance::text)")).ShouldBe(4096);

        provenance["text"] = prefix + new string('a', 4097 - baseline);
        ShouldNameField(await CreateAsync(world, body), "provenance", "4097 stored bytes");
        (await DraftCountAsync(world)).ShouldBe(1);
    }

    [Fact]
    public async Task Create_ProvenanceWithInsignificantWhitespace_IsMeasuredByItsStoredBytes()
    {
        await using var world = await StartAsync();
        var body = Body();
        var baseline = await _fixture.ScalarAsync<int>(world.Database, "select octet_length(('{\"t\":\"\"}')::jsonb::text)");
        var text = new string('a', 4096 - baseline);
        body["provenance"] = JsonNode.Parse("{\"t\":\"" + text + "\"}")!;
        var raw = body.ToJsonString().Replace("\"provenance\":{", "\"provenance\":{" + new string(' ', 30_000), StringComparison.Ordinal);

        var created = await world.Client.SendRawAsync(
            HttpMethod.Post,
            world.Collection,
            headers =>
            {
                world.Admin(headers);
                headers.Add("Idempotency-Key", NewKey());
            },
            raw);

        // Past six times the bound, so a raw-size proxy would refuse what the column accepts.
        Encoding.UTF8.GetByteCount(raw).ShouldBeGreaterThan(4096 * 6);
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
        (await StoredAsync<int>(world, created.Id(), "octet_length(provenance::text)")).ShouldBe(4096);
    }

    [Fact]
    public async Task Create_BodyNumberOutsideTheJsonRange_Is422RequestInvalid_NotA500()
    {
        await using var world = await StartAsync();
        var body = Body();
        body["provenance"] = JsonNode.Parse("{\"n\":1e9999999}")!;

        // The idempotency stage canonicalises the body first and refuses a number that is not a finite JSON number.
        var refused = await CreateAsync(world, body);
        refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, refused.ErrorMessage);
        refused.ErrorCode.ShouldBe("request_invalid");
        (await DraftCountAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task Create_ProvenanceNumbersWithinTheBounds_AreAccepted()
    {
        await using var world = await StartAsync();
        var body = Body();
        body["provenance"] = JsonNode.Parse("{\"n\":[1e-300,0.5,0e3,12345678901234567890]}")!;

        (await CreateAsync(world, body)).Status.ShouldBe(HttpStatusCode.Created);
    }

    [Theory]
    [InlineData("{\"n\":[1E-9999999]}")]
    [InlineData("{\"n\":0.1e-16384}")]
    [InlineData("{\"n\":0e-16384}")]
    [InlineData("{\"n\":0e131073}")]
    [InlineData("{\"n\":[1e-300,1e-300,1e-300,1e-300,1e-300,1e-300,1e-300,1e-300,1e-300,1e-300,1e-300,1e-300,1e-300,1e-300]}")]
    public async Task Create_ProvenanceNumberOutsideNumericRange_Is422DraftInvalid_NotA500(string provenance)
    {
        await using var world = await StartAsync();
        var body = Body();
        body["provenance"] = JsonNode.Parse(provenance)!;

        // The canonicaliser accepts these (finite doubles), but the jsonb cast would raise 22003 on them.
        ShouldNameField(await CreateAsync(world, body), "provenance", provenance);
        (await DraftCountAsync(world)).ShouldBe(0);
    }

    /// <summary>
    /// The content bound is reachable, and the column's CHECK counts <c>jsonb::text</c>: the limit itself is stored,
    /// the next byte is a 422 rather than the 23514 a body the app accepted could otherwise raise.
    /// </summary>
    [Fact]
    public async Task Create_ContentJsonbByteBoundary_AcceptsLimitAndRefusesTheNextByte()
    {
        await using var world = await StartAsync();
        var body = Body();

        body["content"] = await ContentAtStoredBytesAsync(world, 262_144);
        Encoding.UTF8
            .GetByteCount(body.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            .ShouldBeLessThanOrEqualTo(262_144, "the stored bound is reachable only while the wire body stays under the cap");

        var created = await CreateRelaxedAsync(world, body);
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
        (await StoredAsync<int>(world, created.Id(), "octet_length(content::text)")).ShouldBe(262_144);

        body["content"] = await ContentAtStoredBytesAsync(world, 262_145);
        ShouldNameField(await CreateRelaxedAsync(world, body), "content", "262145 stored bytes");
        (await DraftCountAsync(world)).ShouldBe(1);
    }

    /// <summary>A content whose stored <c>jsonb</c> text is exactly <paramref name="target"/> bytes.</summary>
    /// <remarks>
    /// The shape starts at its widest in <em>characters</em> — 200 files, a 512-character path and a 512-character body
    /// reference — and is then widened, never lengthened: replacing one ASCII character with a two-byte one adds
    /// exactly one stored byte and one wire byte. The baseline is measured, not assumed: <c>jsonb::text</c> is longer
    /// than the compact wire text (a space after every <c>:</c> and <c>,</c>, 1200 bytes for this shape), and that is
    /// the difference between the column's octets and the route's 256 KiB cap.
    /// </remarks>
    private async Task<JsonObject> ContentAtStoredBytesAsync(World world, int target)
    {
        var files = Enumerable.Range(0, 200)
            .Select(index => File($"{index:D3}/".PadRight(512, 'a'), bodyRef: new string('b', 512)))
            .ToArray();
        var remaining = target - await StoredBytesAsync(world, Content(files.Select(file => file.DeepClone()).ToArray()));
        remaining.ShouldBeGreaterThanOrEqualTo(0, "the widest shape is the baseline the boundary is reached from");

        foreach (var file in files)
        {
            var path = Math.Min(remaining, 512);
            if (path > 0)
            {
                file["path"] = Widen(file["path"]!.GetValue<string>(), path);
                remaining -= path;
            }

            var bodyRef = Math.Min(remaining, 512);
            if (bodyRef > 0)
            {
                file["body_ref"] = Widen(file["body_ref"]!.GetValue<string>(), bodyRef);
                remaining -= bodyRef;
            }
        }

        remaining.ShouldBe(0, "the shape has room for the requested widening");
        return Content(files);
    }

    /// <summary>Replaces the last <paramref name="take"/> ASCII characters with two-byte ones, keeping the length.</summary>
    private static string Widen(string value, int take) =>
        value[..(value.Length - take)] + new string('\u00e9', take);

    private Task<int> StoredBytesAsync(World world, JsonObject content) =>
        _fixture.ScalarAsync<int>(
            world.Database,
            $"select octet_length(({SqlLiteral(content.ToJsonString())})::jsonb::text)");

    private static Task<ApiAnswer> CreateRelaxedAsync(World world, JsonObject body) =>
        world.Client.SendRawAsync(
            HttpMethod.Post,
            world.Collection,
            headers =>
            {
                world.Admin(headers);
                headers.Add("Idempotency-Key", NewKey());
            },
            body.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));

    private static string SqlLiteral(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";

    [Fact]
    public async Task Create_BodyOver256KiB_Is413PayloadTooLarge_AndWritesNothing()
    {
        await using var world = await StartAsync();
        var oversized = new JsonObject
        {
            ["kind"] = "template",
            ["base_commit"] = "abcdef1234567",
            ["content"] = Content(File(bodyRef: "drafts/" + new string('x', 300 * 1024))),
        };

        var refused = await CreateAsync(world, oversized);

        refused.Status.ShouldBe(HttpStatusCode.RequestEntityTooLarge, refused.ErrorMessage);
        refused.ErrorCode.ShouldBe("payload_too_large");
        (await DraftCountAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task Create_ServicePrincipalHoldingDraftWrite_Is403Forbidden_AndWritesNothing()
    {
        await using var world = await StartAsync();

        // The service-role ceiling does not carry draft:write, so a machine principal is refused at the permission
        // stage before the author gate (which needs an active membership) can run; the founder still creates.
        var service = TenancyApiFactory.Resolved(serviceOrg: world.Org, servicePermissions: ["draft:write"]);

        var refused = await CreateAsync(world, caller: service);

        refused.Status.ShouldBe(HttpStatusCode.Forbidden, refused.ErrorMessage);
        refused.ErrorCode.ShouldBe("forbidden");
        (await DraftCountAsync(world)).ShouldBe(0);

        // Positive control: the founder, whose membership is active, creates.
        (await CreateAsync(world)).Status.ShouldBe(HttpStatusCode.Created);
    }
}
