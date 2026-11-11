using Evidgate.Modules.Healing.Contracts;

namespace Evidgate.Modules.Healing.Application;

/// <summary>
/// The adapter a host runs when no provider was selected. It answers <c>error</c> with nothing spent, so a call that
/// reaches it cannot look like a provider that failed: the gateway refuses such a call before any adapter is invoked, and
/// this type is the last line of that defence rather than a silent success.
/// </summary>
internal sealed class DisabledHealingLlmProvider : IHealingLlmProvider
{
    /// <inheritdoc />
    public Task<HealingLlmResult> InvokeAsync(HealingLlmRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(new HealingLlmResult(HealingLlmStatuses.Error, null, 0, 0m));
    }
}
