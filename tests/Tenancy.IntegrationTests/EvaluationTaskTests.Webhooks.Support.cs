using System.Globalization;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.Modules.Policy.Application.Evaluation;
using Evidgate.Modules.Policy.Webhooks.Application;
using Evidgate.Modules.Policy.Webhooks.Infrastructure;
using Evidgate.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

// Harness of the outbound-webhook delivery cases: a real HTTPS receiver, a resolver the test flips, a connector that
// routes the validated "public" address to the receiver, and a clock the retry schedule can be walked with. The handlers
// run against the real database and the real Api composition; only the network edge is the test's.
public sealed partial class EvaluationTaskTests
{
    private static readonly System.Text.CompositeFormat EndpointRowSql = System.Text.CompositeFormat.Parse(
        """
        insert into public.webhook_endpoint
            (id, org_id, project_id, url, secret_ciphertext, secret_hint, event_types, created_by)
        values ('{0}', '{1}', '{2}', '{3}', decode('{4}', 'hex'), '{5}', array['evaluation.created'], '{6}')
        """);

    private sealed class WebhookWorld(
        RunCreationTests.World world,
        WebhookTestReceiver receiver,
        FakeWebhookHostResolver resolver,
        RoutingWebhookConnector connector,
        FakeTimeProvider clock,
        CapturingLoggerProvider logs) : IAsyncDisposable
    {
        public RunCreationTests.World World { get; } = world;

        public WebhookTestReceiver Receiver { get; } = receiver;

        public FakeWebhookHostResolver Resolver { get; } = resolver;

        public RoutingWebhookConnector Connector { get; } = connector;

        public FakeTimeProvider Clock { get; } = clock;

        public CapturingLoggerProvider Logs { get; } = logs;

        public async ValueTask DisposeAsync()
        {
            await World.DisposeAsync();
            await Receiver.DisposeAsync();
        }
    }

    private sealed record DeliveryRow(
        string State,
        int Attempts,
        string? LastError,
        int? LastStatusCode,
        DateTimeOffset NextAttemptAt,
        DateTimeOffset CreatedAt);

    private async Task<WebhookWorld> StartWebhookWorldAsync(IReadOnlyDictionary<string, string?>? configuration = null)
    {
        var receiver = await WebhookTestReceiver.StartAsync();
        var resolver = new FakeWebhookHostResolver();
        resolver.Set(WebhookTestReceiver.Host, FakeWebhookHostResolver.PublicAddress);
        resolver.Set(WebhookTestReceiver.OtherHost, FakeWebhookHostResolver.PublicAddress);
        var connector = new RoutingWebhookConnector(receiver.Port);
        // Whole seconds: the signed timestamp and the stored instants then agree without rounding.
        var clock = new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        var logs = new CapturingLoggerProvider();
        var thumbprint = receiver.CertificateThumbprint;

        var world = await RunCreationTests.StartAsync(
            _fixture,
            hosts: _hosts,
            configuration: configuration,
            services: services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
                services.RemoveAll<IWebhookHostResolver>();
                services.AddSingleton<IWebhookHostResolver>(resolver);
                services.RemoveAll<IWebhookConnector>();
                services.AddSingleton<IWebhookConnector>(connector);
                services.TryAddSingleton<ITaskQueueWriter, TaskQueueWriter>();
                services.AddLogging(logging => logging.AddProvider(logs));

                // Trust the receiver's self-signed certificate on the real handler, leaving its connect callback,
                // redirect setting and timeouts exactly as production builds them.
                services.Configure<HttpClientFactoryOptions>(
                    WebhookHttpSender.ClientName,
                    options => options.HttpMessageHandlerBuilderActions.Add(builder =>
                    {
                        var handler = (SocketsHttpHandler)builder.PrimaryHandler;
                        handler.SslOptions.RemoteCertificateValidationCallback =
                            (_, certificate, _, _) => certificate?.GetCertHashString() == thumbprint;
                    }));
            });

