using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Corvus.Json;
using Corvus.Json.Validator;
using Xunit;

namespace Contract.Tests;

/// <summary>
/// 01.3.2-T3.1 / AC4: every response the Api actually serves validates against the
/// shape <c>contracts/openapi.yaml</c> declares, and the 00 §9.2 envelope and
/// 00 §9.1 cursor-list fixtures match their spec schemas.
/// </summary>
/// <remarks>
/// <para>
/// The Api is booted in process on a closed port: these paths answer before a
/// handler reaches PostgreSQL. The bad-body cases post to the test-only probe
/// route (<see cref="Evidgate.Testing.TransportProbeStartupFilter"/>), which the
/// host pipeline drives exactly like a module route — request id, envelope
/// middleware and <c>ResultEndpointFilter</c> included.
/// </para>
/// <para>
/// The only spec-declared response reachable without authentication is the
/// 00 §9.2 error envelope — <c>#/components/schemas/ErrorEnvelope</c>, extracted
/// by pointer from the spec and never re-typed here — which this suite validates
/// on the live 400/422 bodies, plus the 00 §9.1 cursor-list shape the spec
/// declares.
/// </para>
/// </remarks>
public sealed class ResponseContractTests
{
    /// <summary>00 §9.2's own example: a batch item failure carries the 0-based <c>index</c>.</summary>
    private const string PerItemErrorEnvelope = """
        {"error":{"code":"manifest_invalid","message":"the batch is invalid","request_id":"req-1","retryable":false,"details":[{"index":3,"path":"/events/3/sequence","reason":"duplicate_different_payload"}]}}
        """;

    /// <summary>A document-level failure omits <c>index</c> (this wave's <c>ErrorDetail</c> addition).</summary>
    private const string DocumentLevelErrorEnvelope = """
        {"error":{"code":"request_invalid","message":"the request body is not valid JSON","request_id":"req-2","retryable":false,"details":[{"path":"project_ref","reason":"required"}]}}
        """;

    /// <summary>Missing <c>message</c>, <c>request_id</c> and <c>retryable</c> — all required by the spec.</summary>
    private const string EnvelopeMissingRequiredMembers = """{"error":{"code":"request_invalid"}}""";

    /// <summary>A <c>details[]</c> row without the required <c>reason</c>.</summary>
    private const string EnvelopeDetailMissingReason = """
        {"error":{"code":"request_invalid","message":"m","request_id":"r","retryable":false,"details":[{"path":"/events/3/sequence"}]}}
        """;

    /// <summary>00 §9.1's empty last page.</summary>
    private const string EmptyCursorPage = """{"items":[],"next_cursor":null}""";

    /// <summary>A page with a populated item and an opaque cursor (synthetic values).</summary>
    private const string PopulatedCursorPage = """
        {"items":[{"run_id":"018f0000-0000-7000-8000-000000000000","state":"complete"}],"next_cursor":"eyJjcmVhdGVkX2F0IjoiMjAyNi0wMS0wMVQwMDowMDowMFoiLCJpZCI6IjEifQ"}
        """;

    /// <summary>00 §9.1 requires <c>next_cursor</c> on every page, null when last.</summary>
    private const string CursorPageMissingNextCursor = """{"items":[]}""";

    /// <summary>The spec types <c>next_cursor</c> as <c>[string, 'null']</c>.</summary>
    private const string CursorPageWithNumericCursor = """{"items":[],"next_cursor":42}""";

    [Fact]
    public async Task Contract_Response_Validates_Against_Spec()
    {
        var spec = OpenApiSpec.Load();
        await using var factory = new EvidgateApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Request-Id", "req-contract-1");
        var cancellationToken = TestContext.Current.CancellationToken;

        // Implemented route on /v1: a body that never parsed is 400 request_invalid.
        using (var malformed = await client.PostAsync(
            Evidgate.Testing.TransportProbeStartupFilter.Path,
            new StringContent("{ not json", Encoding.UTF8, "application/json"),
            cancellationToken))
        {
            using var error = await AssertSpecEnvelopeAsync(spec, malformed, 400, "request_invalid");
            Assert.Empty(error.RootElement.GetProperty("error").GetProperty("details").EnumerateArray());
        }

        // Implemented route on /v1: a missing required field is 422 with a per-field detail.
        using (var missing = await client.PostAsJsonAsync(Evidgate.Testing.TransportProbeStartupFilter.Path, new { }, cancellationToken))
        {
            using var error = await AssertSpecEnvelopeAsync(spec, missing, 422, "request_invalid");
            var detail = Assert.Single(error.RootElement.GetProperty("error").GetProperty("details").EnumerateArray());
            Assert.Equal("project_ref", detail.GetProperty("path").GetString());
            Assert.Equal("required", detail.GetProperty("reason").GetString());
            Assert.False(
                detail.TryGetProperty("index", out _),
                "index is omitted (never null) for document-level reasons — 00 §9.2 types it optional");
        }
    }

