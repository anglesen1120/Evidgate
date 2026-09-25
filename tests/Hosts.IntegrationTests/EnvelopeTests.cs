using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Evidgate.Api.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hosts.IntegrationTests;

/// <summary>
/// AC7 (T3): every bad-request path returns the 00 §9.2 error envelope
/// (<c>code</c>, <c>message</c>, <c>request_id</c>, <c>retryable</c>, <c>details</c>)
/// — never ASP.NET's ProblemDetails — for a malformed body, an unsupported
/// content type, a missing field, an unmatched route and a wrong method, and the
/// <c>X-Request-Id</c> header survives all of them (D6/D7). The host runs in
/// Production, so these exercise the pipeline that ships.
/// </summary>
public sealed class EnvelopeTests
{
    private const string DeadConnectionString =
        "Host=127.0.0.1;Port=1;Username=x;Password=y;Database=evidgate;Timeout=2";

    [Fact]
    public async Task Bad_Request_Returns_9_2_Envelope()
    {
        await using var factory = new EvidgateApiFactory(DeadConnectionString);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Request-Id", "req-envelope-1");

        // Missing required field → the endpoint's 422 envelope, with a detail.
        using var emptyResponse = await client.PostAsJsonAsync("/v1/slice/run", new { }, TestContext.Current.CancellationToken);
        var error = (await AssertEnvelopeAsync(emptyResponse, 422)).GetProperty("error");
        Assert.Equal("request_invalid", error.GetProperty("code").GetString());
        Assert.False(error.GetProperty("retryable").GetBoolean());
        Assert.Equal("req-envelope-1", error.GetProperty("request_id").GetString());
        Assert.Equal("req-envelope-1", emptyResponse.Headers.GetValues("X-Request-Id").Single());
        var detail = Assert.Single(error.GetProperty("details").EnumerateArray());
        Assert.Equal("project_ref", detail.GetProperty("path").GetString());
        Assert.Equal("required", detail.GetProperty("reason").GetString());

        // Malformed JSON → 400 request_invalid.
        using var malformed = await client.PostAsync(
            "/v1/slice/run",
            new StringContent("{ not json", Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
        var malformedError = (await AssertEnvelopeAsync(malformed, 400)).GetProperty("error");
        Assert.Equal("request_invalid", malformedError.GetProperty("code").GetString());
        Assert.Equal("req-envelope-1", malformedError.GetProperty("request_id").GetString());
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/xml")]
    [InlineData(null)]
    public async Task Unreadable_Body_Is_A_Client_Error_Envelope(string? contentType)
    {
        // A body the reader cannot consume — an unsupported or missing content type
        // — is a client error, never a 500 (D6). 00 §9.2 names no 415, so it is 400.
        await using var factory = new EvidgateApiFactory(DeadConnectionString);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Request-Id", "req-content-type-1");

        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes("""{"project_ref":"x"}"""));
        if (contentType is not null)
        {
            content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        }

        using var response = await client.PostAsync("/v1/slice/run", content, TestContext.Current.CancellationToken);
        var error = (await AssertEnvelopeAsync(response, 400)).GetProperty("error");
        Assert.Equal("request_invalid", error.GetProperty("code").GetString());
        Assert.Equal("req-content-type-1", error.GetProperty("request_id").GetString());
    }

    [Fact]
    public async Task Unmatched_Route_And_Method_Carry_The_Envelope()
    {
        await using var factory = new EvidgateApiFactory(DeadConnectionString);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Request-Id", "req-fallback-1");

        using var notFound = await client.GetAsync("/v1/nope", TestContext.Current.CancellationToken);
        var notFoundError = (await AssertEnvelopeAsync(notFound, 404)).GetProperty("error");
        Assert.Equal("not_found", notFoundError.GetProperty("code").GetString());
        Assert.Equal("req-fallback-1", notFound.Headers.GetValues("X-Request-Id").Single());

        using var wrongMethod = await client.GetAsync("/v1/slice/run", TestContext.Current.CancellationToken);
        var wrongMethodError = (await AssertEnvelopeAsync(wrongMethod, 405)).GetProperty("error");
        Assert.Equal("method_not_allowed", wrongMethodError.GetProperty("code").GetString());
        Assert.Equal("req-fallback-1", wrongMethod.Headers.GetValues("X-Request-Id").Single());
    }

    [Fact]
    public async Task Middleware_Error_Keeps_Request_Id_Header()
    {
        // D7: the middleware must not clear the response — 00 §9.1 requires the
        // X-Request-Id header on every response, including middleware-written ones.
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.Items[RequestIdMiddleware.ItemsKey] = "req-middleware-1";
        context.Response.Headers[RequestIdMiddleware.HeaderName] = "req-middleware-1";

        var middleware = new EnvelopeExceptionMiddleware(
            _ => throw new InvalidOperationException("boom"),
            NullLogger<EnvelopeExceptionMiddleware>.Instance);

        await middleware.Invoke(context);

        Assert.Equal(500, context.Response.StatusCode);
        Assert.Equal("req-middleware-1", context.Response.Headers[RequestIdMiddleware.HeaderName].ToString());

        context.Response.Body.Position = 0;
        using var document = JsonDocument.Parse(
            await new StreamReader(context.Response.Body).ReadToEndAsync(TestContext.Current.CancellationToken));
        var error = document.RootElement.GetProperty("error");
        Assert.Equal("internal_error", error.GetProperty("code").GetString());
        Assert.Equal("req-middleware-1", error.GetProperty("request_id").GetString());
    }

    private static async Task<JsonElement> AssertEnvelopeAsync(HttpResponseMessage response, int expectedStatus)
    {
        Assert.Equal(expectedStatus, (int)response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(
            response.Headers.Contains("X-Request-Id"),
            "every response must carry the 00 §9.1 X-Request-Id header");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var error = document.RootElement.GetProperty("error");
        Assert.Equal(JsonValueKind.String, error.GetProperty("code").ValueKind);
        Assert.Equal(JsonValueKind.String, error.GetProperty("message").ValueKind);
        Assert.Equal(JsonValueKind.String, error.GetProperty("request_id").ValueKind);
        var retryableKind = error.GetProperty("retryable").ValueKind;
        Assert.True(
            retryableKind is JsonValueKind.True or JsonValueKind.False,
            $"retryable must be a JSON boolean, was {retryableKind}");
        Assert.Equal(JsonValueKind.Array, error.GetProperty("details").ValueKind);

        // Not ProblemDetails.
        Assert.False(document.RootElement.TryGetProperty("title", out _), "must not emit RFC 7807 'title'");
        Assert.False(document.RootElement.TryGetProperty("status", out _), "must not emit RFC 7807 'status'");
        Assert.False(document.RootElement.TryGetProperty("traceId", out _), "must not emit RFC 7807 'traceId'");

        return document.RootElement.Clone();
    }
}
