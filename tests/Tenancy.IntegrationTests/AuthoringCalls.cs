using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The HTTP edge the node tests of 13.1.1 §5 routes 1-5 share: one request builder that spells out the headers the
/// authoring contract requires (<c>Idempotency-Key</c> on every write, <c>If-Match</c> on a mutation) so no test can
/// silently omit one, plus the three node calls the tree tests repeat.
/// </summary>
internal static class AuthoringCalls
{
    /// <summary>The answer of one authoring request: status, parsed body and the response ETag.</summary>
    /// <param name="Status">The status line.</param>
    /// <param name="Body">The parsed body, or null for 204.</param>
    /// <param name="ETag">The response <c>ETag</c> header, or null.</param>
    internal sealed record Answer(HttpStatusCode Status, JsonElement? Body, string? ETag)
    {
        /// <summary>The body member <paramref name="name"/> of an object answer.</summary>
        /// <param name="name">The member name.</param>
        public JsonElement Member(string name) => Body!.Value.GetProperty(name);

        /// <summary>The <c>details[].reason</c> values of an error envelope.</summary>
        public IReadOnlyList<string> Reasons() =>
            [.. Body!.Value.GetProperty("error").GetProperty("details").EnumerateArray().Select(detail => detail.GetProperty("reason").GetString()!)];

        /// <summary>
        /// The <c>details[].path</c> values of an error envelope: the member an answer blames. A route whose member is a
        /// body field must name it as the client sent it (<c>/position</c>), never the writer's batch envelope
        /// (<c>/changes/0/content/position</c>).
        /// </summary>
        public IReadOnlyList<string?> Paths() =>
            [.. Body!.Value.GetProperty("error").GetProperty("details").EnumerateArray().Select(detail => detail.GetProperty("path").GetString())];
    }

    /// <summary>One request, authorized by the caller's header action.</summary>
    /// <param name="client">The API client.</param>
    /// <param name="authorize">The principal and tenant headers.</param>
    /// <param name="method">The verb.</param>
    /// <param name="url">The route.</param>
    /// <param name="json">A JSON body, or null.</param>
    /// <param name="ifMatch">The <c>If-Match</c> tag, or null.</param>
    /// <param name="idempotencyKey">The key to reuse, or null for a fresh one.</param>
    /// <param name="withIdempotencyKey">False to send no key at all (the AC18 negative case).</param>
    internal static async Task<Answer> SendAsync(
        HttpClient client,
        Action<HttpRequestHeaders> authorize,
        HttpMethod method,
        string url,
        string? json = null,
        string? ifMatch = null,
        string? idempotencyKey = null,
        bool withIdempotencyKey = true)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(authorize);

        using var request = new HttpRequestMessage(method, url);
        authorize(request.Headers);

        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        if (withIdempotencyKey)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey ?? "key-" + Guid.CreateVersion7().ToString("N"));
        }

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        JsonElement? body = null;

        if (!string.IsNullOrWhiteSpace(text))
        {
            using var document = JsonDocument.Parse(text);
            body = document.RootElement.Clone();
        }

        return new Answer(response.StatusCode, body, response.Headers.ETag?.ToString());
    }

    /// <summary><c>POST /nodes</c> (route 2).</summary>
    /// <param name="client">The API client.</param>
    /// <param name="authorize">The caller's headers.</param>
    /// <param name="project">The project.</param>
    /// <param name="parentId">The parent, or null.</param>
    /// <param name="kind">The kind.</param>
    /// <param name="title">The title.</param>
    /// <param name="position">An explicit position, or null to append.</param>
    internal static Task<Answer> CreateNodeAsync(
        HttpClient client,
        Action<HttpRequestHeaders> authorize,
        Guid project,
        Guid? parentId,
        string kind,
        string title,
        int? position = null) =>
        SendAsync(
            client,
            authorize,
            HttpMethod.Post,
            $"/v1/projects/{project}/authoring/nodes",
            JsonSerializer.Serialize(new { parent_id = parentId, kind, title, position }));

    /// <summary><c>GET /tree</c> (route 1).</summary>
    /// <param name="client">The API client.</param>
    /// <param name="authorize">The caller's headers.</param>
    /// <param name="project">The project.</param>
    /// <param name="parentId">The group to list, or null for the roots.</param>
    /// <param name="nodeId">The node to address, or null.</param>
    /// <param name="include">The <c>include</c> value, or null.</param>
    /// <param name="cursor">The cursor, or null.</param>
    internal static Task<Answer> TreeAsync(
        HttpClient client,
        Action<HttpRequestHeaders> authorize,
        Guid project,
        Guid? parentId = null,
        Guid? nodeId = null,
        string? include = null,
        string? cursor = null)
    {
        var query = new List<string>();
        if (parentId is { } parent)
        {
            query.Add("parent_id=" + parent.ToString("D"));
        }

        if (nodeId is { } node)
        {
            query.Add("node_id=" + node.ToString("D"));
        }

        if (include is not null)
        {
            query.Add("include=" + include);
        }

        if (cursor is not null)
        {
            query.Add("cursor=" + cursor);
        }

        var suffix = query.Count == 0 ? string.Empty : "?" + string.Join("&", query);

        return SendAsync(client, authorize, HttpMethod.Get, $"/v1/projects/{project}/authoring/tree{suffix}", withIdempotencyKey: false);
    }
}
