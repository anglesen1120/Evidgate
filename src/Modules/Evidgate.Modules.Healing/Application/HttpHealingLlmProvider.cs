using System.Text;
using System.Text.Json;
using Evidgate.Modules.Healing.Contracts;
using Polly.Timeout;

namespace Evidgate.Modules.Healing.Application;

/// <summary>
/// 08.5.1-T1: the time budget and retry bound of the healing LLM adapter. No configuration key exists for these in this
/// slice (plan §11: nothing real is enabled before O4 closes), so the defaults are in code and a host that later wires the
/// adapter overrides them through <c>IOptions</c>.
/// </summary>
internal sealed class HealingLlmOptions
{
    /// <summary>Budget of one attempt: the per-call timeout AC2 aborts on. An LLM answer is slow, so this is generous.</summary>
    internal TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Transport retries the bounded resilience policy may add on top of the first attempt. The default is 0: the
    /// provider call is a POST, which is not idempotent, so nothing is retried unless a host opts in with a positive
    /// value — the pipeline then adds the retry strategy, because Polly's retry strategy requires at least one attempt.
    /// </summary>
    internal int MaxTransportRetries { get; set; }

    /// <summary>
    /// The output tokens one call may produce, held in the budget together with the prompt's estimate before the provider
    /// is called. A deployment-neutral ceiling: the real per-model limit arrives with the provider contract.
    /// </summary>
    internal int ReservedOutputTokens { get; set; } = 1024;

    /// <summary>
    /// The cost held in the budget for one call before it is placed, replaced by the provider's reported cost afterwards.
    /// A conservative placeholder until the price table of a real provider exists; it bounds concurrent overshoot to this
    /// amount per call in flight.
    /// </summary>
    internal decimal ReservedCostPerCall { get; set; } = 0.05m;

    /// <summary>
    /// Budget of one whole call: one attempt per allowed retry, plus the retry delays and a little slack — with the
    /// default 0 retries that is a single attempt. The client's own <c>Timeout</c> is infinite, so this — not a shorter
    /// client timeout — is what bounds the call.
    /// </summary>
    internal TimeSpan TotalTimeout => (AttemptTimeout * (MaxTransportRetries + 1)) + TimeSpan.FromSeconds(2);
}

/// <summary>The typed client the healing LLM adapter is reached through.</summary>
internal static class HealingLlmHttpClients
{
    /// <summary>Name of the resilience pipeline wrapped around the client.</summary>
    internal const string Pipeline = "healing-llm";

    /// <summary>
    /// Hard ceiling on the response body the client buffers. An answer is a small JSON envelope, so anything above
    /// this is a fault: the read fails and the adapter maps that failure to <c>error</c> instead of buffering it.
    /// </summary>
    internal const int MaxResponseBytes = 1024 * 1024;

    /// <summary>
    /// The path the adapter posts to, relative to the client's base address. The base address is deployment configuration
    /// that this slice deliberately does not wire (O4): no host, no key, no egress.
    /// </summary>
    internal const string GeneratePath = "llm/generate";
}

/// <summary>
/// The real-provider adapter seam, over a typed <see cref="HttpClient"/> whose base address, resilience policy and key
/// the host configures. Its wire shape is this adapter's own, provider-neutral request/answer envelope:
/// <c>{ "model", "prompt_version", "prompt", "response_schema" }</c> out, <c>{ "content", "tokens", "cost" }</c> back,
/// where <c>content</c> is the answer body as a JSON string or a nested JSON value. A concrete provider's mapping lands
/// with O4 without changing the gateway contract.
/// </summary>
/// <remarks>
/// The adapter maps its own transport failure, time budget and non-success status into
/// <see cref="HealingLlmStatuses"/> instead of throwing, so the gateway records every attempt; only the caller's own
/// cancellation propagates. A response body above the client's buffer ceiling fails the read, and that failure — like
/// any other read fault — is an <c>error</c> result, never an exception. It holds no API key, logs nothing, and never
/// puts the payload or the response in an exception message (S3, AC6).
/// </remarks>
internal sealed class HttpHealingLlmProvider(HttpClient client) : IHealingLlmProvider
{
    /// <inheritdoc />
    public async Task<HealingLlmResult> InvokeAsync(HealingLlmRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The prompt goes out and nowhere else: no log line, no exception message, no DB column (AC6).
        var body = JsonSerializer.Serialize(new
        {
            model = request.Model,
            prompt_version = request.PromptVersion,
            prompt = request.Payload,
            response_schema = request.ResponseSchema,
        });

        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        try
        {
            using var response = await client
                .PostAsync(HealingLlmHttpClients.GeneratePath, content, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // With retries opted in, 408/429/5xx are the resilience policy's to retry; whatever status survives it —
                // and any status at all when the retry count is 0 — is a failure to record, never content to validate.
                return Failure(HealingLlmStatuses.Error);
            }

            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return Answer(text);
        }
        catch (TimeoutRejectedException)
        {
            // AC2: the policy's time budget fired (per-attempt or total), so the call is aborted.
            return Failure(HealingLlmStatuses.Timeout);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The client's own cancellation without the caller asking for it is the time budget as HttpClient sees it.
            return Failure(HealingLlmStatuses.Timeout);
        }
        catch (OperationCanceledException)
        {
            // The caller cancelled: that is not a provider outcome and must not be recorded as one.
            throw;
        }
        catch (HttpRequestException)
        {
            // A transport fault, or the response buffer ceiling the client enforces: nothing to validate, nothing spent.
            return Failure(HealingLlmStatuses.Error);
        }
        catch (InvalidOperationException)
        {
            // HttpClient refuses a request it cannot send or a body it cannot hand over the same way; the gateway must
            // still record an attempt, so that refusal is an error result rather than an exception out of the adapter.
            return Failure(HealingLlmStatuses.Error);
        }
        catch (IOException)
        {
            // The body read broke mid-stream: a partial body is not an answer, so it is never parsed.
            return Failure(HealingLlmStatuses.Error);
        }
    }

    /// <summary>A failure result: no content, nothing spent that the provider reported.</summary>
    private static HealingLlmResult Failure(string status) => new(status, null, 0, 0m);

    /// <summary>
    /// Maps a success body to a result. A body that is not the adapter's envelope is an <c>error</c>, not an empty
    /// answer: the gateway must not mistake a broken adapter for a provider that said nothing.
    /// </summary>
    private static HealingLlmResult Answer(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Failure(HealingLlmStatuses.Error);
            }

            var tokens = root.TryGetProperty("tokens", out var tokensValue) && tokensValue.TryGetInt32(out var parsedTokens)
                ? parsedTokens
                : 0;

            var cost = root.TryGetProperty("cost", out var costValue) && costValue.TryGetDecimal(out var parsedCost)
                ? parsedCost
                : 0m;

            if (!root.TryGetProperty("content", out var contentValue))
            {
                return new HealingLlmResult(HealingLlmStatuses.Ok, null, tokens, cost);
            }

            // A string is the answer as the provider wrote it; any other value is a JSON body and is kept as its raw text,
            // which is what the gateway then validates against the declared schema.
            var content = contentValue.ValueKind switch
            {
                JsonValueKind.String => contentValue.GetString(),
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => contentValue.GetRawText(),
            };

            return new HealingLlmResult(HealingLlmStatuses.Ok, content, tokens, cost);
        }
        catch (JsonException)
        {
            return Failure(HealingLlmStatuses.Error);
        }
    }
}
