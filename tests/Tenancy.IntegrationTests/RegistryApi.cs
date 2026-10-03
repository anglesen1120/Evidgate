using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>One HTTP answer of the registry routes, body parsed once.</summary>
/// <param name="Status">The status code.</param>
/// <param name="Body">The JSON body (an empty object when the body was empty).</param>
/// <param name="Headers">The response headers.</param>
internal sealed record ApiAnswer(HttpStatusCode Status, JsonElement Body, HttpResponseHeaders Headers)
{
    /// <summary>The 00 §9.2 <c>error.code</c>, or null on success.</summary>
    public string? ErrorCode =>
        Body.TryGetProperty("error", out var error) ? error.GetProperty("code").GetString() : null;

    /// <summary>The response <c>ETag</c> value, quotes included.</summary>
    public string? ETag => Headers.ETag?.Tag;

    /// <summary>A string property of the body.</summary>
    public string? Text(string name) => Body.GetProperty(name).GetString();

    /// <summary>A uuid property of the body.</summary>
    public Guid Id(string name = "id") => Body.GetProperty(name).GetGuid();
}

/// <summary>Request helpers of the registry endpoint suites (02.1.1-T2).</summary>
internal static class RegistryApi
{
    /// <summary>Sends one request with a caller, an optional body and the 00 §9.1 headers.</summary>
    internal static async Task<ApiAnswer> SendAsync(
        this HttpClient client,
        HttpMethod method,
        string url,
        Action<HttpRequestHeaders> caller,
        object? body = null,
        string? idempotencyKey = null,
        string? ifMatch = null)
    {
        using var request = new HttpRequestMessage(method, url);
        caller(request.Headers);

        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(string.IsNullOrEmpty(text) ? "{}" : text);

        return new ApiAnswer(response.StatusCode, document.RootElement.Clone(), response.Headers);
    }

    /// <summary>A fresh <c>Idempotency-Key</c>.</summary>
    internal static string NewKey() => Guid.NewGuid().ToString("N");
}
