using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.Modules.Policy.Webhooks.Application;
using Evidgate.Modules.Policy.Webhooks.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Policy.Webhooks;

/// <summary>Registers the shared pieces every webhook slice uses.</summary>
internal static class WebhookCoreServiceCollectionExtensions
{
    /// <summary>Adds the URL guard, the secret protector, and the task writer shared by fan-out and replay.</summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddWebhookCore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<WebhookUrlOptions>()
            .BindConfiguration(WebhookUrlOptions.Section)
            .Validate(
                static o => o.AllowedPorts.All(static p => p is >= 1 and <= 65535),
                $"{WebhookUrlOptions.Section}:AllowedPorts must hold ports from 1 to 65535")
            .ValidateOnStart();
        services.TryAddSingleton<IWebhookHostResolver, DnsWebhookHostResolver>();
        services.TryAddSingleton<WebhookUrlGuard>();
        services.TryAddSingleton<WebhookSecretProtector>();
        services.TryAddSingleton<ITaskQueueWriter, TaskQueueWriter>();

        return services;
    }
}
