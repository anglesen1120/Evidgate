using System.Globalization;
using System.Text;
using System.Text.Json;
using Evidgate.BuildingBlocks.Contracts.Validation;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Contract.Tests;

/// <summary>
/// 01.3.2-T2.1: <see cref="ContractValidationFilter"/> — the per-route
/// <c>ValidateContractBody</c> filter that runs the compiled contract schema over
/// the bound <see cref="JsonDocument"/>/<see cref="JsonElement"/> argument before
/// the handler materializes a DTO (00 §5.5 edge "JSON Schema runs before
/// binding"), answers 413 <c>payload_too_large</c> for the <c>x-max-bytes</c>
/// transport cap (00 §9.3) and 422 <c>request_invalid</c> with the mapped
/// <c>details[]</c> rows (00 §9.2).
/// </summary>
/// <remarks>
/// No route mounts the filter until the Api wiring task, so these are the only
/// committed tests of that behaviour: the filter is invoked directly through a
/// local <see cref="EndpointFilterInvocationContext"/> fake, and the response it
/// writes is read back from a <see cref="MemoryStream"/> body. The expected
/// <c>details[]</c> triples are the corpus sidecars under
/// <c>fixtures/contracts/</c> (compared as the corpus's order-insensitive
/// <c>(index, path, reason)</c> set, fixtures/contracts/README.md) — never values
/// typed into this file.
/// </remarks>
public sealed class ContractValidationFilterTests
{
    private const string SchemaKey = ContractSchemaKeys.EventEnvelope;

    /// <summary>The effective <c>X-Request-Id</c>; <c>RequestIdMiddleware</c> assigns it to <see cref="HttpContext.TraceIdentifier"/> (00 §9.1).</summary>
    private const string RequestId = "req-contract-filter-1";

    /// <summary>Sentinel the <c>next</c> delegate returns, so the pass-through path is observable.</summary>
    private static readonly object NextResult = new();

    private static readonly Lazy<ContractSchemaSet> Schemas =
        new(() => ContractSchemaSet.Load(ContractPaths.ContractsDirectory));

    /// <summary>The schema's <c>x-max-bytes</c> cap (00 §9.3; 1 MiB for the event envelope).</summary>
    private static long MaxBytes => Schemas.Value.MaxBytes(SchemaKey)
        ?? throw new InvalidOperationException($"{SchemaKey} must declare x-max-bytes for the transport-cap tests");

    /// <summary>A valid document reaches the handler untouched.</summary>
    [Fact]
    public async Task Valid_Document_Passes_To_Next()
    {
        using var body = ReadVector("acc-01-single-event.json");

        var outcome = await InvokeAsync([body], contentLength: VectorLength("acc-01-single-event.json"));

        outcome.NextCalls.ShouldBe(1);
        outcome.HandlerResult.ShouldBeSameAs(NextResult);
        outcome.RawBody.ShouldBeEmpty();
    }

    /// <summary>
    /// The cap boundary is inclusive: a body exactly at <c>x-max-bytes</c> is not
    /// oversized (the corpus ships that vector as <c>acc-05</c> for this reason).
    /// </summary>
    [Fact]
    public async Task Document_At_Max_Bytes_Passes_To_Next()
    {
        VectorLength("acc-05-max-bytes-exact.json").ShouldBe(MaxBytes);
        using var body = ReadVector("acc-05-max-bytes-exact.json");

        var outcome = await InvokeAsync([body], contentLength: MaxBytes);

        outcome.NextCalls.ShouldBe(1);
        outcome.HandlerResult.ShouldBeSameAs(NextResult);
    }

