using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Evidgate.Api.Endpoints;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Hosts.IntegrationTests;

/// <summary>
/// 01.4.2-T4.4 / AC6: a pipeline Result becomes the 00 §9.2 response exactly once
/// — success carries the body and the status the command declared, a failure is the
/// envelope with the request id, and nothing is ever ProblemDetails (00 §5.5 C8) —
/// and the mounted probe route really renders through that filter.
/// </summary>
public sealed class EndpointFilterTests
{
    private const string DeadConnectionString =
        "Host=127.0.0.1;Port=1;Username=evidgate_app;Password=y;Database=evidgate;Timeout=2";

    [Fact]
    public async Task Result_Maps_To_9_2_Envelope()
    {
        // Success: the body the command returned, at the status it declared (201).
        var okContext = Context("req-filter-ok");
        var ok = Result.Success(new FilterBody("run-1"), status: 201).ToHttpResult(okContext);
        await ok.ExecuteAsync(okContext);

        okContext.Response.StatusCode.ShouldBe(201);
        okContext.Response.ContentType.ShouldBe("application/json");
        var success = JsonDocument.Parse(await ReadAsync(okContext)).RootElement;
        success.GetProperty("run_id").GetString().ShouldBe("run-1");
        success.TryGetProperty("error", out _).ShouldBeFalse();

        // Failure: one 00 §9.2 envelope, every field from the Error, request id echoed.
        var errorContext = Context("req-filter-error");
        var failed = Result
            .Failure(new Error(
                "idempotency_conflict",
                "the key was reused",
                409,
                Retryable: true,
                Details: [new ErrorDetail("Idempotency-Key", "reused")]))
            .ToHttpResult(errorContext);
        await failed.ExecuteAsync(errorContext);

        errorContext.Response.StatusCode.ShouldBe(409);
        errorContext.Response.ContentType.ShouldBe("application/json");
        var envelope = JsonDocument.Parse(await ReadAsync(errorContext)).RootElement;
        envelope.EnumerateObject().Select(property => property.Name).ShouldBe(["error"]);
        var error = envelope.GetProperty("error");
        error.GetProperty("code").GetString().ShouldBe("idempotency_conflict");
        error.GetProperty("message").GetString().ShouldBe("the key was reused");
        error.GetProperty("request_id").GetString().ShouldBe("req-filter-error");
        error.GetProperty("retryable").GetBoolean().ShouldBeTrue();
        error.GetProperty("details")[0].GetProperty("path").GetString().ShouldBe("Idempotency-Key");
    }

    /// <summary>
    /// A route the filter is mounted on: a client error renders the envelope (not the
    /// framework's body-less 400, not ProblemDetails) and a good request still answers
    /// the route's DTO — the filter unwraps the Result.
    /// </summary>
    [Fact]
    public async Task Route_Renders_Through_The_Result_Filter()
    {
        await using var factory = new EvidgateApiFactory(DeadConnectionString);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Request-Id", "req-filter-route");

        using var missing = await client.PostAsJsonAsync(
            Evidgate.Testing.TransportProbeStartupFilter.Path,
            new { },
            TestContext.Current.CancellationToken);

        missing.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        missing.Content.Headers.ContentType?.MediaType.ShouldBe("application/json");
        var error = (await ReadJsonAsync(missing)).GetProperty("error");
        error.GetProperty("code").GetString().ShouldBe("request_invalid");
        error.GetProperty("request_id").GetString().ShouldBe("req-filter-route");
        error.GetProperty("details")[0].GetProperty("path").GetString().ShouldBe("project_ref");

        using var ok = await client.PostAsJsonAsync(
            Evidgate.Testing.TransportProbeStartupFilter.Path,
            new { project_ref = "probe/test" },
            TestContext.Current.CancellationToken);

        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await ReadJsonAsync(ok);
        body.GetProperty("accepted").GetBoolean().ShouldBeTrue();
        body.TryGetProperty("error", out _).ShouldBeFalse();
    }

    private static DefaultHttpContext Context(string requestId)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.ConfigureHttpJsonOptions(
            json => json.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);

        return new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            TraceIdentifier = requestId,
            Response = { Body = new MemoryStream() },
        };
    }

    private static async Task<string> ReadAsync(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        return await new StreamReader(context.Response.Body).ReadToEndAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;

    /// <summary>The success body of the direct mapping case (snake_case on the wire).</summary>
    private sealed record FilterBody(string RunId);
}
