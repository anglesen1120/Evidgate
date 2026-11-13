namespace Evidgate.Modules.Healing.Application;

/// <summary>
/// A refusal by the provider's own request handlers (egress pin, key lookup) before any byte left the process. The
/// gateway tells it apart from a transport fault: nothing can have been billed, so the held estimate is given back.
/// </summary>
/// <param name="message">What was refused; it names a configuration key, never a target, payload or secret.</param>
internal sealed class HealingLlmNotSentException(string message) : InvalidOperationException(message);
