using System.Collections.Concurrent;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Secrets;
using Evidgate.Modules.Healing;
using Evidgate.Modules.Healing.Application;
using Evidgate.Modules.Healing.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class HealingLlmGatewayTests
{
    // ------------------------------------------------------------------ the world

    private sealed record World(
        Guid Org,
        Guid Project,
        NpgsqlDataSource DataSource,
        ServiceProvider Provider) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Provider.DisposeAsync();
            await DataSource.DisposeAsync();
        }
    }

    private async Task<World> StartAsync(Action<IServiceCollection>? configure = null)
    {
        var database = await _fixture.SharedDatabaseAsync();
        var org = Guid.CreateVersion7();
        var project = Guid.CreateVersion7();

        await using (var seed = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database)))
        {
            var orgSlug = Unique.Slug("healing-llm");
            var projectSlug = Unique.Slug("checkout");
            await seed.ExecuteAsync(
                "insert into public.organization (id, name, slug) values (@id, @name, @slug)",
                ("id", org),
                ("name", orgSlug),
                ("slug", orgSlug));
            await seed.ExecuteAsync(
                "insert into public.project (id, org_id, name, slug) values (@id, @org_id, @name, @slug)",
                ("id", project),
                ("org_id", org),
                ("name", projectSlug),
                ("slug", projectSlug));
            await seed.CommitAsync();
        }

        var dataSource = NpgsqlDataSource.Create(_fixture.AppConnectionString(database));
        var services = new ServiceCollection();
        services.AddSingleton(dataSource);
        services.AddEvidgateTenancy();

        // The module's own composition: the Development adapter, the gateway and the typed HTTP client with its pipeline.
        services.AddHealingModule(
            HealingLlmTestConfiguration.Build(HealingProviderSelection.Mock),
            HealingLlmTestConfiguration.Host(Environments.Development));

        configure?.Invoke(services);

        return new World(org, project, dataSource, services.BuildServiceProvider());
    }

    /// <summary>
    /// Makes the module's own HTTP adapter the provider over <paramref name="handler"/>: no socket is opened, and every
    /// request the adapter sends is observable. The HTTP adapter is not the default (O4 is open), so these cases wire it
    /// on purpose.
    /// </summary>
    /// <param name="services">The collection the test composes, before its provider is built.</param>
    /// <param name="handler">The handler that stands in for the provider's endpoint.</param>
    private static void UseHttpProvider(IServiceCollection services, HttpMessageHandler handler)
    {
        services.Replace(ServiceDescriptor.Singleton<IHealingLlmProvider>(provider =>
            provider.GetRequiredService<HttpHealingLlmProvider>()));

        // The key handler resolves the configured secret per request; this world has no secret store, so one fixed value.
        services.TryAddSingleton<ISecretProvider>(new FixedSecret());
        services.PostConfigure<HealingProviderOptions>(options =>
        {
            options.ApiKeySecret = SecretNames.HealingLlmApiKey;

            // The egress handler pins requests to the configured base address, so it names the stand-in endpoint too.
            options.BaseAddress = "http://healing.invalid/";
        });

        services
            .AddHttpClient<HttpHealingLlmProvider>()
            .ConfigureHttpClient(client => client.BaseAddress = new Uri("http://healing.invalid/"))
            .ConfigurePrimaryHttpMessageHandler(() => handler);
    }

    /// <summary>
    /// Invokes the gateway the way a caller does: with the org id and no tenant scope of its own, on the app role. Every
    /// case here asks for a call the guards allow, so a refusal fails the case with its reason instead of surfacing later
    /// as a missing row.
    /// </summary>
    private static async Task<HealingLlmInvocation> InvokeAsync(World world, HealingLlmRequest request)
    {
        // The gateway opens its own short tenant transactions, so the caller holds no scope.
        await using var services = world.Provider.CreateAsyncScope();
        var gateway = services.ServiceProvider.GetRequiredService<HealingLlmGateway>();
        var result = await gateway.InvokeAsync(world.Org, request, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);

        return result.Value;
    }

    /// <summary>The rows this org recorded, in attempt order, read through the same tenant path.</summary>
    private static async Task<IReadOnlyList<HealingLlmCallRow>> ReadCallsAsync(World world)
    {
        await using var services = world.Provider.CreateAsyncScope();
        var scopes = services.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
        await using var scope = await scopes.BeginAsync(
            world.Org,
            world.Project,
            readOnlyTransaction: true,
            TestContext.Current.CancellationToken);

        var db = services.ServiceProvider.GetRequiredService<ITenantDb>();
        return await db.QueryAsync<HealingLlmCallRow>(
            """
            select id, org_id as "OrgId", project_id as "ProjectId", incident_id as "IncidentId",
                   candidate_id as "CandidateId", purpose, model, prompt_version as "PromptVersion",
                   tokens, cost, status, attempt
              from public.healing_llm_call
             order by attempt
            """,
            null,
            TestContext.Current.CancellationToken);
    }

    private static HealingLlmRequest AuthoringRequest(Guid project, string? responseSchema = null) =>
        new(project, HealingLlmPurposes.AuthoringGeneration, null, null, Model, PromptVersion, Payload, responseSchema);

    private static HealingLlmRequest TriageRequest(Guid project, Guid? incidentId) =>
        new(project, HealingLlmPurposes.TriageHypothesis, incidentId, null, Model, PromptVersion, Payload, null);

    /// <summary>A provider that answers every attempt the same way and counts the attempts.</summary>
    private sealed class StubProvider(Func<HealingLlmRequest, HealingLlmResult> answer) : IHealingLlmProvider
    {
        private int _calls;

        internal int Calls => Volatile.Read(ref _calls);

        public Task<HealingLlmResult> InvokeAsync(HealingLlmRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = Interlocked.Increment(ref _calls);
            return Task.FromResult(answer(request));
        }
    }

    /// <summary>
    /// A handler that never answers: it waits until the pipeline's time budget cancels it, and counts how many attempts
    /// reached it — which is how AC2's "no retry beyond the bounded policy" is measured.
    /// </summary>
    private sealed class HangingHandler : HttpMessageHandler
    {
        private int _calls;

        internal int Calls => Volatile.Read(ref _calls);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _ = Interlocked.Increment(ref _calls);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage();
        }
    }

    /// <summary>
    /// A provider that answers one queued result per attempt, then repeats the last one — which is how a call whose first
    /// attempt is refused and whose second is accepted is staged without any timing.
    /// </summary>
    private sealed class QueuedProvider(HealingLlmResult[] answers) : IHealingLlmProvider
    {
        private int _calls;

        internal int Calls => Volatile.Read(ref _calls);

        public Task<HealingLlmResult> InvokeAsync(HealingLlmRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = Interlocked.Increment(ref _calls) - 1;
            return Task.FromResult(answers[Math.Min(index, answers.Length - 1)]);
        }
    }

    /// <summary>
    /// A handler that answers with the response it is handed and records every request it was asked for — which is how
    /// "the redirect was not followed" and "a non-success status is not retried" are measured without a socket.
    /// </summary>
    private sealed class RespondingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private readonly ConcurrentQueue<Uri> _requests = new();

        private int _calls;

        /// <summary>How many requests reached the handler.</summary>
        internal int Calls => Volatile.Read(ref _calls);

        /// <summary>The URIs those requests were sent to, in arrival order.</summary>
        internal IReadOnlyCollection<Uri> Requests => _requests;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = Interlocked.Increment(ref _calls);
            if (request.RequestUri is { } uri)
            {
                _requests.Enqueue(uri);
            }

            return Task.FromResult(respond(request));
        }
    }

    private sealed class FixedSecret : ISecretProvider
    {
        public ValueTask<string?> GetSecretAsync(string name, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<string?>("test-key-not-a-secret");
    }
}
