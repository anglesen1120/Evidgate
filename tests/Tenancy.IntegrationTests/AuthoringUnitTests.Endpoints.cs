using System.Text;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Modules.Authoring.Domain;
using Evidgate.Modules.Authoring.Endpoints;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 13.1.1-T2.1: the shared edge readers of the authoring routes, without a database or a host — the promise the three
/// route files inherit from <see cref="AuthoringEndpointSupport"/> is that a caller's own bytes become a 413/422 and
/// never a 500 (OI-13-116, T2.1 row: <c>AuthoringRequestValidationTests.MemberNameWithLoneSurrogate_Is422</c>).
/// </summary>
/// <remarks>
/// <see cref="AuthoringEndpointSupport.ReadBodyAsync(HttpContext, CancellationToken)"/> is driven through a
/// <see cref="DefaultHttpContext"/>, the same seam the delegates use, so the duplicate-member, lone-surrogate and cap
/// rules are proven here rather than only through a migrated database and a seeded tenant.
/// </remarks>
public sealed partial class AuthoringUnitTests
{
    /// <summary>A member NAME that is not valid UTF-16 (<c>{"\ud800":1}</c>) is a 422, never the reader's 500 (OI-13-116).</summary>
    [Fact]
    public async Task MemberNameWithLoneSurrogate_Is422()
    {
        var (body, error) = await ReadAsync("{\"\\ud800\":1}");

        BodyIsUndefined(body);
        // Whether the reader's duplicate detection (which materialises the name) throws InvalidOperationException or
        // JsonException depends on where it transcodes, so the reason is pinned to the pair that means "the caller's
        // own bytes": the guarantee under test is the 422, never the reader's internal throw site.
        AssertInvalidText(error, AuthoringEndpointSupport.InvalidMemberNamePath, string.Empty);
    }

    /// <summary>A lone surrogate in a string VALUE is refused the same way, and the detail names the member's path.</summary>
    [Fact]
    public async Task ValueWithLoneSurrogate_Is422()
    {
        var (body, error) = await ReadAsync("{\"title\":\"\\ud800\"}");

        BodyIsUndefined(body);
        AssertInvalidText(error, "/title", string.Empty);
    }

    /// <summary>
    /// A duplicate member is a 422 <c>invalid_json</c>: the reader would otherwise keep one of the two values silently,
    /// and the idempotency check of AC18 would hash a body the caller did not send.
    /// </summary>
    [Fact]
    public async Task DuplicateMember_Is422()
    {
        var (body, error) = await ReadAsync("{\"title\":\"a\",\"title\":\"b\"}");

        BodyIsUndefined(body);
        error.ShouldNotBeNull();
        error!.Code.ShouldBe(ApiErrorCode.RequestInvalid);
        error.Status.ShouldBe(StatusCodes.Status422UnprocessableEntity);
        error.Details.ShouldNotBeNull();
        error.Details!.ShouldContain(detail => detail.Reason == "invalid_json");
    }

    /// <summary>A body over the 64 KiB cap is 413 <c>payload_too_large</c> — measured while streaming, not only from Content-Length.</summary>
    [Fact]
    public async Task BodyOverCap_Is413()
    {
        var oversized = new string('a', AuthoringEndpointSupport.BodyCap + 1);
        var (body, error) = await ReadAsync($"{{\"title\":\"{oversized}\"}}", contentLength: null);

        BodyIsUndefined(body);
        error.ShouldNotBeNull();
        error!.Code.ShouldBe(ApiErrorCode.PayloadTooLarge);
        error.Status.ShouldBe(StatusCodes.Status413PayloadTooLarge);
    }

