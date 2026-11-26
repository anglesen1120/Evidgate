namespace Evidgate.Modules.Policy.Webhooks.Domain;

/// <summary>The stored states of <c>webhook_endpoint.state</c> and <c>webhook_outbound_delivery.state</c>.</summary>
internal static class WebhookStates
{
    /// <summary>An endpoint that receives deliveries.</summary>
    public const string EndpointActive = "active";

    /// <summary>A reversible stop: nothing new is delivered.</summary>
    public const string EndpointDisabled = "disabled";

    /// <summary>Terminal soft delete; the row stays for delivery history.</summary>
    public const string EndpointDeleted = "deleted";

    /// <summary>A delivery waiting for its first attempt or a replay.</summary>
    public const string DeliveryQueued = "queued";

    /// <summary>A delivery claimed by a worker.</summary>
    public const string DeliverySending = "sending";

    /// <summary>A 2xx answer was received; terminal.</summary>
    public const string DeliveryDelivered = "delivered";

    /// <summary>The last attempt failed and a later one is scheduled.</summary>
    public const string DeliveryRetrying = "retrying";

    /// <summary>The retry window is exhausted; only an operator replay leaves it.</summary>
    public const string DeliveryDeadLetter = "dead_letter";
}

/// <summary>The closed codes a refused registration or a failed delivery attempt records; never an address or a response body.</summary>
internal static class WebhookErrors
{
    /// <summary>The registered URL breaks the scheme, userinfo, port, host or address rules.</summary>
    public const string EndpointUrlBlocked = "endpoint_url_blocked";

    /// <summary>The host resolved to a blocked address when the delivery connected.</summary>
    public const string DnsRebindingBlocked = "dns_rebinding_blocked";

    /// <summary>The endpoint answered 3xx; redirects are never followed.</summary>
    public const string RedirectNotFollowed = "redirect_not_followed";

    /// <summary>The endpoint was disabled or deleted before the attempt.</summary>
    public const string EndpointDisabled = "endpoint_disabled";
}
