using System.Net;
using System.Security.Claims;
using Evidgate.Api.OperatorIngress;
using Evidgate.Modules.Policy.Webhooks.Application;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// Outbound webhook endpoint management, the delivery log and the operator replay through the real host and a real
/// database. DNS is a fake resolver so the SSRF guard's verdicts are deterministic; everything else (RLS, the guard
/// triggers, the audit buffer, the task queue) is real.
/// </summary>
[Collection(ExclusiveTenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class WebhookEndpointTests : IAsyncLifetime
{
    private const string GoodUrl = "https://hooks.example.com/evidgate";

    private readonly PostgresTenancyFixture _fixture;
    private DedicatedDatabase? _dedicated;

    public WebhookEndpointTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    // The class counts audit and delivery rows unfiltered, so each test owns a migrated database.
    public async ValueTask InitializeAsync() => _dedicated = await _fixture.CreateDedicatedDatabaseAsync();

    public async ValueTask DisposeAsync()
    {
        if (_dedicated is not null)
        {
            await _dedicated.DisposeAsync();
        }
    }

    [Fact]
    public async Task EndpointRegister_ShowsSecretOnce_NeverReadAgain()
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var host = api;

        var created = await RegisterAsync(client, user, org, project, GoodUrl);

        created.Status.ShouldBe(HttpStatusCode.Created);
        var secret = created.Text("secret")!;
        secret.Length.ShouldBeGreaterThan(16);
        created.Text("state").ShouldBe("active");
        created.Text("url").ShouldBe(GoodUrl);
        created.Headers.CacheControl!.NoStore.ShouldBeTrue("the answer carries the secret, so no cache may keep it");
        created.ETag.ShouldBe("\"1\"");

        var id = created.Id();
        var read = await client.SendAsync(HttpMethod.Get, $"/v1/projects/{project}/webhook-endpoints/{id}", Admin(user, org));
        var list = await client.SendAsync(HttpMethod.Get, $"/v1/projects/{project}/webhook-endpoints", Admin(user, org));

        read.Status.ShouldBe(HttpStatusCode.OK);
        read.Body.TryGetProperty("secret", out _).ShouldBeFalse();
        read.Text("secret_hint").ShouldNotBeNullOrEmpty();
        list.Body.GetRawText().ShouldNotContain(secret);
        read.Body.GetRawText().ShouldNotContain(secret);

        // The row holds ciphertext only, and neither it nor the audit row can be turned back into the plaintext.
        (await _fixture.ScalarAsync<long>(_dedicated!.Name, $"select count(*) from public.webhook_endpoint where position('{secret}' in encode(secret_ciphertext, 'escape')) > 0"))
            .ShouldBe(0);
        (await _fixture.ScalarAsync<long>(_dedicated.Name, "select count(*) from public.audit_log where action = 'webhook_endpoint.create'"))
            .ShouldBe(1);
        (await _fixture.ScalarAsync<long>(_dedicated.Name, $"select count(*) from public.audit_log where position('{secret}' in audit_log::text) > 0"))
            .ShouldBe(0, "an audit row names the endpoint and never the secret");
    }

    [Theory]
    [InlineData("http://hooks.example.com/evidgate", "scheme")]
    [InlineData("https://user:pass@hooks.example.com/evidgate", "userinfo")]
    [InlineData("https://hooks.example.com:8443/evidgate", "port")]
    [InlineData("https://private.example.com/evidgate", "address")]
    [InlineData("https://rebind.example.com/evidgate", "address")]
    public async Task EndpointRegister_BlockedTarget_422EndpointUrlBlocked_AndWritesNothing(string url, string reason)
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var host = api;

        var blocked = await RegisterAsync(client, user, org, project, url);

        blocked.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        blocked.ErrorCode.ShouldBe("endpoint_url_blocked");
        blocked.Body.GetProperty("error").GetProperty("details")[0].GetProperty("reason").GetString().ShouldBe(reason);
        (await _fixture.ScalarAsync<long>(_dedicated!.Name, "select count(*) from public.webhook_endpoint")).ShouldBe(0);

        // Positive control: the same host with an allowed shape registers.
        (await RegisterAsync(client, user, org, project, GoodUrl)).Status.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task EndpointUpdate_StaleIfMatch_412_AndCurrentIfMatchApplies()
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var host = api;
        var created = await RegisterAsync(client, user, org, project, GoodUrl);
        var path = $"/v1/projects/{project}/webhook-endpoints/{created.Id()}";

        var missing = await client.SendAsync(HttpMethod.Patch, path, Admin(user, org), new { state = "disabled" });
        var stale = await client.SendAsync(HttpMethod.Patch, path, Admin(user, org), new { state = "disabled" }, ifMatch: "\"99\"");
        var blocked = await client.SendAsync(HttpMethod.Patch, path, Admin(user, org), new { url = "http://hooks.example.com/x" }, ifMatch: created.ETag);
        var empty = await client.SendAsync(HttpMethod.Patch, path, Admin(user, org), new { }, ifMatch: created.ETag);
        var applied = await client.SendAsync(HttpMethod.Patch, path, Admin(user, org), new { state = "disabled" }, ifMatch: created.ETag);

        missing.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        stale.Status.ShouldBe(HttpStatusCode.PreconditionFailed);
        blocked.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        blocked.ErrorCode.ShouldBe("endpoint_url_blocked");
        empty.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        applied.Status.ShouldBe(HttpStatusCode.OK);
        applied.Text("state").ShouldBe("disabled");
        applied.ETag.ShouldNotBe(created.ETag);
        (await _fixture.ScalarAsync<long>(_dedicated!.Name, "select count(*) from public.audit_log where action = 'webhook_endpoint.update'"))
            .ShouldBe(1, "only the applied change is audited");

        var replayed = await client.SendAsync(HttpMethod.Patch, path, Admin(user, org), new { state = "active" }, ifMatch: created.ETag);
        replayed.Status.ShouldBe(HttpStatusCode.PreconditionFailed);
    }

    [Fact]
    public async Task EndpointRegister_AtTheCap_409_AndADuplicateUrlIs409()
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var host = api;

        for (var i = 0; i < 20; i++)
        {
            (await RegisterAsync(client, user, org, project, $"https://hooks.example.com/e{i}")).Status.ShouldBe(HttpStatusCode.Created);
        }

        var over = await RegisterAsync(client, user, org, project, "https://hooks.example.com/e20");
        var duplicate = await RegisterAsync(client, user, org, project, "https://hooks.example.com/e3");

        over.Status.ShouldBe(HttpStatusCode.Conflict);
        over.ErrorCode.ShouldBe("endpoint_limit_reached");
        duplicate.Status.ShouldBe(HttpStatusCode.Conflict);
        (await _fixture.ScalarAsync<long>(_dedicated!.Name, "select count(*) from public.webhook_endpoint")).ShouldBe(20);
    }

    [Fact]
    public async Task DeleteEndpoint_StopsDelivery_KeepsHistory()
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var host = api;
        var endpoint = (await RegisterAsync(client, user, org, project, GoodUrl)).Id();
        await SeedDeliveryAsync(org, project, endpoint, Guid.CreateVersion7(), "delivered", DateTimeOffset.UtcNow.AddMinutes(-5));
        var path = $"/v1/projects/{project}/webhook-endpoints/{endpoint}";

        var deleted = await client.SendAsync(HttpMethod.Delete, path, Admin(user, org));
        var repeat = await client.SendAsync(HttpMethod.Delete, path, Admin(user, org));
        var read = await client.SendAsync(HttpMethod.Get, path, Admin(user, org));
        var history = await client.SendAsync(HttpMethod.Get, $"/v1/projects/{project}/webhook-deliveries", Admin(user, org));

        deleted.Status.ShouldBe(HttpStatusCode.NoContent);
        repeat.Status.ShouldBe(HttpStatusCode.NotFound);
        read.Status.ShouldBe(HttpStatusCode.NotFound);
        (await _fixture.ScalarAsync<string>(_dedicated!.Name, $"select state from public.webhook_endpoint where id = '{endpoint}'")).ShouldBe("deleted");
        history.Body.GetProperty("items").GetArrayLength().ShouldBe(1, "the delivery history of a deleted endpoint stays readable");
        (await _fixture.ScalarAsync<long>(_dedicated.Name, "select count(*) from public.audit_log where action = 'webhook_endpoint.delete'"))
            .ShouldBe(1, "the repeat finds no live row and writes no second audit row");

        // The URL is free again.
        (await RegisterAsync(client, user, org, project, GoodUrl)).Status.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task ListWebhookDeliveries_Query_PagesNewestFirst_FiltersByState_AndRefusesBadInput()
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var host = api;
        var endpoint = (await RegisterAsync(client, user, org, project, GoodUrl)).Id();
        var now = DateTimeOffset.UtcNow;
        var oldest = Guid.CreateVersion7();
        var middle = Guid.CreateVersion7();
        var newest = Guid.CreateVersion7();
        await SeedDeliveryAsync(org, project, endpoint, oldest, "delivered", now.AddMinutes(-30));
        await SeedDeliveryAsync(org, project, endpoint, middle, "dead_letter", now.AddMinutes(-20));
        await SeedDeliveryAsync(org, project, endpoint, newest, "dead_letter", now.AddMinutes(-10));
        var path = $"/v1/projects/{project}/webhook-deliveries";

        var first = await client.SendAsync(HttpMethod.Get, $"{path}?limit=2", Admin(user, org));
        var cursor = first.Text("next_cursor");
        var second = await client.SendAsync(HttpMethod.Get, $"{path}?limit=2&cursor={Uri.EscapeDataString(cursor!)}", Admin(user, org));
        var filtered = await client.SendAsync(HttpMethod.Get, $"{path}?state=delivered", Admin(user, org));

        first.Status.ShouldBe(HttpStatusCode.OK);
        EventIds(first).ShouldBe([newest, middle]);
        cursor.ShouldNotBeNullOrEmpty();
        EventIds(second).ShouldBe([oldest]);
        second.Body.GetProperty("next_cursor").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
        EventIds(filtered).ShouldBe([oldest]);
        first.Body.GetProperty("items")[0].GetProperty("state").GetString().ShouldBe("dead_letter");

        (await client.SendAsync(HttpMethod.Get, $"{path}?state=bogus", Admin(user, org))).Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await client.SendAsync(HttpMethod.Get, $"{path}?cursor=not-a-cursor", Admin(user, org))).Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await client.SendAsync(HttpMethod.Get, $"{path}?limit=0", Admin(user, org))).Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await client.SendAsync(HttpMethod.Get, $"/v1/projects/{Guid.CreateVersion7()}/webhook-deliveries", Admin(user, org)))
            .Status.ShouldBeOneOf(HttpStatusCode.NotFound, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task DeliveryListSql_Shapes_UseTheirIndexes_WithoutSeqScanOrSort()
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var host = api;
        var endpoint = (await RegisterAsync(client, user, org, project, GoodUrl)).Id();
        await SeedDeliveryAsync(org, project, endpoint, Guid.CreateVersion7(), "dead_letter", DateTimeOffset.UtcNow.AddMinutes(-1));

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(_dedicated!.Name), org);
        await scope.SetAsync("enable_seqscan", "off");

        foreach (var (filtered, cursor, index) in new[]
        {
            (true, false, "webhook_outbound_delivery_project_state_idx"),
            (true, true, "webhook_outbound_delivery_project_state_idx"),
            (false, false, "webhook_outbound_delivery_project_idx"),
            (false, true, "webhook_outbound_delivery_project_idx"),
        })
        {
            var plan = await scope.ScalarAsync<string>(
                $"explain (format json) {WebhookEndpointSql.DeliveryPage(filtered, cursor)}",
                ("org_id", org),
                ("project_id", project),
                ("state", "dead_letter"),
                ("cursor_created_at", DateTimeOffset.UtcNow),
                ("cursor_id", Guid.CreateVersion7()),
                ("limit", 51));

            plan.ShouldContain(index);
            plan.ShouldNotContain("\"Node Type\": \"Seq Scan\"");
            plan.ShouldNotContain("\"Node Type\": \"Sort\"");
        }
    }

    [Fact]
    public async Task DeadLetterReplay_ByEventId_RequeuesAndEnqueuesATask_OnlyOnce()
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var host = api;
        var endpoint = (await RegisterAsync(client, user, org, project, GoodUrl)).Id();
        var deadEvent = Guid.CreateVersion7();
        var deliveredEvent = Guid.CreateVersion7();
        await SeedDeliveryAsync(org, project, endpoint, deadEvent, "dead_letter", DateTimeOffset.UtcNow.AddMinutes(-3));
        await SeedDeliveryAsync(org, project, endpoint, deliveredEvent, "delivered", DateTimeOffset.UtcNow.AddMinutes(-2));
        var tasksBefore = await _fixture.ScalarAsync<long>(_dedicated!.Name, "select count(*) from public.task where task_type = 'webhook.deliver'");

        var replayed = await client.SendAsync(HttpMethod.Post, ReplayUrl(deadEvent, org), _ => { });

        replayed.Status.ShouldBe(HttpStatusCode.OK);
        replayed.Body.GetProperty("delivery_ids").GetArrayLength().ShouldBe(1);
        (await _fixture.ScalarAsync<string>(_dedicated.Name, $"select state from public.webhook_outbound_delivery where event_id = '{deadEvent}'")).ShouldBe("queued");
        (await _fixture.ScalarAsync<long>(_dedicated.Name, "select count(*) from public.task where task_type = 'webhook.deliver'"))
            .ShouldBe(tasksBefore + 1, "the replayed row is never left without a delivery task");
        (await _fixture.ScalarAsync<long>(_dedicated.Name, "select count(*) from public.audit_log where action = 'webhook_delivery.replay'")).ShouldBe(1);

        // A second replay finds nothing dead-lettered; a delivered row and an unknown event are refused apart.
        (await client.SendAsync(HttpMethod.Post, ReplayUrl(deadEvent, org), _ => { })).ErrorCode.ShouldBe("delivery_not_dead_letter");
        (await client.SendAsync(HttpMethod.Post, ReplayUrl(deliveredEvent, org), _ => { })).Status.ShouldBe(HttpStatusCode.Conflict);
        (await client.SendAsync(HttpMethod.Post, ReplayUrl(Guid.CreateVersion7(), org), _ => { })).Status.ShouldBe(HttpStatusCode.NotFound);
        (await client.SendAsync(HttpMethod.Post, $"/internal/operator/webhook-deliveries/{deadEvent}/replay", _ => { })).Status.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task DeadLetterReplay_OfADisabledEndpoint_MovesNothing()
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var host = api;
        var created = await RegisterAsync(client, user, org, project, GoodUrl);
        var deadEvent = Guid.CreateVersion7();
        await SeedDeliveryAsync(org, project, created.Id(), deadEvent, "dead_letter", DateTimeOffset.UtcNow.AddMinutes(-3));
        await client.SendAsync(HttpMethod.Patch, $"/v1/projects/{project}/webhook-endpoints/{created.Id()}", Admin(user, org), new { state = "disabled" }, ifMatch: created.ETag);

        var answer = await client.SendAsync(HttpMethod.Post, ReplayUrl(deadEvent, org), _ => { });

        answer.Status.ShouldBe(HttpStatusCode.Conflict);
        (await _fixture.ScalarAsync<string>(_dedicated!.Name, $"select state from public.webhook_outbound_delivery where event_id = '{deadEvent}'")).ShouldBe("dead_letter");
    }

    [Fact]
    public async Task WebhookRoutes_CallerWithoutThePermission_403_WhileTheAdminIsServed()
    {
        var (api, client, user, org, project) = await StartAsync();
        await using var host = api;
        var endpoint = (await RegisterAsync(client, user, org, project, GoodUrl)).Id();
        var member = Guid.CreateVersion7();
        await _fixture.ExecuteAsync(
            _dedicated!.Name,
            $"""
            insert into public."user" (id, email) values ('{member}', 'member@example.test');
            insert into public.membership (id, org_id, user_id, invited_email, status) values (gen_random_uuid(), '{org}', '{member}', 'member@example.test', 'active')
            """);
        var routes = new (HttpMethod Method, string Path, object? Body, string? IfMatch)[]
        {
            (HttpMethod.Post, $"/v1/projects/{project}/webhook-endpoints", new { url = "https://hooks.example.com/other" }, null),
            (HttpMethod.Get, $"/v1/projects/{project}/webhook-endpoints", null, null),
            (HttpMethod.Get, $"/v1/projects/{project}/webhook-endpoints/{endpoint}", null, null),
            (HttpMethod.Patch, $"/v1/projects/{project}/webhook-endpoints/{endpoint}", new { state = "disabled" }, "\"1\""),
            (HttpMethod.Delete, $"/v1/projects/{project}/webhook-endpoints/{endpoint}", null, null),
            (HttpMethod.Get, $"/v1/projects/{project}/webhook-deliveries", null, null),
        };

        foreach (var (method, path, body, ifMatch) in routes)
        {
            var denied = await client.SendAsync(method, path, Admin(member, org), body, ifMatch: ifMatch);
            denied.Status.ShouldBe(HttpStatusCode.Forbidden, $"{method} {path}");
            denied.ErrorCode.ShouldBe("forbidden");
        }

        (await _fixture.ScalarAsync<long>(_dedicated.Name, "select count(*) from public.webhook_endpoint where state = 'active'"))
            .ShouldBe(1, "no denied write reached the table");
        (await client.SendAsync(HttpMethod.Get, routes[1].Path, Admin(user, org))).Status.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task WebhookRoutes_AnotherTenant_CannotSeeOrChangeTheEndpoint()
    {
        var (api, client, user, orgA, projectA) = await StartAsync();
        await using var host = api;
        var endpoint = (await RegisterAsync(client, user, orgA, projectA, GoodUrl)).Id();
        var other = Guid.CreateVersion7();
        var orgB = await CreateOrgAsync(client, other, Unique.Slug("beta"));
        var projectB = await CreateProjectAsync(client, other, orgB, Unique.Slug("billing"));
        var path = $"/v1/projects/{projectA}/webhook-endpoints/{endpoint}";

        var read = await client.SendAsync(HttpMethod.Get, path, Admin(other, orgB));
        var patch = await client.SendAsync(HttpMethod.Patch, path, Admin(other, orgB), new { state = "disabled" }, ifMatch: "\"1\"");
        var delete = await client.SendAsync(HttpMethod.Delete, path, Admin(other, orgB));
        var ownProject = await client.SendAsync(HttpMethod.Get, $"/v1/projects/{projectB}/webhook-endpoints/{endpoint}", Admin(other, orgB));
        var list = await client.SendAsync(HttpMethod.Get, $"/v1/projects/{projectB}/webhook-endpoints", Admin(other, orgB));

        foreach (var answer in new[] { read, patch, delete, ownProject })
        {
            answer.Status.ShouldBeOneOf(HttpStatusCode.NotFound, HttpStatusCode.Forbidden);
        }

        list.Status.ShouldBe(HttpStatusCode.OK);
        list.Body.GetProperty("items").GetArrayLength().ShouldBe(0);
        (await client.SendAsync(HttpMethod.Get, path, Admin(user, orgA))).Status.ShouldBe(HttpStatusCode.OK);
        (await _fixture.ScalarAsync<string>(_dedicated!.Name, $"select state from public.webhook_endpoint where id = '{endpoint}'")).ShouldBe("active");
    }

    private static string ReplayUrl(Guid eventId, Guid org) => $"/internal/operator/webhook-deliveries/{eventId}/replay?org_id={org}";

    private static Guid[] EventIds(ApiAnswer page) =>
        [.. page.Body.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("event_id").GetGuid())];

    private static Action<System.Net.Http.Headers.HttpRequestHeaders> Admin(Guid user, Guid org) =>
        TenancyApiFactory.Resolved(user: user, orgHeader: org);

    private static Task<ApiAnswer> RegisterAsync(HttpClient client, Guid user, Guid org, Guid project, string url) =>
        client.SendAsync(HttpMethod.Post, $"/v1/projects/{project}/webhook-endpoints", Admin(user, org), new { url });

    private async Task<(TenancyApiFactory Api, HttpClient Client, Guid User, Guid Org, Guid Project)> StartAsync()
    {
        var api = TenancyApiFactory.RealResolver(
            _fixture.AppConnectionString(_dedicated!.Name),
            services: services =>
            {
                services.AddSingleton<IWebhookHostResolver>(new StaticResolver());
                services.AddSingleton<IOperatorGate>(new PermitGate());
            });
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, Unique.Slug("acme"));
        var project = await CreateProjectAsync(client, user, org, Unique.Slug("checkout"));

        return (api, client, user, org, project);
    }

    private Task SeedDeliveryAsync(Guid org, Guid project, Guid endpoint, Guid eventId, string state, DateTimeOffset createdAt) =>
        _fixture.ExecuteAsync(
            _dedicated!.Name,
            $"""
            insert into public.webhook_outbound_delivery
              (id, org_id, project_id, endpoint_id, event_type, event_id, state, attempts, created_at, updated_at)
            values
              (gen_random_uuid(), '{org}', '{project}', '{endpoint}', 'evaluation.created', '{eventId}', '{state}', 1,
               '{createdAt:O}', '{createdAt:O}')
            """);

    /// <summary>Public for every host except the two names a test points at private space.</summary>
    private sealed class StaticResolver : IWebhookHostResolver
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
            Task.FromResult(host switch
            {
                "private.example.com" => new[] { IPAddress.Parse("10.0.0.7") },
                "rebind.example.com" => new[] { IPAddress.Parse("93.184.216.34"), IPAddress.Parse("169.254.169.254") },
                _ => new[] { IPAddress.Parse("93.184.216.34") },
            });
    }

    /// <summary>Admits one platform operator with a user id, the identity the replay's audit row is attributed to.</summary>
    private sealed class PermitGate : IOperatorGate
    {
        public ValueTask<bool> IsOperatorAsync(HttpContext httpContext)
        {
            httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, Guid.Parse("00000000-0000-7000-8000-0000000000a1").ToString("D"))],
                authenticationType: "operator-test"));

            return ValueTask.FromResult(true);
        }
    }
}
