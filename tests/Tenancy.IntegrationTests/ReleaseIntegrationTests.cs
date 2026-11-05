// POST /v1/projects/{p}/releases and GET /v1/releases/{id} through the real host and a real PostgreSQL.
// Request bodies are written as JSON text so each test reads as the request it sends; every row read is
// scoped to the world's own org unless the case is the isolation case itself.
#pragma warning disable CA1861
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The release create API and its read. A release is a named set of component digests bound to environment
/// revisions; the create path validates the closed body, stores it in one tenant transaction with its audit row,
/// and the read returns the same components plus the runs bound to them by digest match.
/// </summary>
[Trait("Category", "Docker")]
public sealed partial class ReleaseIntegrationTests
{
    private readonly PostgresTenancyFixture _fixture;

    public ReleaseIntegrationTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    /// <summary>A well-formed content address of the requested fill character.</summary>
    private static string Digest(char fill) => "sha256:" + new string(fill, 64);

    /// <summary>One <c>components[]</c> member of the create body.</summary>
    private sealed record Component(string component_key, string digest, Guid environment_revision_id);

    /// <summary>The create body: a name, the components and an optional state (the default is the server's draft).</summary>
    private static string Body(string name, IReadOnlyList<Component> components, string? state = null)
    {
        var members = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = name,
            ["components"] = components,
        };
        if (state is not null)
        {
            members["state"] = state;
        }

