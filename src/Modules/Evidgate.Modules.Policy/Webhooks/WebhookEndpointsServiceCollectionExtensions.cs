using Evidgate.Modules.Policy.Webhooks.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Evidgate.Modules.Policy.Webhooks;

/// <summary>Registration of the webhook management and operator replay services.</summary>
internal static class WebhookEndpointsServiceCollectionExtensions
{
    /// <summary>
    /// Adds the operator replay handler. The endpoint and delivery-list handlers are Mediator handlers the generator
    /// registers with the module; the replay is a plain singleton like <c>ReplayDeadLetterTask</c>, because the operator
    /// surface has no tenant principal for the pipeline. Needs <see cref="WebhookCoreServiceCollectionExtensions.AddWebhookCore"/>
    /// for the URL guard and the secret protector the handlers take.
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddWebhookEndpoints(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ReplayWebhookDeliveriesHandler>();

        return services;
    }
}
