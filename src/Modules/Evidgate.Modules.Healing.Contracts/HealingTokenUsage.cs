namespace Evidgate.Modules.Healing.Contracts;

/// <summary>
/// What one org's healing attempts spent on model tokens inside a window: the sum every recorded attempt
/// contributed, and how many attempts reported a token count at all.
/// </summary>
/// <remarks>
/// The two values answer different questions and travel together because neither is trustworthy alone. The sum
/// covers every attempt — a retry that failed after the provider had already read the prompt burned tokens too —
/// while <see cref="KnownAttempts"/> says whether anything was actually reported: an attempt whose status failed
/// and which reported no tokens is unknown spend, not a measured zero. A caller that states a cost must not
/// present the second as the first.
/// </remarks>
/// <param name="Tokens">
/// The tokens the window's attempts reported, or null when the window holds no attempt to measure.
/// </param>
/// <param name="KnownAttempts">
/// The attempts whose token use is known: an <c>ok</c> attempt, or any attempt above zero tokens.
/// </param>
public sealed record HealingTokenUsage(decimal? Tokens, long KnownAttempts);
