using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;

namespace Evidgate.Modules.Healing.Application;

/// <summary>
/// The closed set of reasons the gateway refuses a call before any provider is reached, and the failure each one
/// answers with. The reason travels as an <see cref="ErrorDetail"/> on a code the wire vocabulary already carries, so a
/// refusal is one stable machine token and the telemetry counter's label is the same token.
/// </summary>
internal static class HealingLlmRefusals
{
    /// <summary>No provider was selected, so the deployment places no call at all.</summary>
    internal const string ProviderDisabled = "provider_disabled";

    /// <summary>The kill switch resolves to <c>disabled</c> (or a dependency is unavailable) for the scope.</summary>
    internal const string HealingDisabled = "healing_disabled";

    /// <summary>The project's daily budget is used up by cost or by tokens.</summary>
    internal const string BudgetExceeded = "budget_exceeded";

    /// <summary>No daily budget row exists for the project and the kind that is running must not spend unmeasured.</summary>
    internal const string BudgetNotProvisioned = "budget_not_provisioned";

    /// <summary>The budget read itself failed; the call is refused rather than placed unmeasured.</summary>
    internal const string BudgetUnreadable = "budget_unreadable";

    /// <summary>Every reason a call may be refused with, in the order the gateway checks them.</summary>
    internal static IReadOnlyList<string> Reasons { get; } =
        [ProviderDisabled, HealingDisabled, BudgetExceeded, BudgetNotProvisioned, BudgetUnreadable];

    /// <summary>The failure of an invocation refused for <paramref name="reason"/>.</summary>
    /// <param name="reason">One of the constants of this class.</param>
    internal static Result<HealingLlmInvocation> Refuse(string reason) =>
        Result.Failure<HealingLlmInvocation>(ToError(reason));

    /// <summary>The failure a refusal carries: a code the vocabulary already has, and the reason as its only detail.</summary>
    /// <param name="reason">One of the constants of this class.</param>
    internal static Error ToError(string reason) => reason switch
    {
        // The window resets, so the same call may succeed later.
        BudgetExceeded => new Error(
            ApiErrorCode.QuotaExceeded,
            "this project has spent its healing LLM budget for today",
            429,
            Retryable: true,
            Details: Detail(reason)),

        // A deployment or a switch has to change before the call can be placed.
        ProviderDisabled => new Error(
            ApiErrorCode.Unavailable,
            "no healing LLM provider is configured for this deployment",
            503,
            Retryable: false,
            Details: Detail(reason)),

        HealingDisabled => new Error(
            ApiErrorCode.Unavailable,
            "healing is switched off for this scope",
            503,
            Retryable: false,
            Details: Detail(reason)),

        BudgetNotProvisioned => new Error(
            ApiErrorCode.Unavailable,
            "this project has no healing LLM budget provisioned",
            503,
            Retryable: false,
            Details: Detail(reason)),

        // A read fault is transient, so the same call may succeed later.
        BudgetUnreadable => new Error(
            ApiErrorCode.Unavailable,
            "the project's healing LLM budget could not be read",
            503,
            Retryable: true,
            Details: Detail(reason)),

        _ => throw new ArgumentOutOfRangeException(
            nameof(reason),
            reason,
            "not a healing LLM refusal reason"),
    };

    /// <summary>The single detail every refusal carries: the subsystem and the reason token.</summary>
    /// <param name="reason">One of the constants of this class.</param>
    private static IReadOnlyList<ErrorDetail> Detail(string reason) => [new ErrorDetail("healing", reason)];
}
