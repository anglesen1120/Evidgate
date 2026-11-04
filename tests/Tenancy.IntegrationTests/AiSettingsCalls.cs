using System.Net;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Evidgate.BuildingBlocks.Canonicalization;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The HTTP edge and the row reads the project AI-settings suites share (13.2.1 §5): one request builder that spells out
/// the two preconditions every save carries (<c>If-Match</c>, <c>Idempotency-Key</c>), the eight-field document the
/// canonical hash is computed over, and the reads the version, audit and immutability assertions depend on.
/// </summary>
/// <remarks>
/// The suites talk to the routes only — no module type, no handler as a seam — so a diverging implementation fails a
/// test instead of being dodged by the caller. The stored document is read back as JSON built from the row's own
/// columns, which is what makes "the hash covers what the database holds" a real check rather than a re-assertion of the
/// response body.
/// </remarks>
internal static class AiSettingsCalls
{
    /// <summary>The <c>[AuditedAction]</c> of the save (13.2.1 §5, AC13).</summary>
    internal const string AuditAction = "authoring.ai_settings.update";

    /// <summary>The 13.2.1 §4 caps (the per-field minimum of the story's plan and the OI-13-222 defaults).</summary>
    internal const int DescriptionLimit = 1000;

    internal const int UrlLimit = 2048;

    internal const int WritingStyleLimit = 250;

    internal const int CriterionLimit = 250;

    internal const int CoverageLimit = 1500;

    internal const int ExclusionsLimit = 1500;

    internal const int StepDetailLimit = 200;