        return new WebhookWorld(world, receiver, resolver, connector, clock, logs);
    }

    /// <summary>A stored live evaluation: the event a delivery announces.</summary>
    private async Task<Guid> SeedEvaluationAsync(WebhookWorld web)
    {
        var seed = await SeedAsync(web.World);
        var record = await EvaluateAsync(web.World, seed, EvaluationMode.Live, TestContext.Current.CancellationToken);

        return record.Id;
    }

    /// <summary>One endpoint row, written as the owner (the API that creates endpoints is not under test here).</summary>
    private async Task<(Guid Id, string Secret)> SeedEndpointAsync(
        WebhookWorld web,
        string path = "/ok",
        string host = WebhookTestReceiver.Host,
        string state = "active",
        bool unreadableSecret = false)
    {
        var secret = web.World.Api.Services.GetRequiredService<WebhookSecretProtector>().Generate();
        var cipher = unreadableSecret ? new byte[32] : secret.Ciphertext;
        var member = await _fixture.ScalarAsync<Guid>(
            web.World.Database,
            $"select id from public.membership where org_id = '{web.World.Org:D}' limit 1");
        var id = Guid.CreateVersion7();
        await _fixture.ExecuteAsync(
            web.World.Database,
            string.Format(
                CultureInfo.InvariantCulture,
                EndpointRowSql,
                id,
                web.World.Org,
                web.World.Project,
                $"https://{host}{path}?endpoint={id:N}",
                Convert.ToHexString(cipher),
                secret.Hint,
                member));
        if (state != "active")
        {
            await SetEndpointStateAsync(web, id, state);
        }

        return (id, secret.Plaintext);
    }

    private Task SetEndpointStateAsync(WebhookWorld web, Guid endpoint, string state) =>
        _fixture.ExecuteAsync(
            web.World.Database,
            $"""
            update public.webhook_endpoint
               set state = '{state}', deleted_at = {(state == "deleted" ? "now()" : "null")}
             where id = '{endpoint:D}'
            """);

    /// <summary>One queued delivery row created at the fake clock's instant.</summary>
    private async Task<Guid> SeedDeliveryAsync(WebhookWorld web, Guid endpoint, Guid evaluation)
    {
        var id = Guid.CreateVersion7();
        var now = web.Clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
        await _fixture.ExecuteAsync(
            web.World.Database,
            $"""
            insert into public.webhook_outbound_delivery
                (id, org_id, project_id, endpoint_id, event_type, event_id, next_attempt_at, created_at)
            values ('{id:D}', '{web.World.Org:D}', '{web.World.Project:D}', '{endpoint:D}',
                    'evaluation.created', '{evaluation:D}', '{now}', '{now}')
            """);

        return id;
    }

    private static Task DeliverAsync(WebhookWorld web, Guid delivery) =>
        web.World.Api.Services.GetRequiredService<WebhookDeliveryHandler>()
            .HandleAsync(
                new WebhookDeliveryPayload(web.World.Org, delivery),
                web.World.Org,
                TestContext.Current.CancellationToken)
            .AsTask();

    /// <summary>Delivers once and returns the deferral the handler asked for, or null when it finished the row.</summary>
    private static async Task<TaskDeferredException?> DeliverCatchingDeferralAsync(WebhookWorld web, Guid delivery)
    {
        try
        {
            await DeliverAsync(web, delivery);

            return null;
        }
        catch (TaskDeferredException deferred)
        {
            return deferred;
        }
    }

    private async Task<DeliveryRow> ReadDeliveryAsync(WebhookWorld web, Guid delivery)
    {
        await using var connection = await OwnerAsync(web.World);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select state, attempts, last_error, last_status_code, next_attempt_at, created_at
              from public.webhook_outbound_delivery where id = @id
            """;
        command.Parameters.AddWithValue("id", delivery);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        (await reader.ReadAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();

        var ct = TestContext.Current.CancellationToken;
        return new DeliveryRow(
            reader.GetString(0),
            reader.GetInt32(1),
            await reader.IsDBNullAsync(2, ct) ? null : reader.GetString(2),
            await reader.IsDBNullAsync(3, ct) ? null : reader.GetInt32(3),
            await reader.GetFieldValueAsync<DateTimeOffset>(4, ct),
            await reader.GetFieldValueAsync<DateTimeOffset>(5, ct));
    }

    private Task<long> CountRowsAsync(WebhookWorld web, string table, string where = "true") =>
        _fixture.ScalarAsync<long>(
            web.World.Database,
            $"select count(*) from public.{table} where org_id = '{web.World.Org:D}' and {where}");
}
