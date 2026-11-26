using Microsoft.Extensions.DependencyInjection;

namespace Evidgate.BuildingBlocks.Persistence.Outbox;

/// <summary>
/// A module's standing interest in one outbox event type: besides the task the event's own <c>task_type</c> names, the
/// dispatcher inserts one <paramref name="DeliveryTaskType"/> task per event, carrying the event's payload.
/// </summary>
/// <remarks>
/// One consumer per event type was the dispatcher's rule; a second module that must react to the same event gets its
/// own task row instead of sharing the first consumer's, so one module's failure or dead letter never blocks the other.
/// </remarks>
/// <param name="SourceTaskType">The outbox row's <c>task_type</c> the subscriber follows.</param>
/// <param name="DeliveryTaskType">The <c>task_type</c> of the subscriber's own task; unique per subscription.</param>
public sealed record OutboxSubscription(string SourceTaskType, string DeliveryTaskType);

/// <summary>Registration of <see cref="OutboxSubscription"/>s.</summary>
public static class OutboxSubscriptionExtensions
{
    /// <summary>Adds a subscription once; the same pair registered twice is a no-op.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="sourceTaskType">The outbox <c>task_type</c> to follow.</param>
    /// <param name="deliveryTaskType">The <c>task_type</c> of the subscriber's own task.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddOutboxSubscription(
        this IServiceCollection services,
        string sourceTaskType,
        string deliveryTaskType)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceTaskType);
        ArgumentException.ThrowIfNullOrWhiteSpace(deliveryTaskType);

        var subscription = new OutboxSubscription(sourceTaskType, deliveryTaskType);
        // TryAddEnumerable keys an instance descriptor by its type, which would drop every subscription after the first.
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(OutboxSubscription)
            && Equals(descriptor.ImplementationInstance, subscription)))
        {
            services.AddSingleton(subscription);
        }

        return services;
    }
}