    /// <summary>One project of one org, its founder and the client the routes are called on.</summary>
    internal sealed record World(
        TenancyApiFactory Api,
        HttpClient Client,
        string Database,
        Guid User,
        Guid Org,
        Guid Project) : IAsyncDisposable
    {
        /// <summary>The founder's headers: the <c>tenant_admin</c> that may save.</summary>
        public Action<HttpRequestHeaders> As => CallerFor(User);

        /// <summary>The headers of one member of the org.</summary>
        public Action<HttpRequestHeaders> CallerFor(Guid user) => TenancyApiFactory.Resolved(user: user, orgHeader: Org);

        /// <summary><c>/v1/projects/{p}/ai-settings</c>: GET the head, PUT a save.</summary>
        public string Settings => $"/v1/projects/{Project}/ai-settings";

        /// <summary><c>/v1/projects/{p}/ai-settings/versions</c>.</summary>
        public string Versions => $"{Settings}/versions";

        /// <summary><c>/v1/projects/{p}/ai-settings/templates</c>.</summary>
        public string Templates => $"{Settings}/templates";

        /// <summary>The history detail of one version.</summary>
        /// <param name="versionNo">The version number.</param>
        public string VersionUrl(int versionNo) => $"{Versions}/{versionNo}";

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
        }
    }

    /// <summary>One stored version, read from the row's columns rather than from the response.</summary>
    /// <param name="Document">The eight-field document the row holds.</param>
    /// <param name="ContentHash">The row's stored <c>content_hash</c>.</param>
    /// <param name="AuthorMembershipId">The membership the row records as its author.</param>
    /// <param name="Reason">The stored reason, or null.</param>
    /// <param name="TemplateCode">The recorded template code, or null.</param>
    /// <param name="TemplateVersion">The recorded template version, or null.</param>
    internal sealed record StoredDocument(
        JsonObject Document,
        string ContentHash,
        Guid AuthorMembershipId,
        string? Reason,
        string? TemplateCode,
        int? TemplateVersion);

    /// <summary>A fresh org with one project, as its founder.</summary>
    /// <param name="fixture">The harness fixture (shared database, app connection string).</param>
    internal static async Task<World> StartAsync(PostgresTenancyFixture fixture)
    {
        var database = await fixture.SharedDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(fixture.AppConnectionString(database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await RegistryApi.CreateOrgAsync(client, user, Unique.Slug("acme"));
        var project = await RegistryApi.CreateProjectAsync(client, user, org, Unique.Slug("checkout"));

        return new World(api, client, database, user, org, project);
    }

    /// <summary>
    /// The canonical document of AC1 with every field at its empty value and the two languages sorted ascending — the
    /// shape the default template seeds, so a test starts from what a fresh project already has.
    /// </summary>
    internal static JsonObject Document() => new()
    {
        ["description"] = string.Empty,
        ["environment_url"] = null,
        ["languages"] = new JsonArray("en", "vi"),
        ["writing_style"] = string.Empty,
        ["coverage_strategy"] = string.Empty,
        ["priority_criteria"] = new JsonObject
        {
            ["critical"] = string.Empty,
            ["high"] = string.Empty,
            ["medium"] = string.Empty,
            ["low"] = string.Empty,
        },
        ["exclusion_rules"] = string.Empty,
        ["step_detail"] = string.Empty,
    };

    /// <summary>The <c>SaveAiSettingsRequest</c> body (13.2.1 §5).</summary>
    /// <param name="document">The eight-field document.</param>
    /// <param name="template">The provenance pair, or null for a save without one.</param>
    /// <param name="reason">The reason, or null.</param>
    internal static JsonObject Body(JsonObject document, JsonObject? template = null, string? reason = null) => new()
    {
        ["settings"] = document.DeepClone(),
        ["template"] = template?.DeepClone(),
        ["reason"] = reason,
    };

    /// <summary>
    /// Prose of exactly <paramref name="length"/> code points: spaces and non-hex letters, so neither the secret-shape
    /// rules nor the audit trail's credential pattern can match it and a refusal can only be the field's own cap.
    /// </summary>
    /// <param name="length">The exact code-point length.</param>
    internal static string ProseOfLength(int length) =>
        string.Concat(Enumerable.Repeat("note ", (length / 5) + 1))[..(length - 1)] + "n";

    /// <summary>A template provenance pair (<c>{code, version}</c>).</summary>
    /// <param name="code">The template code, e.g. <c>default</c>.</param>
    /// <param name="version">The template version.</param>
    internal static JsonObject Template(string code, int version) => new() { ["code"] = code, ["version"] = version };

    /// <summary>The canonical hash of an eight-field document (RFC 8785 over the sorted document, AC1/§4).</summary>
    /// <param name="document">The document.</param>
    internal static string HashOf(JsonNode document) => CanonicalHash.Compute(document.ToJsonString());

    /// <summary>One request with the caller's headers and the two preconditions, when the case needs them.</summary>
    /// <param name="client">The API client.</param>
    /// <param name="caller">The caller's headers.</param>
    /// <param name="method">The verb.</param>
    /// <param name="url">The route.</param>
    /// <param name="body">The JSON body, or null.</param>
    /// <param name="ifMatch">The <c>If-Match</c> value; null omits the header, empty sends it empty.</param>
    /// <param name="idempotencyKey">The key to reuse, or null for a fresh one.</param>
    /// <param name="withIdempotencyKey">False sends no key at all.</param>
    internal static async Task<ApiAnswer> SendAsync(
        HttpClient client,
        Action<HttpRequestHeaders> caller,
        HttpMethod method,
        string url,
        JsonNode? body = null,
        string? ifMatch = null,
        string? idempotencyKey = null,
        bool withIdempotencyKey = true)
    {
        using var request = new HttpRequestMessage(method, url);
        caller(request.Headers);

        if (withIdempotencyKey && method != HttpMethod.Get)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey ?? RegistryApi.NewKey());
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

    /// <summary><c>PUT /ai-settings</c>.</summary>
    /// <param name="client">The API client.</param>
    /// <param name="caller">The caller's headers.</param>
    /// <param name="world">The project.</param>
    /// <param name="body">The request body.</param>
    /// <param name="ifMatch">The <c>If-Match</c> value, or null to omit the header.</param>
    /// <param name="idempotencyKey">The key to reuse, or null for a fresh one.</param>
    /// <param name="withIdempotencyKey">False sends no key at all.</param>
    internal static Task<ApiAnswer> SaveAsync(
        HttpClient client,
        Action<HttpRequestHeaders> caller,
        World world,
        JsonObject body,
        string? ifMatch = null,
        string? idempotencyKey = null,
        bool withIdempotencyKey = true) =>
        SendAsync(client, caller, HttpMethod.Put, world.Settings, body, ifMatch, idempotencyKey, withIdempotencyKey);

    /// <summary><c>GET</c> one route with the caller's headers.</summary>
    /// <param name="client">The API client.</param>
    /// <param name="caller">The caller's headers.</param>
    /// <param name="url">The route.</param>
    internal static Task<ApiAnswer> GetAsync(HttpClient client, Action<HttpRequestHeaders> caller, string url) =>
        SendAsync(client, caller, HttpMethod.Get, url);

    /// <summary>The <c>details[]</c> of an error envelope as (path, reason) pairs.</summary>
    /// <param name="answer">The refused answer.</param>
    internal static IReadOnlyList<(string? Path, string Reason)> Details(ApiAnswer answer) =>
    [
        .. answer.Body.GetProperty("error").GetProperty("details").EnumerateArray()
            .Select(detail => (detail.GetProperty("path").GetString(), detail.GetProperty("reason").GetString()!)),
    ];

    /// <summary>Asserts a 422 whose details blame exactly <paramref name="path"/> with <paramref name="reason"/>.</summary>
    /// <param name="answer">The answer.</param>
    /// <param name="path">The JSON pointer the detail must carry.</param>
    /// <param name="reason">The reason code.</param>
    internal static void ShouldRefuse(ApiAnswer answer, string path, string reason)
    {
        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("request_invalid");

        var blamed = Details(answer).Where(detail => detail.Path == path).ToList();

        blamed.Count.ShouldBe(1, $"exactly one detail must blame '{path}', the answer blamed {string.Join(", ", Details(answer).Select(d => d.Path))}");
        blamed[0].Reason.ShouldBe(reason);
    }

    /// <summary>The response's normalised document.</summary>
    /// <param name="answer">A settings answer.</param>
    internal static JsonObject SettingsOf(ApiAnswer answer) =>
        JsonNode.Parse(answer.Body.GetProperty("settings").GetRawText())!.AsObject();

    /// <summary>The response's language list, in the order the answer carries it.</summary>
    /// <param name="answer">A settings answer.</param>
    internal static IReadOnlyList<string> LanguagesOf(ApiAnswer answer) =>
        [.. answer.Body.GetProperty("settings").GetProperty("languages").EnumerateArray().Select(language => language.GetString()!)];

    /// <summary>The org's stored settings rows, all projects.</summary>
    /// <param name="fixture">The fixture.</param>
    /// <param name="world">The project.</param>
    internal static Task<long> SettingsCountAsync(PostgresTenancyFixture fixture, World world) =>
        fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.project_ai_settings where org_id = '{world.Org}'");

    /// <summary>The org's audit rows for the save action only (role grants audit under other actions).</summary>
    /// <param name="fixture">The fixture.</param>
    /// <param name="world">The project.</param>
    internal static Task<long> AuditCountAsync(PostgresTenancyFixture fixture, World world) =>
        fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.audit_log where org_id = '{world.Org}' and action = '{AuditAction}'");

    /// <summary>The head version number, or 0 when the project has no saved version.</summary>
    /// <param name="fixture">The fixture.</param>
    /// <param name="world">The project.</param>
    internal static async Task<long> HeadVersionAsync(PostgresTenancyFixture fixture, World world) =>
        await fixture.ScalarAsync<long>(
            world.Database,
            $"select coalesce(max(version_no), 0)::bigint from public.project_ai_settings where org_id = '{world.Org}' and project_id = '{world.Project}'");

    /// <summary>The org's idempotency rows (a refused save must leave none).</summary>
    /// <param name="fixture">The fixture.</param>
    /// <param name="world">The project.</param>
    internal static Task<long> IdempotencyCountAsync(PostgresTenancyFixture fixture, World world) =>
        fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.idempotency_record where org_id = '{world.Org}'");

    /// <summary>Reads one stored version back from its columns.</summary>
    /// <param name="fixture">The fixture.</param>
    /// <param name="world">The project.</param>
    /// <param name="versionNo">The version number.</param>
    internal static async Task<StoredDocument> StoredAsync(PostgresTenancyFixture fixture, World world, int versionNo)
    {
        var where = $"where org_id = '{world.Org}' and project_id = '{world.Project}' and version_no = {versionNo}";

        var json = await fixture.ScalarAsync<string>(
            world.Database,
            $"""
            select json_build_object(
                     'description', description,
                     'environment_url', environment_url,
                     'languages', languages,
                     'writing_style', writing_style,
                     'coverage_strategy', coverage_strategy,
                     'priority_criteria', json_build_object(
                         'critical', priority_criterion_critical,
                         'high', priority_criterion_high,
                         'medium', priority_criterion_medium,
                         'low', priority_criterion_low),
                     'exclusion_rules', exclusion_rules,
                     'step_detail', step_detail)::text
            from public.project_ai_settings {where}
            """);

        var meta = await fixture.ScalarAsync<string>(
            world.Database,
            $"""
            select content_hash || '|' || author_membership_id || '|' || coalesce(reason, '') || '|'
                   || coalesce(template_code, '') || '|' || coalesce(template_version::text, '')
            from public.project_ai_settings {where}
            """);

        var parts = meta.Split('|');

        return new StoredDocument(
            JsonNode.Parse(json)!.AsObject(),
            parts[0],
            Guid.Parse(parts[1]),
            parts[2].Length == 0 ? null : parts[2],
            parts[3].Length == 0 ? null : parts[3],
            parts[4].Length == 0 ? null : int.Parse(parts[4], CultureInfo.InvariantCulture));
    }

    /// <summary>The membership a user holds in the world's org.</summary>
    /// <param name="fixture">The fixture.</param>
    /// <param name="world">The org.</param>
    /// <param name="user">The user.</param>
    internal static Task<Guid> MembershipAsync(PostgresTenancyFixture fixture, World world, Guid user) =>
        fixture.ScalarAsync<Guid>(
            world.Database,
            $"select id from public.membership where org_id = '{world.Org}' and user_id = '{user}'");

    /// <summary>An active member of the org holding one built-in role, granted by the founder through the API.</summary>
    /// <param name="fixture">The fixture.</param>
    /// <param name="world">The org.</param>
    /// <param name="roleCode">The built-in role code, or null for a member with no role.</param>
    internal static async Task<Guid> AddMemberAsync(PostgresTenancyFixture fixture, World world, string? roleCode)
    {
        var user = Guid.CreateVersion7();
        var membership = Guid.CreateVersion7();

        await fixture.ExecuteAsync(
            world.Database,
            $"""
            insert into public."user" (id, email) values ('{user}', 'member-{user:N}@example.test');
            insert into public.membership (id, org_id, user_id, invited_email, status)
            values ('{membership}', '{world.Org}', '{user}', 'member-{user:N}@example.test', 'active')
            """);

        if (roleCode is not null)
        {
            var role = await fixture.ScalarAsync<Guid>(
                world.Database, $"select id from public.role where code = '{roleCode}' and org_id is null");
            var granted = await RegistryApi.SendAsync(
                world.Client,
                HttpMethod.Post,
                $"/v1/orgs/{world.Org}/memberships/{membership}/roles",
                world.As,
                new { role_id = role },
                RegistryApi.NewKey());

            granted.Status.ShouldBe(HttpStatusCode.OK, granted.ErrorMessage);
        }

        return user;
    }
}
