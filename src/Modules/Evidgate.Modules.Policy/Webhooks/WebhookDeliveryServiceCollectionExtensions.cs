using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.Modules.Policy.Webhooks.Application;
using Evidgate.Modules.Policy.Webhooks.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Policy.Webhooks;

/// <summary>Registers the outbound-webhook event fan-out, the delivery handler and the pinned HTTP client.</summary>
internal static class WebhookDeliveryServiceCollectionExtensions
{
    /// <summary>
    /// Adds the <c>evaluation.created</c> fan-out and the <c>webhook.deliver</c> handlers with the <c>webhook</c> client:
    /// no resilience pipeline (the retry schedule is the delivery's own), no redirects, and a connect callback that dials
    /// only the addresses the guard validated for the request.
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddWebhookDelivery(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddWebhookCore();
        services.AddOptions<WebhookDeliveryOptions>()
            .BindConfiguration(WebhookDeliveryOptions.Section)
            .Validate(
                static o => o.IsValid,
                $"{WebhookDeliveryOptions.Section}: ConnectTimeout must be positive and not above TotalTimeout")
            .ValidateOnStart();
        services.TryAddSingleton<IWebhookConnector, PinnedWebhookConnector>();
        services.TryAddSingleton<WebhookHttpSender>();

        services.AddHttpClient(WebhookHttpSender.ClientName, static client => client.Timeout = Timeout.InfiniteTimeSpan)
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(static provider => CreateHandler(provider));

        services.AddTaskHandler<WebhookFanoutPayload, WebhookFanoutHandler>();
        services.AddTransactionlessTaskHandler<WebhookDeliveryPayload, WebhookDeliveryHandler>();

        return services;
    }

    private static SocketsHttpHandler CreateHandler(IServiceProvider provider)
    {
        var connector = provider.GetRequiredService<IWebhookConnector>();
        var options = provider.GetRequiredService<IOptions<WebhookDeliveryOptions>>().Value;

        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            // No diagnostics handler: no client span carries the tenant URL (url.full) and no trace headers leave the platform.
            ActivityHeadersPropagator = null,
            MaxResponseDrainSize = 0,
            UseProxy = false,
            UseCookies = false,
            ConnectTimeout = options.ConnectTimeout,
            ConnectCallback = async (context, cancellationToken) =>
            {
                // The request names the addresses the guard accepted; the handler never resolves the host itself.
                if (context.InitialRequestMessage is null
                    || !context.InitialRequestMessage.Options.TryGetValue(WebhookHttpSender.TargetKey, out var target))
                {
                    throw new InvalidOperationException("a webhook request carries no validated target");
                }

                return await connector.ConnectAsync(target.Addresses, context.DnsEndPoint.Port, cancellationToken)
                    .ConfigureAwait(false);
            },
        };
    }
}
