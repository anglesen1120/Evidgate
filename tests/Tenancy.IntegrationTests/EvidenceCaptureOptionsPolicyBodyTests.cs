// The body of the kind-policy write: a media type that no jsonb cast or text column can carry is refused before the
// write, and a member the body does not declare is refused at binding. Both refusals leave the stored policy untouched.
using System.Net;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class EvidenceCaptureOptionsTests
{
    /// <summary>A complete policy body whose <c>media_types</c> member is the given JSON array text.</summary>
    private static string BodyWithMediaTypes(string mediaTypes) =>
        $$"""
        {"max_bytes":2048,"media_types":{{mediaTypes}},"default_sensitivity":"normal","capture_enabled":true,"reject_on_secret":false,"redaction_rules":[{"rule_type":"header","name_pattern":"authorization"}]}
        """;

    /// <summary>The stored media types of one project's policy rows, as <c>kind|type,type</c>.</summary>
    private Task<IReadOnlyList<string>> StoredMediaTypesAsync(World world) =>
        _fixture.ColumnAsync<string>(
            world.Database,
            "select kind || '|' || array_to_string(media_types, ',') from public.artifact_kind_policy "
            + $"where org_id = '{world.Org:D}' and project_id = '{world.Project:D}' order by kind");

    /// <summary>The chain's rows of this project's kind-policy writes: a refusal must leave the count unchanged.</summary>
    private Task<int> AuditRowCountAsync(World world) =>
        _fixture.ScalarAsync<int>(
            world.Database,
            "select count(*)::int from public.audit_log where action = 'artifact_kind_policy.set' "
            + $"and org_id = '{world.Org:D}' and object_id = '{world.Project:D}:trace'");

    [Fact]
    public async Task CaptureOptions_MediaTypeWithNul_422AndStoredPolicyUnchanged()
    {
        await using var world = await StartAsync();
        var member = world.MemberClient(world.Founder);
        var url = PolicyUrl(world.Project, "trace");

        var stored = await member.Client.SendRawAsync(
            HttpMethod.Put, url, member.Caller, BodyWithMediaTypes("""["application/zip"]"""));
        stored.Status.ShouldBe(HttpStatusCode.OK, stored.ErrorMessage);

        var answer = await member.Client.SendRawAsync(
            HttpMethod.Put, url, member.Caller, BodyWithMediaTypes("""["application/\u0000zip"]"""));

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, "a NUL cannot reach the jsonb cast the write makes");
        answer.ErrorCode.ShouldBe("request_invalid");
        (await StoredMediaTypesAsync(world)).ShouldBe(["trace|application/zip"], "the refused write replaces nothing");
        (await AuditRowCountAsync(world)).ShouldBe(1, "a refused write appends no audit row");
    }

    [Theory]
    [InlineData("""["application/zi\u0007p"]""")] // a C0 control character.
    [InlineData("""["application/zi\u202Ep"]""")] // a bidi format character: a type reads as another.
    [InlineData("""["   "]""")] // blank, which was refused before this check existed and stays refused.
    public async Task CaptureOptions_MediaTypeWithControlFormatOrBlank_422AndStoredPolicyUnchanged(string mediaTypes)
    {
        await using var world = await StartAsync();
        var member = world.MemberClient(world.Founder);
        var url = PolicyUrl(world.Project, "trace");

        var stored = await member.Client.SendRawAsync(
            HttpMethod.Put, url, member.Caller, BodyWithMediaTypes("""["application/zip"]"""));
        stored.Status.ShouldBe(HttpStatusCode.OK, stored.ErrorMessage);

        var answer = await member.Client.SendRawAsync(
            HttpMethod.Put, url, member.Caller, BodyWithMediaTypes(mediaTypes));

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("request_invalid");
        (await StoredMediaTypesAsync(world)).ShouldBe(["trace|application/zip"]);
        (await AuditRowCountAsync(world)).ShouldBe(1);
    }

    [Fact]
    public async Task CaptureOptions_LoneSurrogateMediaType_422AndStoredPolicyUnchanged()
    {
        await using var world = await StartAsync();
        var member = world.MemberClient(world.Founder);
        var url = PolicyUrl(world.Project, "trace");

        var stored = await member.Client.SendRawAsync(
            HttpMethod.Put, url, member.Caller, BodyWithMediaTypes("""["application/zip"]"""));
        stored.Status.ShouldBe(HttpStatusCode.OK, stored.ErrorMessage);

        // The token parses as JSON but its escape spells no UTF-16 text, so the entry is refused as a media type that
        // does not exist rather than as a malformed document, and the jsonb cast never sees it.
        var answer = await member.Client.SendRawAsync(
            HttpMethod.Put, url, member.Caller, BodyWithMediaTypes("""["application/\ud800zip"]"""));

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("request_invalid");
        (await StoredMediaTypesAsync(world)).ShouldBe(["trace|application/zip"]);
        (await AuditRowCountAsync(world)).ShouldBe(1);
    }

    [Theory]
    [InlineData("""["application/zip",5]""")] // a token that is not a string.
    [InlineData("""[null]""")] // the same refusal a null rule entry gets.
    public async Task CaptureOptions_MediaTypeEntryThatIsNotAString_422AndStoredPolicyUnchanged(string mediaTypes)
    {
        await using var world = await StartAsync();
        var member = world.MemberClient(world.Founder);
        var url = PolicyUrl(world.Project, "trace");

        var stored = await member.Client.SendRawAsync(
            HttpMethod.Put, url, member.Caller, BodyWithMediaTypes("""["application/zip"]"""));
        stored.Status.ShouldBe(HttpStatusCode.OK, stored.ErrorMessage);

        var answer = await member.Client.SendRawAsync(
            HttpMethod.Put, url, member.Caller, BodyWithMediaTypes(mediaTypes));

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("request_invalid");
        (await StoredMediaTypesAsync(world)).ShouldBe(["trace|application/zip"]);
        (await AuditRowCountAsync(world)).ShouldBe(1);
    }

    [Fact]
    public async Task CaptureOptions_UnknownTopLevelMember_400AndNoRows()
    {
        await using var world = await StartAsync();
        var member = world.MemberClient(world.Founder);
        var url = PolicyUrl(world.Project, "trace");

        // The positive control: the same five members, without the member the body does not declare.
        var accepted = await member.Client.SendRawAsync(HttpMethod.Put, url, member.Caller, PolicyBody());
        accepted.Status.ShouldBe(HttpStatusCode.OK, accepted.ErrorMessage);

        // `additionalProperties: false` in the contract is enforced at binding, so the extra member is not ignored.
        var answer = await member.Client.SendRawAsync(
            HttpMethod.Put, url, member.Caller,
            """
            {"max_bytes":4096,"media_types":["application/zip"],"default_sensitivity":"sensitive","capture_enabled":true,"reject_on_secret":true,"strict":true}
            """);

        answer.Status.ShouldBe(HttpStatusCode.BadRequest);
        answer.ErrorCode.ShouldBe("request_invalid");
        (await PolicyRowsAsync(world)).ShouldBe(["trace|2048|normal|true|false"], "the first write survives untouched");
        (await AuditRowCountAsync(world)).ShouldBe(1);
    }

    [Fact]
    public async Task CaptureOptions_MediaTypeLengthBoundary_255AcceptedAnd256Refused()
    {
        await using var world = await StartAsync();
        var member = world.MemberClient(world.Founder);
        var url = PolicyUrl(world.Project, "trace");

        var atLimit = new string('a', 255);
        var accepted = await member.Client.SendRawAsync(
            HttpMethod.Put, url, member.Caller, BodyWithMediaTypes($$"""["{{atLimit}}"]"""));
        accepted.Status.ShouldBe(HttpStatusCode.OK, accepted.ErrorMessage);
        (await StoredMediaTypesAsync(world)).ShouldBe([$"trace|{atLimit}"]);

        var overLimit = await member.Client.SendRawAsync(
            HttpMethod.Put, url, member.Caller, BodyWithMediaTypes($$"""["{{new string('b', 256)}}"]"""));
        overLimit.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        overLimit.ErrorCode.ShouldBe("request_invalid");
        (await StoredMediaTypesAsync(world)).ShouldBe([$"trace|{atLimit}"], "the longest stored type survives the refusal");
    }
}