    /// <summary>A well-formed object is accepted unchanged, so the strictness above cannot be a blanket refusal.</summary>
    [Fact]
    public async Task ValidObject_IsAccepted()
    {
        var (body, error) = await ReadAsync("{\"title\":\"a budget\",\"steps\":[{\"no\":1,\"action\":\"open\"}]}");

        error.ShouldBeNull();
        body.ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Object);
    }

    /// <summary>
    /// A string value carrying U+0000 is refused as <c>invalid_text</c> at its member's pointer: PostgreSQL refuses the
    /// character in <c>text</c> (22021) and in <c>jsonb</c> (22P05), so storing it would be a 500 for the caller's own
    /// bytes (security S-1/S-2, the rule <c>Ingestion</c>'s <c>UnstorableText</c> states for its surface).
    /// </summary>
    [Fact]
    public async Task ValueWithNul_Is422AtTheMemberPath()
    {
        var (body, error) = await ReadAsync("{\"title\":\"a\\u0000b\"}");

        BodyIsUndefined(body);
        error.ShouldNotBeNull();
        error!.Code.ShouldBe(ApiErrorCode.RequestInvalid);
        error.Status.ShouldBe(StatusCodes.Status422UnprocessableEntity);
        error.Details!.ShouldContain(detail => detail.Path == "/title" && detail.Reason == "invalid_text");
    }

    /// <summary>
    /// The same character one level down answers the pointer of the leaf (<c>/steps/0/action</c>), so a client can fix
    /// the exact step rather than guess which member was refused.
    /// </summary>
    [Fact]
    public async Task NulInsideANestedValue_Is422AtTheLeafPointer()
    {
        var (body, error) = await ReadAsync("{\"steps\":[{\"no\":1,\"action\":\"open\",\"expected\":\"sh\\u0000wn\"}]}");

        BodyIsUndefined(body);
        error.ShouldNotBeNull();
        error!.Details!.ShouldContain(detail => detail.Path == "/steps/0/expected" && detail.Reason == "invalid_text");
    }

    /// <summary>
    /// A member <b>name</b> carrying U+0000 is refused at the stable placeholder rather than in a pointer built from
    /// the caller's own name (the rule the lone-surrogate name case already follows).
    /// </summary>
    [Fact]
    public async Task MemberNameWithNul_Is422AtThePlaceholder()
    {
        var (body, error) = await ReadAsync("{\"kind\":\"module\",\"ti\\u0000tle\":\"x\"}");

        BodyIsUndefined(body);
        AssertInvalidText(error, AuthoringEndpointSupport.InvalidMemberNamePath, string.Empty);
    }

    /// <summary>
    /// Exactly the 64 KiB cap is read when the body is streamed (no <c>Content-Length</c>): the cap is now measured
    /// before a chunk is buffered, so the boundary itself must still be inside it.
    /// </summary>
    [Fact]
    public async Task BodyAtCap_IsAcceptedWhenStreamed()
    {
        const string head = "{\"title\":\"";
        const string tail = "\"}";
        var json = head + new string('a', AuthoringEndpointSupport.BodyCap - head.Length - tail.Length) + tail;
        Encoding.UTF8.GetByteCount(json).ShouldBe(AuthoringEndpointSupport.BodyCap);

        var (body, error) = await ReadAsync(json, contentLength: null);

        error.ShouldBeNull();
        body.ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Object);
    }

    /// <summary>
    /// One byte past the cap is 413 when it arrives in the last chunk of a streamed body — the case the buffered-then-
    /// measured reader could only answer after allocating past the cap.
    /// </summary>
    [Fact]
    public async Task BodyJustOverCap_Is413WhenStreamed()
    {
        const string head = "{\"title\":\"";
        const string tail = "\"}";
        var json = head + new string('a', AuthoringEndpointSupport.BodyCap - head.Length - tail.Length + 1) + tail;

        var (body, error) = await ReadAsync(json, contentLength: null);

        BodyIsUndefined(body);
        error.ShouldNotBeNull();
        error!.Code.ShouldBe(ApiErrorCode.PayloadTooLarge);
        error.Status.ShouldBe(StatusCodes.Status413PayloadTooLarge);
    }

    /// <summary>
    /// An unknown member is echoed in the 422 only up to 64 characters, so a caller cannot turn a name into a
    /// kilobyte-long <c>details[].path</c> (the <c>TextRules.Brief</c> bound of the catalog's equivalent).
    /// </summary>
    [Fact]
    public async Task UnknownMemberName_IsClippedInTheDetailPath()
    {
        var name = new string('x', 200);
        var (body, error) = await ReadAsync($"{{\"title\":\"x\",\"{name}\":1}}");

        error.ShouldBeNull("the reader itself accepts a long, well-formed name; the clip is the member check's");
        var unknown = AuthoringEndpointSupport.UnknownMembers(
            body,
            ["title"],
            "the unit test body");

        unknown.ShouldNotBeNull();
        var detail = unknown!.Details!.Single();
        detail.Reason.ShouldBe("unknown_field");

        // The clip is `MaximumEchoedNameLength` characters plus the ellipsis, under the leading slash of the pointer.
        var expected = "/" + new string('x', AuthoringText.MaximumEchoedNameLength) + "…";
        detail.Path.ShouldBe(expected);
    }

    /// <summary>
    /// The clip stays on a rune boundary and shows control and format runes as <c>?</c>: cutting 63 ASCII characters
    /// before an astral character at a raw index 64 would end the echoed name on a lone high surrogate, which a
    /// <c>details[].path</c> must never carry (<c>AuthoringContentSurrogateTests</c> keeps the same out of the
    /// placeholder), and a raw newline or a U+202E would reorder the rendered line (security S-8, the
    /// <c>TextRules.Brief</c> rule).
    /// </summary>
    [Fact]
    public async Task UnknownMemberName_ClipsOnARuneBoundary_AndShowsFormatRunesAsQuestionMarks()
    {
        var split = new string('x', AuthoringText.MaximumEchoedNameLength - 1) + "😀";
        var (body, error) = await ReadAsync($"{{\"title\":\"x\",\"{split}\":1}}");
        error.ShouldBeNull();

        var clipped = AuthoringEndpointSupport.UnknownMembers(body, ["title"], "the unit test body")!.Details!.Single();
        clipped.Path.ShouldBe("/" + new string('x', AuthoringText.MaximumEchoedNameLength - 1) + "…");
        clipped.Path!.Any(char.IsSurrogate)
            .ShouldBeFalse("an echoed name never carries an unpaired surrogate");

        var (control, controlError) = await ReadAsync("{\"title\":\"x\",\"a\\u202eb\":1}");
        controlError.ShouldBeNull();
        AuthoringEndpointSupport.UnknownMembers(control, ["title"], "the unit test body")!.Details!.Single()
            .Path.ShouldBe("/a?b");

        var (newline, newlineError) = await ReadAsync("{\"title\":\"x\",\"ti\\nle\":1}");
        newlineError.ShouldBeNull();
        AuthoringEndpointSupport.UnknownMembers(newline, ["title"], "the unit test body")!.Details!.Single()
            .Path.ShouldBe("/ti?le");
    }

    /// <summary>Runs one raw body through the shared reader.</summary>
    /// <param name="json">The raw request body.</param>
    /// <param name="contentLength">The declared length, or null to make the reader stream and count.</param>
    private static async Task<(System.Text.Json.JsonElement Body, Evidgate.BuildingBlocks.SharedKernel.Results.Error? Error)> ReadAsync(
        string json,
        long? contentLength = -1)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = contentLength == -1 ? bytes.Length : contentLength;

        return await AuthoringEndpointSupport.ReadBodyAsync(context, CancellationToken.None);
    }

    /// <summary>A refused body answers no element at all (the caller must return the error unchanged).</summary>
    /// <param name="body">The element the reader answered.</param>
    private static void BodyIsUndefined(System.Text.Json.JsonElement body) =>
        body.ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Undefined);

    /// <summary>
    /// The shared 422 <c>request_invalid</c> shape of an unstorable string: <c>invalid_text</c> at the member's path
    /// when the value is the culprit, and the same reason at <see cref="AuthoringEndpointSupport.InvalidMemberNamePath"/>
    /// (or <c>invalid_json</c> at the root, when the parser refuses first) when a name is.
    /// </summary>
    /// <param name="error">The error the reader answered.</param>
    /// <param name="path">The expected <c>details[].path</c> of the text case.</param>
    /// <param name="orEmptyPath">An alternative path the same 422 may report instead.</param>
    private static void AssertInvalidText(
        Evidgate.BuildingBlocks.SharedKernel.Results.Error? error,
        string path,
        string orEmptyPath)
    {
        error.ShouldNotBeNull();
        error!.Code.ShouldBe(ApiErrorCode.RequestInvalid);
        error.Status.ShouldBe(StatusCodes.Status422UnprocessableEntity);
        error.Details.ShouldNotBeNull();
        error.Details!.ShouldContain(detail => (detail.Path == path || detail.Path == orEmptyPath)
            && (detail.Reason == "invalid_text" || detail.Reason == "invalid_json"));
    }
}
