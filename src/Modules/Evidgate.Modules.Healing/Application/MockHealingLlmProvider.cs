using System.Text.Json;
using Evidgate.Modules.Healing.Contracts;

namespace Evidgate.Modules.Healing.Application;

/// <summary>
/// 08.5.1-T1: the provider the module registers by default, because O4 (provider, region, egress) is still open and no
/// real provider call may be enabled before it closes (plan §11). Deterministic and offline: the same request always
/// yields the same JSON answer, so tests and dry runs can drive the whole gateway path without a network or a key.
/// </summary>
/// <remarks>
/// The answer is a fixed JSON object naming the request's own metadata — never its payload, which is not echoed anywhere.
/// Token count is a deterministic estimate of the prompt (four characters per token) rather than a plausible but invented
/// measurement, and the cost is zero because nothing was bought.
/// </remarks>
internal sealed class MockHealingLlmProvider : IHealingLlmProvider
{
    /// <summary>The marker every answer carries, so a caller can tell a mock answer from a provider's.</summary>
    internal const string ContentMarker = "healing-llm-mock";

    private int _calls;

    /// <summary>How many times the mock was invoked (tests assert the gateway's retry bound through it).</summary>
    internal int Calls => Volatile.Read(ref _calls);

    /// <inheritdoc />
    public Task<HealingLlmResult> InvokeAsync(HealingLlmRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        _ = Interlocked.Increment(ref _calls);

        var content = JsonSerializer.Serialize(new
        {
            mock = ContentMarker,
            purpose = request.Purpose,
            model = request.Model,
            prompt_version = request.PromptVersion,
        });

        // Four characters per token, the usual English rule of thumb: deterministic, and it never inspects the payload's
        // text beyond its length.
        var tokens = request.Payload.Length / 4;

        return Task.FromResult(new HealingLlmResult(HealingLlmStatuses.Ok, content, tokens, 0m));
    }
}