    [Fact]
    public void Error_Envelope_Fixture_Validates_Against_Spec()
    {
        var spec = OpenApiSpec.Load();

        AssertValid(spec, "ErrorEnvelope", PerItemErrorEnvelope, "00 §9.2's per-item example (index present)");
        AssertValid(spec, "ErrorEnvelope", DocumentLevelErrorEnvelope, "a document-level error (index absent)");
        AssertInvalid(spec, "ErrorEnvelope", EnvelopeMissingRequiredMembers, "an envelope missing message/request_id/retryable");
        AssertInvalid(spec, "ErrorEnvelope", EnvelopeDetailMissingReason, "a details[] row without reason");
    }

    [Fact]
    public void Cursor_Pagination_Fixture_Validates_Against_Spec()
    {
        var spec = OpenApiSpec.Load();

        AssertValid(spec, "CursorList", EmptyCursorPage, "00 §9.1's empty last page");
        AssertValid(spec, "CursorList", PopulatedCursorPage, "a page with an item and an opaque cursor");
        AssertInvalid(spec, "CursorList", CursorPageMissingNextCursor, "a page without next_cursor");
        AssertInvalid(spec, "CursorList", CursorPageWithNumericCursor, "a page whose next_cursor is not a string");
    }

    /// <summary>
    /// Asserts the response is the 00 §9.2 envelope the spec declares, that the
    /// <c>X-Request-Id</c> header is echoed in <c>error.request_id</c> (00 §9.1),
    /// and returns the parsed body for shape assertions.
    /// </summary>
    private static async Task<JsonDocument> AssertSpecEnvelopeAsync(
        OpenApiSpec spec,
        HttpResponseMessage response,
        int expectedStatus,
        string expectedCode)
    {
        Assert.Equal(expectedStatus, (int)response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.Contains("X-Request-Id"), "every response carries the 00 §9.1 X-Request-Id header");

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        AssertValid(spec, "ErrorEnvelope", body, $"the {expectedStatus} response body");

        var document = JsonDocument.Parse(body);
        var error = document.RootElement.GetProperty("error");
        Assert.Equal(expectedCode, error.GetProperty("code").GetString());
        Assert.Equal(response.Headers.GetValues("X-Request-Id").Single(), error.GetProperty("request_id").GetString());
        Assert.False(error.GetProperty("retryable").GetBoolean(), "a client error is not retryable unchanged");
        Assert.Equal(JsonValueKind.Array, error.GetProperty("details").ValueKind);
        return document;
    }

    private static void AssertValid(OpenApiSpec spec, string schemaName, string instanceJson, string label)
    {
        var failures = Validate(spec, schemaName, instanceJson);
        Assert.True(
            failures.Length == 0,
            $"{label} must validate against components.schemas.{schemaName}: {string.Join(" | ", failures)}");
    }

    private static void AssertInvalid(OpenApiSpec spec, string schemaName, string instanceJson, string label)
    {
        var failures = Validate(spec, schemaName, instanceJson);
        Assert.True(
            failures.Length > 0,
            $"{label} must NOT validate against components.schemas.{schemaName}, or the valid cases above prove nothing");
    }

    /// <summary>Validates an instance against a spec <c>components.schemas</c> entry via Corvus; returns the failure rows.</summary>
    private static string[] Validate(OpenApiSpec spec, string schemaName, string instanceJson)
    {
        var schema = JsonSchema.FromText(
            spec.Schema(schemaName).ToJsonString(),
            $"urn:evidgate:contract-spec:{schemaName}",
            new JsonSchema.Options(allowFileSystemAndHttpResolution: false, alwaysAssertFormat: true));

        using var instance = JsonDocument.Parse(instanceJson);
        return
        [
            .. schema.Validate(instance.RootElement, ValidationLevel.Detailed).Results
                .Where(result => !result.Valid)
                .Select(result => $"{(result.Location is { } location ? location.Item3.ToString() : string.Empty)} {result.Message}".Trim()),
        ];
    }
}