    /// <summary>
    /// A batch rejected on its items answers the 00 §9.2 envelope with one
    /// <c>details[]</c> row per corpus triple, each carrying the 0-based
    /// <c>events[]</c> index (00 §9.4) — the per-item <c>index</c> case.
    /// </summary>
    [Fact]
    public async Task Batch_Item_Errors_Return_422_With_Per_Item_Index_Details()
    {
        const string vector = "rej-10-batch-item-errors.json";
        using var body = ReadVector(vector);

        var outcome = await InvokeAsync([body], contentLength: VectorLength(vector));

        outcome.NextCalls.ShouldBe(0);
        outcome.StatusCode.ShouldBe(422);
        outcome.ContentType.ShouldBe("application/json");

        using var response = JsonDocument.Parse(outcome.RawBody);
        var error = Error(response.RootElement);
        error.GetProperty("code").GetString().ShouldBe(ApiErrorCode.RequestInvalid);
        error.GetProperty("retryable").GetBoolean().ShouldBeFalse();
        error.GetProperty("request_id").GetString().ShouldBe(RequestId);

        Canonical(WireDetails(error)).ShouldBe(Canonical(SidecarDetails(vector)));

        // Every row is the per-item shape: index present, exactly the three wire keys.
        foreach (var detail in error.GetProperty("details").EnumerateArray())
        {
            detail.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)
                .ShouldBe(["index", "path", "reason"]);
        }

