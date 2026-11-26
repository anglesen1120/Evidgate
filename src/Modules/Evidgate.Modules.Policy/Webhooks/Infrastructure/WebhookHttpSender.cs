using System.Net.Http.Headers;
using Evidgate.Modules.Policy.Webhooks.Application;
using Evidgate.Modules.Policy.Webhooks.Domain;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Policy.Webhooks.Infrastructure;

/// <summary>
/// Posts one signed event. Redirects are never followed, the socket goes only to an address the guard validated for
/// this attempt, the attempt has one total deadline, and only the status line and the first few KiB of the answer are
/// read, so a hostile receiver cannot make a worker follow, wait or buffer without bound.
/// </summary>
internal sealed class WebhookHttpSender(IHttpClientFactory clients, IOptions<WebhookDeliveryOptions> options)
{
    /// <summary>The named client: no resilience pipeline, no redirects, the pinned connect callback.</summary>
    public const string ClientName = "webhook";

    /// <summary>Carries the validated target to the connect callback of the pooled handler.</summary>
    public static readonly HttpRequestOptionsKey<WebhookTarget> TargetKey = new("evidgate.webhook.target");

    /// <summary>Sends <paramref name="body"/> to <paramref name="target"/> and classifies the outcome.</summary>
    /// <param name="target">The URL and the addresses the guard accepted for this attempt.</param>
    /// <param name="eventId">The value of the event id header.</param>
    /// <param name="timestamp">The signed Unix timestamp, also sent as a header.</param>
    /// <param name="signature">The <c>v1=</c> signature header value.</param>
    /// <param name="body">The exact signed bytes.</param>
    /// <param name="cancellationToken">The task's token; its cancellation is not an attempt result.</param>
    public async Task<WebhookAttempt> SendAsync(
        WebhookTarget target,
        Guid eventId,
        long timestamp,
        string signature,
        byte[] body,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(body);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(options.Value.TotalTimeout);
        using var request = BuildRequest(target, eventId, timestamp, signature, body);

        try
        {
            using var response = await clients.CreateClient(ClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token)
                .ConfigureAwait(false);
            var status = (int)response.StatusCode;
            await DrainAsync(response, limit.Token, cancellationToken).ConfigureAwait(false);

            return Classify(status);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new WebhookAttempt(WebhookDeliveryErrors.Timeout, null);
        }
        catch (Exception failure) when (failure is HttpRequestException or IOException)
        {
            return new WebhookAttempt(WebhookDeliveryErrors.ConnectionError, null);
        }
    }

    private static HttpRequestMessage BuildRequest(
        WebhookTarget target,
        Guid eventId,
        long timestamp,
        string signature,
        byte[] body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, target.Uri) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        // One connection per attempt: a pooled socket could still point at an address from an earlier validation.
        request.Headers.ConnectionClose = true;
        request.Headers.TryAddWithoutValidation(WebhookSigner.EventIdHeader, eventId.ToString("D"));
        request.Headers.TryAddWithoutValidation(WebhookSigner.EventTypeHeader, EvaluationCreatedPayload.EventType);
        request.Headers.TryAddWithoutValidation(
            WebhookSigner.TimestampHeader,
            timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation(WebhookSigner.SignatureHeader, signature);
        request.Options.Set(TargetKey, target);

        return request;
    }

    /// <summary>
    /// Reads at most the cap and drops it; the status is already known, so a slow or broken body changes nothing. Each
    /// read asks only for the remaining allowance and the drain stops at the cap without waiting for the end of the
    /// body, so a chunked reply cannot make the worker consume more than the cap.
    /// </summary>
    private static async Task DrainAsync(HttpResponseMessage response, CancellationToken limit, CancellationToken caller)
    {
        if (response.Content.Headers.ContentLength > WebhookDeliveryOptions.MaxResponseBytes)
        {
            return;
        }

        try
        {
            var stream = await response.Content.ReadAsStreamAsync(limit).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                var buffer = new byte[WebhookDeliveryOptions.MaxResponseBytes];
                var remaining = buffer.Length;
                while (remaining > 0)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(0, remaining), limit).ConfigureAwait(false);
                    if (read == 0)
                    {
                        return;
                    }

                    remaining -= read;
                }
            }
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested)
        {
            // The deadline ended the read after the status line: the answer stands.
        }
        catch (Exception failure) when (failure is HttpRequestException or IOException)
        {
            // The peer broke the body after the status line: the answer stands.
        }
    }

    private static WebhookAttempt Classify(int status) => status switch
    {
        >= 200 and <= 299 => new WebhookAttempt(null, status),
        >= 300 and <= 399 => new WebhookAttempt(WebhookErrors.RedirectNotFollowed, status),
        _ => new WebhookAttempt(WebhookDeliveryErrors.HttpError, status),
    };
}
