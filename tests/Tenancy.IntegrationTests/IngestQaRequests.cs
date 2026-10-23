// 04.1.1-T1 independent QA (S33): request helpers shared by the IngestQa* suites. The shapes mirror the
// implementer's IngestEventBatchTests helpers so a QA case reads as the request it sends; they are declared
// separately because the implementer's helpers are private to their class.
#pragma warning disable CA1861
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>Envelope/batch builders and one <c>POST /v1/runs/{r}/events</c> for the independent QA suite.</summary>
internal static class IngestQaRequests
{
    /// <summary>The envelope's <c>occurred_at</c> (informative only).</summary>
    internal const string OccurredAt = "2026-09-26T10:15:30Z";

    /// <summary>
    /// One envelope (00 §9.4). <paramref name="status"/> null omits the member; <paramref name="durationMs"/> and
    /// <paramref name="epoch"/> are boxed so a case can send a number past <c>long</c>/<c>int</c> as written.
    /// </summary>
    internal static Dictionary<string, object?> Event(
        Guid eventId,
        string executionKey,
        long sequence,
        string producerId = IngestTestWorld.ProducerId,
        long attemptNo = 1,
        string type = "test.finished",
        string? status = "passed",
        object? ext = null,
        object? durationMs = null,
        object? epoch = null,
        object? occurredAt = null,
        Guid[]? artifactIds = null)
    {
        var envelope = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["schema_version"] = 1,
            ["event_id"] = eventId,
            ["producer_id"] = producerId,
            ["sequence"] = sequence,
            ["job_id"] = null,
            ["epoch"] = epoch ?? 1L,
            ["execution_key"] = executionKey,
            ["attempt_no"] = attemptNo,
            ["type"] = type,
            ["occurred_at"] = occurredAt ?? OccurredAt,
            ["artifact_ids"] = artifactIds ?? Array.Empty<Guid>(),
            ["ext"] = ext ?? new { schema = "playwright/1" },
        };

        if (status is not null)
        {
            envelope["status"] = status;
        }

        if (durationMs is not null)
        {
            envelope["duration_ms"] = durationMs;
        }

        return envelope;
    }

    /// <summary>One batch envelope.</summary>
    internal static object Batch(params object[] events) => new { schema_version = 1, events };

    /// <summary>One event of the world's plan with its real execution key.</summary>
    internal static string KeyOf(IngestTestWorld world, int index) => world.Plan.Executions[index].ExecutionKey;

    /// <summary>The serialized JSON of a batch.</summary>
    internal static string Json(object body) => JsonSerializer.Serialize(body);

    /// <summary>The JSON body of a batch.</summary>
    internal static StringContent JsonBody(object body) =>
        new(Json(body), Encoding.UTF8, "application/json");

    /// <summary>One raw <c>POST /v1/runs/{r}/events</c> with the given content.</summary>
    internal static async Task<ApiAnswer> PostAsync(
        HttpClient client,
        Guid runId,
        HttpContent content,
        string? requestId = null,
        Action<HttpRequestMessage>? mutate = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/runs/{runId:D}/events");
        if (requestId is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Request-Id", requestId);
        }

        request.Content = content;
        mutate?.Invoke(request);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(string.IsNullOrEmpty(text) ? "{}" : text);

        return new ApiAnswer(response.StatusCode, document.RootElement.Clone(), response.Headers);
    }

    /// <summary>One JSON <c>POST /v1/runs/{r}/events</c>.</summary>
    internal static Task<ApiAnswer> PostEventsAsync(
        HttpClient client,
        Guid runId,
        object body,
        string? requestId = null) =>
        PostAsync(client, runId, JsonBody(body), requestId);

    /// <summary>The per-event acks of a 200 body, in request order.</summary>
    internal static List<JsonElement> Acks(ApiAnswer answer) =>
        [.. answer.Body.GetProperty("acks").EnumerateArray()];

    /// <summary>The 00 §9.2 <c>error.details</c> rows of a failure body.</summary>
    internal static List<JsonElement> Details(ApiAnswer answer) =>
        [.. answer.Body.GetProperty("error").GetProperty("details").EnumerateArray()];

    /// <summary>The <c>index</c> of one detail row.</summary>
    internal static int IndexOf(JsonElement detail) => detail.GetProperty("index").GetInt32();

    /// <summary>A JSON body padded with trailing whitespace to exactly <paramref name="bytes"/> UTF-8 bytes.</summary>
    internal static string PaddedBody(object body, int bytes)
    {
        var json = JsonSerializer.Serialize(body);
        var length = Encoding.UTF8.GetByteCount(json);

        return length <= bytes ? json + new string(' ', bytes - length) : json;
    }
}

/// <summary>
/// A JSON body whose length the client does not declare, so the request goes out chunked and the server sees
/// <c>Content-Length: null</c> — the "no Content-Length" half of the 1 MiB boundary (AC7).
/// </summary>
internal sealed class ChunkedJsonContent(byte[] body) : HttpContent
{
    /// <inheritdoc />
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        stream.WriteAsync(body, TestContext.Current.CancellationToken).AsTask();

    /// <inheritdoc />
    protected override bool TryComputeLength(out long length)
    {
        // Returning false makes HttpClient use Transfer-Encoding: chunked and send no Content-Length.
        length = 0;
        return false;
    }
}