        // The body is the hosts' own envelope contract, not merely envelope-shaped.
        JsonSerializer.Deserialize(outcome.RawBody, EvidgateJsonContext.Default.ErrorEnvelope)!
            .Error.Code.ShouldBe(ApiErrorCode.RequestInvalid);
    }

    /// <summary>
    /// Document-level failures — an invalid batch envelope and a single-event
    /// reject — carry no <c>index</c>, and the key is <em>omitted on the wire</em>
    /// (<see cref="ErrorDetail.Index"/> is ignored when null), so a client's
    /// schema never sees <c>"index": null</c>.
    /// </summary>
    [Theory]
    [InlineData("rej-11-batch-envelope-invalid.json")]
    [InlineData("rej-02-single-missing-members.json")]
    public async Task Body_Level_Errors_Return_422_Without_Index_On_The_Wire(string vector)
    {
        using var body = ReadVector(vector);

        var outcome = await InvokeAsync([body], contentLength: VectorLength(vector));

        outcome.NextCalls.ShouldBe(0);
        outcome.StatusCode.ShouldBe(422);
        outcome.RawBody.ShouldNotContain("\"index\"");

        using var response = JsonDocument.Parse(outcome.RawBody);
        var error = Error(response.RootElement);
        error.GetProperty("code").GetString().ShouldBe(ApiErrorCode.RequestInvalid);
        error.GetProperty("retryable").GetBoolean().ShouldBeFalse();
        error.GetProperty("request_id").GetString().ShouldBe(RequestId);

        var details = WireDetails(error);
        details.ShouldAllBe(detail => detail.Index == null);
        Canonical(details).ShouldBe(Canonical(SidecarDetails(vector)));
        foreach (var detail in error.GetProperty("details").EnumerateArray())
        {
            detail.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)
                .ShouldBe(["path", "reason"]);
        }
    }

    /// <summary>
    /// A declared <c>Content-Length</c> over the cap is answered before parsing:
    /// 413 <c>payload_too_large</c> with the whole document as a single
    /// <c>body_too_large</c> detail at path <c>""</c> (00 §9.3), and no
    /// <c>index</c> key on the wire.
    /// </summary>
    [Fact]
    public async Task Content_Length_Over_Max_Bytes_Returns_413_PayloadTooLarge()
    {
        const string vector = "rej-16-over-max-bytes.json";
        // The corpus's own over-cap vector is exactly one byte past the cap.
        VectorLength(vector).ShouldBe(MaxBytes + 1);
        using var body = ReadVector(vector);

        var outcome = await InvokeAsync([body], contentLength: MaxBytes + 1);

        outcome.NextCalls.ShouldBe(0);
        outcome.StatusCode.ShouldBe(413);
        outcome.ContentType.ShouldBe("application/json");
        outcome.RawBody.ShouldNotContain("\"index\"");

        using var response = JsonDocument.Parse(outcome.RawBody);
        var error = Error(response.RootElement);
        error.GetProperty("code").GetString().ShouldBe(ApiErrorCode.PayloadTooLarge);
        error.GetProperty("retryable").GetBoolean().ShouldBeFalse();
        error.GetProperty("request_id").GetString().ShouldBe(RequestId);
        Canonical(WireDetails(error)).ShouldBe(Canonical(SidecarDetails(vector)));
    }

    /// <summary>
    /// A body with no <c>Content-Length</c> (chunked upload) falls back to the
    /// bound document's UTF-8 byte count. The body is built compactly so that
    /// measurement is unambiguous; the cap is decisive before schema validation,
    /// so the shape of the oversize document does not matter.
    /// </summary>
    [Fact]
    public async Task Oversize_Without_Content_Length_Returns_413()
    {
        var json = "{\"events\":[],\"pad\":\"" + new string('a', (int)MaxBytes) + "\"}";
        using var body = JsonDocument.Parse(json);

        var outcome = await InvokeAsync([body], contentLength: null);

        outcome.NextCalls.ShouldBe(0);
        outcome.StatusCode.ShouldBe(413);

        using var response = JsonDocument.Parse(outcome.RawBody);
        var error = Error(response.RootElement);
        error.GetProperty("code").GetString().ShouldBe(ApiErrorCode.PayloadTooLarge);
        Canonical(WireDetails(error)).ShouldBe(["-||body_too_large"]);
    }

    /// <summary>A <see cref="JsonElement"/> argument works the same as the bound <see cref="JsonDocument"/>.</summary>
    [Fact]
    public async Task JsonElement_Body_Argument_Is_Validated()
    {
        const string vector = "rej-10-batch-item-errors.json";
        using var body = ReadVector(vector);

        var outcome = await InvokeAsync([body.RootElement.Clone()], contentLength: VectorLength(vector));

        outcome.StatusCode.ShouldBe(422);
        using var response = JsonDocument.Parse(outcome.RawBody);
        Canonical(WireDetails(Error(response.RootElement))).ShouldBe(Canonical(SidecarDetails(vector)));
    }

    /// <summary>
    /// The filter validates the parse tree: a route that did not bind its body as
    /// <see cref="JsonDocument"/>/<see cref="JsonElement"/> fails loudly at request
    /// time instead of silently skipping validation.
    /// </summary>
    [Fact]
    public async Task Missing_Json_Body_Argument_Throws_InvalidOperationException()
    {
        var notABody = await Should.ThrowAsync<InvalidOperationException>(
            async () => await InvokeAsync(["not-a-bound-body"]));
        notABody.Message.ShouldContain("JsonDocument");

        await Should.ThrowAsync<InvalidOperationException>(async () => await InvokeAsync([]));
    }

    /// <summary>An unknown schema key is a wiring error, not a validation verdict.</summary>
    [Fact]
    public async Task Unknown_Schema_Key_Throws_ArgumentException()
    {
        using var body = ReadVector("acc-01-single-event.json");

        var thrown = await Should.ThrowAsync<ArgumentException>(
            async () => await InvokeAsync([body], schemaKey: "no-such-contract.v1"));

        thrown.Message.ShouldContain("no-such-contract.v1");
    }

    /// <summary>
    /// V-3 (01.3.2-T2 hardening): the <see cref="IHostEnvironment"/> overload
    /// walks upward from the content root to a real <c>contracts/</c> directory
    /// instead of assuming the content root is the repository root.
    /// </summary>
    [Fact]
    public void Host_Environment_Overload_Finds_Contracts_Above_The_Content_Root()
    {
        var root = Path.Combine(Path.GetTempPath(), $"contract-env-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "contracts", "schemas"));
            File.WriteAllText(
                Path.Combine(root, "contracts", "schemas", "probe.v1.json"),
                """{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object"}""");
            var contentRoot = Path.Combine(root, "src", "app");
            Directory.CreateDirectory(contentRoot);

            var services = new ServiceCollection();
            services.AddEvidgateContractValidation(new StubEnvironment(contentRoot));

            services.BuildServiceProvider()
                .GetRequiredService<ContractSchemaSet>()
                .Contains("probe.v1").ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Only the content root is read; the rest of the surface is inert.</summary>
    private sealed class StubEnvironment(string contentRootPath) : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "Contract.Tests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    /// <summary>Runs the filter over <paramref name="arguments"/> and captures what it produced.</summary>
    private static async Task<Outcome> InvokeAsync(
        object?[] arguments,
        long? contentLength = null,
        string schemaKey = SchemaKey)
    {
        var services = new ServiceCollection();
        services.AddSingleton(Schemas.Value);

        var httpContext = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            TraceIdentifier = RequestId,
            Response = { Body = new MemoryStream() },
        };
        httpContext.Request.ContentLength = contentLength;

        var nextCalls = 0;
        var result = await new ContractValidationFilter(schemaKey)
            .InvokeAsync(new FakeInvocationContext(httpContext, arguments), _ =>
            {
                nextCalls++;
                return ValueTask.FromResult<object?>(NextResult);
            })
            .ConfigureAwait(false);

        // The failure path returns an IResult instead of calling next: render it
        // the way the minimal-API pipeline would.
        if (result is IResult failure)
        {
            await failure.ExecuteAsync(httpContext).ConfigureAwait(false);
        }

        httpContext.Response.Body.Position = 0;
        var raw = await new StreamReader(httpContext.Response.Body)
            .ReadToEndAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(false);

        return new Outcome(result, nextCalls, httpContext.Response.StatusCode, httpContext.Response.ContentType, raw);
    }

    private static string VectorPath(string vectorFileName) =>
        Path.Combine(ContractPaths.FixturesDirectory, "vectors", SchemaKey, vectorFileName);

    private static long VectorLength(string vectorFileName) => new FileInfo(VectorPath(vectorFileName)).Length;

    /// <summary>The corpus vector, parsed exactly as the route's body parameter would be bound.</summary>
    private static JsonDocument ReadVector(string vectorFileName) =>
        JsonDocument.Parse(File.ReadAllBytes(VectorPath(vectorFileName)));

    /// <summary>The <c>error</c> member of the 00 §9.2 envelope.</summary>
    private static JsonElement Error(JsonElement envelope) => envelope.GetProperty("error");

    /// <summary>The <c>details[]</c> triples as they appear on the wire.</summary>
    private static (int? Index, string Path, string Reason)[] WireDetails(JsonElement error) =>
        error.GetProperty("details")
            .EnumerateArray()
            .Select(detail => (
                detail.TryGetProperty("index", out var index) ? index.GetInt32() : (int?)null,
                detail.GetProperty("path").GetString()!,
                detail.GetProperty("reason").GetString()!))
            .ToArray();

    /// <summary>The expected <c>details[]</c> triples of a reject vector's sidecar (fixtures/contracts/README.md).</summary>
    private static (int? Index, string Path, string Reason)[] SidecarDetails(string vectorFileName)
    {
        var sidecar = Path.Combine(
            ContractPaths.FixturesDirectory,
            "vectors",
            SchemaKey,
            Path.GetFileNameWithoutExtension(vectorFileName) + ".verdict.json");

        using var document = JsonDocument.Parse(File.ReadAllText(sidecar));
        return document.RootElement.GetProperty("errors")
            .EnumerateArray()
            .Select(error => (
                error.TryGetProperty("index", out var index) && index.ValueKind == JsonValueKind.Number
                    ? index.GetInt32()
                    : (int?)null,
                error.GetProperty("path").GetString()!,
                error.GetProperty("reason").GetString()!))
            .ToArray();
    }

    /// <summary>The corpus comparison form: sorted <c>index|path|reason</c> rows ("-" for a null index).</summary>
    private static string[] Canonical(IEnumerable<(int? Index, string Path, string Reason)> details) =>
        details
            .Select(detail => string.Join(
                '|',
                detail.Index?.ToString(CultureInfo.InvariantCulture) ?? "-",
                detail.Path,
                detail.Reason))
            .Order(StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// The filter needs nothing but an argument list and an <see cref="HttpContext"/>
    /// with request services; minimal-API binding supplies the same shape in production.
    /// </summary>
    private sealed class FakeInvocationContext(HttpContext httpContext, object?[] arguments)
        : EndpointFilterInvocationContext
    {
        public override HttpContext HttpContext { get; } = httpContext;

        public override IList<object?> Arguments { get; } = arguments;

        public override T GetArgument<T>(int index) => (T)Arguments[index]!;
    }

    /// <summary>What the filter produced for one invocation.</summary>
    private sealed record Outcome(
        object? HandlerResult,
        int NextCalls,
        int StatusCode,
        string? ContentType,
        string RawBody);
}
