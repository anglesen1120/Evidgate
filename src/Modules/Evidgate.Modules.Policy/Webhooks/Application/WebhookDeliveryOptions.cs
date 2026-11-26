namespace Evidgate.Modules.Policy.Webhooks.Application;

/// <summary>Limits of one outbound delivery attempt (<c>Webhooks:Delivery</c>).</summary>
internal sealed class WebhookDeliveryOptions
{
    /// <summary>The configuration section.</summary>
    public const string Section = "Webhooks:Delivery";

    /// <summary>How much of a response body is read before the connection is closed.</summary>
    public const int MaxResponseBytes = 4096;

    /// <summary>Longest wait for the TCP connection.</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Longest wait for the whole attempt, from connect to the end of the bounded body read.</summary>
    public TimeSpan TotalTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>True when both limits are positive and the connect limit fits inside the total one.</summary>
    public bool IsValid => ConnectTimeout > TimeSpan.Zero && TotalTimeout >= ConnectTimeout;
}

/// <summary>The closed codes a failed attempt stores in <c>last_error</c>; never a response text or an address.</summary>
internal static class WebhookDeliveryErrors
{
    /// <summary>The attempt exceeded its total time.</summary>
    public const string Timeout = "timeout";

    /// <summary>The connection or TLS handshake failed, or the stream broke.</summary>
    public const string ConnectionError = "connection_error";

    /// <summary>The endpoint answered with a status outside 2xx and 3xx.</summary>
    public const string HttpError = "http_error";

    /// <summary>The stored secret could not be decrypted.</summary>
    public const string SecretUnreadable = "secret_unreadable";
}

/// <summary>What one attempt achieved: delivered, or a closed error code with the status seen, if any.</summary>
/// <param name="Error">Null when the endpoint answered 2xx.</param>
/// <param name="StatusCode">The HTTP status the endpoint answered, when it answered.</param>
internal readonly record struct WebhookAttempt(string? Error, int? StatusCode)
{
    /// <summary>True for a 2xx answer.</summary>
    public bool Delivered => Error is null;
}
