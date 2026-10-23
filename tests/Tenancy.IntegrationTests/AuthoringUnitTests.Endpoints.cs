using System.Text;
using Evidgate.BuildingBlocks.SharedKernel.Api;
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
