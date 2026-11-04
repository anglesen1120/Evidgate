using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Shouldly;
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

    /// <summary>The 00 §9.2 <c>error.message</c>, or null on success.</summary>
    public string? ErrorMessage =>
        Body.TryGetProperty("error", out var error) ? error.GetProperty("message").GetString() : null;

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

    /// <summary>
    /// Sends one request with a caller and a body spelled as JSON text — the form a member name the reader cannot
    /// decode, or a repeated member, needs and a serialised object cannot produce.
    /// </summary>
    internal static async Task<ApiAnswer> SendRawAsync(
        this HttpClient client,
        HttpMethod method,
        string url,
        Action<HttpRequestHeaders> caller,
        string json)
    {
        using var request = new HttpRequestMessage(method, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        caller(request.Headers);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(string.IsNullOrEmpty(text) ? "{}" : text);

        return new ApiAnswer(response.StatusCode, document.RootElement.Clone(), response.Headers);
    }

    /// <summary>Creates an org as <paramref name="user"/> (the founder) through the real route; returns its id.</summary>
    internal static async Task<Guid> CreateOrgAsync(HttpClient client, Guid user, string slug)
    {
        var created = await client.SendAsync(
            HttpMethod.Post, "/v1/orgs", TenancyApiFactory.Resolved(user: user, email: $"{slug}@example.test"), new { name = slug, slug }, NewKey());
        created.Status.ShouldBe(HttpStatusCode.Created);

        return created.Id();
    }

    /// <summary>Creates a project in <paramref name="org"/> as <paramref name="user"/>; returns its id.</summary>
    internal static async Task<Guid> CreateProjectAsync(HttpClient client, Guid user, Guid org, string slug)
    {
        var created = await client.SendAsync(
            HttpMethod.Post, $"/v1/orgs/{org}/projects", TenancyApiFactory.Resolved(user: user), new { name = slug, slug }, NewKey());
        created.Status.ShouldBe(HttpStatusCode.Created);

        return created.Id();
    }

    /// <summary>A fresh <c>Idempotency-Key</c>.</summary>
    internal static string NewKey() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// An active member of <paramref name="org"/> holding one built-in role (or none), seeded with the migration
    /// role and granted by <paramref name="granter"/> through the membership route; returns the member's user id.
    /// </summary>
    internal static async Task<Guid> AddMemberAsync(
        PostgresTenancyFixture fixture,
        HttpClient client,
        string database,
        Guid org,
        Action<HttpRequestHeaders> granter,
        string? roleCode)
    {
        var user = Guid.CreateVersion7();
        var membership = Guid.CreateVersion7();

        await fixture.ExecuteAsync(
            database,
            $"""
            insert into public."user" (id, email) values ('{user}', 'member-{user:N}@example.test');
            insert into public.membership (id, org_id, user_id, invited_email, status)
            values ('{membership}', '{org}', '{user}', 'member-{user:N}@example.test', 'active')
            """);

        if (roleCode is not null)
        {
            var role = await fixture.ScalarAsync<Guid>(
                database, $"select id from public.role where code = '{roleCode}' and org_id is null");
            var granted = await client.SendAsync(
                HttpMethod.Post,
                $"/v1/orgs/{org}/memberships/{membership}/roles",
                granter,
                new { role_id = role },
                NewKey());

            granted.Status.ShouldBe(HttpStatusCode.OK, granted.ErrorMessage);
        }

        return user;
    }
}