        return JsonSerializer.Serialize(members);
    }

    /// <summary>One named component under the world's own environment revision.</summary>
    private static Component[] OneComponent(RunCreationTests.World world, string key = "web", char fill = 'a') =>
        [new(key, Digest(fill), world.EnvironmentRevision)];

    /// <summary>One <c>POST /v1/projects/{p}/releases</c>, with the caller, key and raw body the case names.</summary>
    private static async Task<ApiAnswer> CreateAsync(
        RunCreationTests.World world,
        string body,
        Action<HttpRequestHeaders>? caller = null,
        Guid? project = null,
        HttpClient? client = null,
        string? idempotencyKey = null,
        bool omitIdempotencyKey = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/projects/{project ?? world.Project}/releases");
        (caller ?? world.Admin)(request.Headers);
        if (!omitIdempotencyKey)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey ?? NewKey());
        }

        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        return await AnswerAsync(client ?? world.Client, request).ConfigureAwait(false);
    }

    /// <summary>One <c>GET /v1/releases/{id}</c> as the caller.</summary>
    private static async Task<ApiAnswer> GetReleaseAsync(
        RunCreationTests.World world,
        Guid releaseId,
        Action<HttpRequestHeaders>? caller = null,
        HttpClient? client = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/releases/{releaseId:D}");
        (caller ?? world.Admin)(request.Headers);

        return await AnswerAsync(client ?? world.Client, request).ConfigureAwait(false);
    }

    /// <summary>One created release, asserted 201 and its single <c>release_id</c> member.</summary>
    private static async Task<Guid> CreateReleaseAsync(
        RunCreationTests.World world,
        string name,
        string? state,
        params Component[] components)
    {
        var answer = await CreateAsync(world, Body(name, components, state)).ConfigureAwait(false);
        answer.Status.ShouldBe(HttpStatusCode.Created, answer.ErrorMessage);
        answer.Body.EnumerateObject().Select(member => member.Name).ShouldBe(["release_id"]);

        return answer.Id("release_id");
    }

    /// <summary>Sends one prepared request and returns the parsed answer, disposing the request.</summary>
    private static async Task<ApiAnswer> AnswerAsync(HttpClient client, HttpRequestMessage request)
    {
        using (request)
        {
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(string.IsNullOrEmpty(text) ? "{}" : text);

            return new ApiAnswer(response.StatusCode, document.RootElement.Clone(), response.Headers);
        }
    }

    /// <summary>Asserts the semantic refusal: 422 <c>release_invalid</c> with at least one detail, at <paramref name="path"/> when named.</summary>
    private static void ShouldBeReleaseInvalid(ApiAnswer answer, string? path = null)
    {
        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("release_invalid");

        var details = answer.Body.GetProperty("error").GetProperty("details");
        details.GetArrayLength().ShouldBeGreaterThan(0, "release_invalid carries the failing components");

        if (path is not null)
        {
            details.EnumerateArray()
                .Select(detail => detail.GetProperty("path").GetString())
                .ShouldContain(path);
        }
    }

    /// <summary>Asserts the closed-body refusal: 400 <c>request_invalid</c>.</summary>
    private static void ShouldBeRequestInvalid(ApiAnswer answer)
    {
        answer.Status.ShouldBe(HttpStatusCode.BadRequest, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("request_invalid");
    }

    /// <summary>Asserts the shape refusal: 422 <c>request_invalid</c> (a declared member with the wrong JSON type, an unknown or repeated member).</summary>
    private static void ShouldBeShapeInvalid(ApiAnswer answer)
    {
        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("request_invalid");
    }

    /// <summary>A whole refusal envelope with its per-request id normalised, so two 404 bodies can be compared.</summary>
    private static string Envelope(ApiAnswer answer)
    {
        var root = JsonNode.Parse(answer.Body.GetRawText())?.AsObject()
            ?? throw new InvalidOperationException("the refusal body is not a JSON object");
        var error = root["error"]?.AsObject()
            ?? throw new InvalidOperationException("the refusal body carries no error object");
        error["request_id"] = "normalised";

        return root.ToJsonString();
    }

    /// <summary>The release rows of the world's org.</summary>
    private async Task<long> ReleaseCountAsync(RunCreationTests.World world) =>
        await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.release where org_id = '{world.Org:D}'").ConfigureAwait(false);

    /// <summary>The release rows of one org, read as the migration role.</summary>
    private async Task<long> ReleaseCountAsync(string database, Guid org) =>
        await _fixture.ScalarAsync<long>(
            database,
            $"select count(*) from public.release where org_id = '{org:D}'").ConfigureAwait(false);

    /// <summary>The component rows of one release.</summary>
    private async Task<long> ComponentCountAsync(RunCreationTests.World world, Guid releaseId) =>
        await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.release_component where org_id = '{world.Org:D}' and release_id = '{releaseId:D}'")
            .ConfigureAwait(false);

    /// <summary>The idempotency records the release create key claimed.</summary>
    private async Task<long> IdempotencyCountAsync(RunCreationTests.World world, string key) =>
        await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.idempotency_record where org_id = '{world.Org:D}' "
            + $"and operation = 'release:create' and key = '{key}'").ConfigureAwait(false);

    /// <summary>The audit rows the release itself wrote.</summary>
    private async Task<long> ReleaseAuditCountAsync(RunCreationTests.World world, Guid releaseId) =>
        await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.audit_log where org_id = '{world.Org:D}' "
            + $"and object_type = 'release' and object_id = '{releaseId:D}'").ConfigureAwait(false);

    /// <summary>Seeds one environment and its revision for an existing project of the world's org, as the owner.</summary>
    private async Task<Guid> SeedRevisionAsync(RunCreationTests.World world, Guid project)
    {
        var environment = Guid.CreateVersion7();
        var revision = Guid.CreateVersion7();
        await _fixture.ExecuteAsync(
            world.Database,
            $"""
            insert into public.environment (id, org_id, project_id, environment_key, name)
            values ('{environment:D}', '{world.Org:D}', '{project:D}', 'staging', 'Staging');
            insert into public.environment_revision
                (id, org_id, project_id, environment_id, revision_no, base_urls, dependency_versions, revision_hash, created_by)
            values ('{revision:D}', '{world.Org:D}', '{project:D}', '{environment:D}', 1, jsonb_build_object(), jsonb_build_object(),
                    'sha256:{new string('b', 64)}', 'probe');
            """).ConfigureAwait(false);

        return revision;
    }

    /// <summary>Seeds an org whose project and environment revision must never be reachable from another tenant.</summary>
    private async Task<(Guid Org, Guid Project, Guid EnvironmentRevision)> SeedForeignTenantAsync(string database)
    {
        var org = Guid.CreateVersion7();
        var project = Guid.CreateVersion7();
        var environment = Guid.CreateVersion7();
        var revision = Guid.CreateVersion7();

        await _fixture.ExecuteAsync(
            database,
            $"""
            insert into public.organization (id, name, slug) values ('{org:D}', 'Foreign', 'foreign-{org:N}');
            insert into public.project (id, org_id, name, slug) values ('{project:D}', '{org:D}', 'Foreign', 'foreign-{project:N}');
            insert into public.environment (id, org_id, project_id, environment_key, name)
            values ('{environment:D}', '{org:D}', '{project:D}', 'staging', 'Staging');
            insert into public.environment_revision
                (id, org_id, project_id, environment_id, revision_no, base_urls, dependency_versions, revision_hash, created_by)
            values ('{revision:D}', '{org:D}', '{project:D}', '{environment:D}', 1, jsonb_build_object(), jsonb_build_object(),
                    'sha256:{new string('a', 64)}', 'probe');
            """).ConfigureAwait(false);

        return (org, project, revision);
    }
}
