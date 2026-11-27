using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Secrets;
using Evidgate.Modules.Authoring.Trackers.Application;
using Evidgate.Modules.Authoring.Trackers.Infrastructure;
using Evidgate.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Shouldly;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

public sealed partial class TrackerCallbackEndpointTests
{
    private const string RedirectUri = "https://evidgate.test/v1/tracker-connections/linear/callback";

    private sealed record Tenant(Guid User, Guid Org, Guid Project)
    {
        /// <summary>A member's own request: the org is named by header.</summary>
        public Action<HttpRequestHeaders> Member => TenancyApiFactory.Resolved(user: User, orgHeader: Org);

        /// <summary>The browser returning from the tracker: a session and no org header.</summary>
        public Action<HttpRequestHeaders> Browser => TenancyApiFactory.Resolved(user: User);
    }

    /// <summary>
    /// The provider's token endpoint. It sits under the real allowlist handler, so only an allowlisted address reaches it,
    /// and it records the address only, never a body that carries the client secret.
    /// </summary>
    private sealed class ProviderStub : HttpMessageHandler
    {
        private readonly ConcurrentQueue<string> _requests = new();

        public string Access { get; } = $"canary-access-{Guid.NewGuid():N}";

        public string Refresh { get; } = $"canary-refresh-{Guid.NewGuid():N}";

        public IReadOnlyCollection<string> Requests => _requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _requests.Enqueue(request.RequestUri!.AbsoluteUri);
            var body = JsonSerializer.Serialize(new
            {
                access_token = Access,
                refresh_token = Refresh,
                scope = "read",
                expires_in = 3600,
                token_type = "Bearer",
            });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class World(
        TenancyApiFactory api,
        HttpClient client,
        string database,
        ProviderStub provider,
        CapturingLoggerProvider logs,
        string clientSecret) : IAsyncDisposable
    {
        public HttpClient Client => client;

        public string Database => database;

        public ProviderStub Provider => provider;

        public CapturingLoggerProvider Logs => logs;

        /// <summary>Every value that must never appear in a response, a log line or an audit row.</summary>
        public string[] Canaries => [provider.Access, provider.Refresh, clientSecret];

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await api.DisposeAsync();
        }
    }

    /// <summary>
    /// One real host (real tenant resolution and permission evaluation; a header stands in for the session) with the
    /// provider transport stubbed through the module's own named HTTP client. <paramref name="configured"/> false is a
    /// deployment with no OAuth client for the tracker.
    /// </summary>
    private async Task<World> StartAsync(bool configured = true)
    {
        var database = await _fixture.SharedDatabaseAsync();
        var provider = new ProviderStub();
        var logs = new CapturingLoggerProvider();
        var clientSecret = $"canary-client-secret-{Guid.NewGuid():N}";

        var api = TenancyApiFactory.RealResolver(
            _fixture.AppConnectionString(database),
            configured
                ? new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    [$"{LinearTrackerOptions.SectionName}:ClientId"] = "linear-client-id",
                    [$"{LinearTrackerOptions.SectionName}:RedirectUri"] = RedirectUri,
                    [SecretNames.ToConfigurationKey(SecretNames.TrackersLinearClientSecret)] = clientSecret,
                }
                : null,
            services =>
            {
                services.AddLogging(logging => logging.AddProvider(logs));
                services.Configure<HttpClientFactoryOptions>(
                    nameof(ITrackerProviderClient),
                    options => options.HttpMessageHandlerBuilderActions.Add(builder => builder.PrimaryHandler = provider));
            });

        return new World(api, api.CreateClient(), database, provider, logs, clientSecret);
    }

    private static async Task<Tenant> NewTenantAsync(World world, string stem)
    {
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(world.Client, user, Unique.Slug(stem));
        var project = await CreateProjectAsync(world.Client, user, org, Unique.Slug("checkout"));

        return new Tenant(user, org, project);
    }

    private static Task<ApiAnswer> AuthorizeAsync(World world, Tenant tenant) =>
        world.Client.SendAsync(
            HttpMethod.Post,
            $"/v1/projects/{tenant.Project}/tracker-connections/linear/authorize",
            tenant.Member,
            idempotencyKey: NewKey());

    /// <summary>Starts a consent through the real route and returns the <c>state</c> the provider would hand back.</summary>
    private static async Task<string> StartConsentAsync(World world, Tenant tenant)
    {
        var answer = await AuthorizeAsync(world, tenant);
        answer.Status.ShouldBe(HttpStatusCode.OK);

        return QueryHelpers.ParseQuery(new Uri(answer.Text("authorization_url")!).Query)["state"].ToString();
    }

    private static Task<ApiAnswer> CallbackAsync(World world, Tenant tenant, string state, string? providerError = null) =>
        world.Client.SendAsync(
            HttpMethod.Get,
            $"/v1/tracker-connections/linear/callback?state={Uri.EscapeDataString(state)}"
            + (providerError is null ? "&code=provider-code" : $"&error={providerError}"),
            tenant.Browser);

    private static Task<ApiAnswer> DisconnectAsync(World world, Tenant caller, Guid connection) =>
        world.Client.SendAsync(
            HttpMethod.Delete,
            $"/v1/projects/{caller.Project}/tracker-connections/{connection}",
            caller.Member,
            idempotencyKey: NewKey());

    private Task<long> CountAsync(World world, string table, Guid org, string condition = "true") =>
        _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.{table} where org_id = '{org:D}' and {condition}");

    /// <summary>Every audit string of the org: what a sink or an auditor could read back.</summary>
    private async Task<IReadOnlyList<string>> AuditTextAsync(World world, Guid org) =>
        await _fixture.ColumnAsync<string>(
            world.Database,
            $"""
            select concat_ws('|', actor_id, action, object_type, object_id, request_id, trace_id, metadata::text)
              from public.audit_log where org_id = '{org:D}'
            """);

    /// <summary>
    /// Asserts no canary is in the response, any captured log call (message, state, scopes, exception) or any audit row,
    /// after proving each of the three has content to search, so an empty capture cannot pass.
    /// </summary>
    private async Task AssertNoCanaryAsync(World world, Tenant tenant, string responseBody)
    {
        var logTexts = world.Logs.Entries.SelectMany(entry => entry.Texts).ToList();
        var audit = await AuditTextAsync(world, tenant.Org);

        responseBody.ShouldNotBeEmpty();
        logTexts.ShouldNotBeEmpty();
        audit.ShouldContain(row => row.Contains("tracker.connection.connect", StringComparison.Ordinal));

        foreach (var canary in world.Canaries)
        {
            responseBody.Contains(canary, StringComparison.Ordinal).ShouldBeFalse("a canary is in the response");
            logTexts.Any(text => text.Contains(canary, StringComparison.Ordinal)).ShouldBeFalse("a canary is in a log call");
            audit.Any(row => row.Contains(canary, StringComparison.Ordinal)).ShouldBeFalse("a canary is in an audit row");
        }
    }
}
