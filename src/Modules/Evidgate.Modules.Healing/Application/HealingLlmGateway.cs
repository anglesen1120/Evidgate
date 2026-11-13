using System.Data.Common;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Healing.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Evidgate.Modules.Healing.Application;

/// <summary>
/// 08.5.1-T1: the attribution rules of <c>healing_llm_call</c> (migration 0054), enforced in managed code before the first
/// row is written, so a caller's mistake fails with a message instead of a PostgreSQL CHECK violation (or, worse, a row a
/// later reader has to interpret).
/// </summary>
internal static class HealingLlmRequestRules
{
    /// <summary>
    /// The attempt budget of one gateway call: the first attempt plus exactly one retry of a schema violation (AC1). A
    /// transport failure is not retried here — the typed client's bounded resilience policy owns that (AC2).
    /// </summary>
    internal const int MaxAttempts = 2;

    /// <summary>Validates <paramref name="request"/> against the table's CHECK constraints.</summary>
    /// <param name="request">The call about to be placed.</param>
    /// <exception cref="ArgumentException">
    /// The call cannot be attributed: an unknown purpose, no project/model/prompt version, an authoring call naming an
    /// incident or candidate, or an incident-scoped purpose naming no incident.
    /// </exception>
    internal static void EnsureValid(HealingLlmRequest request)
    {
        if (request.ProjectId == Guid.Empty)
        {
            throw new ArgumentException(
                "a healing LLM call must name the project it is attributed to and costed against",
                nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.Model))
        {
            throw new ArgumentException("a healing LLM call must name the model it asks for", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.PromptVersion))
        {
            throw new ArgumentException("a healing LLM call must name its prompt version (08.7.2 lineage)", nameof(request));
        }

        if (request.Payload is null)
        {
            throw new ArgumentException(
                "a healing LLM call must carry the redacted payload (08.5.3 redacts before this seam)",
                nameof(request));
        }

        if (request.Purpose is not (HealingLlmPurposes.AuthoringGeneration
            or HealingLlmPurposes.TriageHypothesis
            or HealingLlmPurposes.CandidateGeneration))
        {
            throw new ArgumentException(
                $"unknown healing LLM purpose '{request.Purpose}': "
                + $"expected one of '{HealingLlmPurposes.AuthoringGeneration}', '{HealingLlmPurposes.TriageHypothesis}', "
                + $"'{HealingLlmPurposes.CandidateGeneration}'",
                nameof(request));
        }

        if (request.Purpose == HealingLlmPurposes.AuthoringGeneration)
        {
            if (request.IncidentId is not null || request.CandidateId is not null)
            {
                throw new ArgumentException(
                    "an authoring_generation call serves a project generation and must name no incident or candidate",
                    nameof(request));
            }

            return;
        }

        if (request.IncidentId is null)
        {
            throw new ArgumentException(
                $"a healing LLM call with purpose '{request.Purpose}' must name its incident "
                + $"('{HealingLlmPurposes.AuthoringGeneration}' is the only purpose without one)",
                nameof(request));
        }
    }
}

/// <summary>
/// 08.5.1-T1: one gateway call's outcome. The gateway records every attempt and returns the last one, together with the
/// id of that row — the value a generation stores as <c>authoring_version.ai_run_id</c> so the artefact it produced is
/// attributable and costed (AC7).
/// </summary>
/// <param name="Result">
/// The accepted answer, or the failing status with no content: a refused answer never leaves the gateway (AC1). Its
/// tokens and cost are the totals over every attempt of the call, not the last attempt's alone.
/// </param>
/// <param name="CallId">The <c>healing_llm_call.id</c> of the attempt this outcome reports.</param>
internal sealed record HealingLlmInvocation(HealingLlmResult Result, Guid CallId);

/// <summary>
/// 08.5.1-T1: the provider-adapter gateway. It invokes the adapter, validates the answer against the caller-declared JSON
/// Schema, retries a schema violation exactly once (AC1), fails closed, and records one row per attempt (AC4, AC7).
/// </summary>
/// <remarks>
/// Every attempt is a two-phase unit: the budget is reserved and committed in one short tenant transaction, the provider
/// is called with no transaction and no scope open, and the real usage plus the attempt row are committed in a second one
/// (AC3). The caller therefore passes the org id and holds no tenant scope.
/// <para>
/// Before the schema is compiled and before any adapter is reached, two guards run: the deployment's provider selection
/// and the kill switch in force for the project. The budget reservation follows the schema compile. Each refusal is a
/// failed <see cref="Result{T}"/> naming one reason of <see cref="HealingLlmRefusals"/>, counts once on
/// <see cref="EvidgateTelemetry.HealingLlmRefusedMetricName"/> and writes no row — a refused call spends nothing.
/// </para>
/// <para>
/// A reservation is released in a fresh scope only for a failure before the request is sent. Once the provider call has
/// started, a throw, a cancel, a timeout or a failed settlement keeps the estimate charged (an error row records it),
/// because the provider may have billed the call. A hard process kill between the phases leaks the same way until the
/// period resets; every such leak can only over-count,
/// which makes the next reservation refuse sooner. Real usage above an estimate is charged at settlement, so a call can
/// overshoot a cap by its excess until the provider enforces an output ceiling. The reset belongs to the reset schedule.
/// </para>
/// </remarks>
internal sealed partial class HealingLlmGateway(
    IHealingLlmProvider provider,
    HealingLlmCallRecorder recorder,
    IHealingAvailabilityResolver availability,
    HealingLlmBudgetLedger ledger,
    HealingLlmTransactions transactions,
    IOptions<HealingProviderOptions> options,
    IOptions<HealingLlmOptions> llmOptions,
    ILogger<HealingLlmGateway> logger)
{
    /// <summary>Places the call and records every attempt.</summary>
    /// <param name="orgId">The tenant the call belongs to, from the caller's authenticated context.</param>
    /// <param name="request">The call; its payload is already redacted by the caller.</param>
    /// <param name="cancellationToken">Cancellation of the call.</param>
    /// <returns>
    /// The outcome, with the id of the row that reports it, or the failure of a guard that refused the call.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// The request cannot be attributed, or its declared schema is not a JSON Schema document. Both are refused before
    /// the provider is called, so neither spends a token nor writes a row.
    /// </exception>
    /// <exception cref="InvalidOperationException">The caller holds a tenant scope open.</exception>
    internal async Task<Result<HealingLlmInvocation>> InvokeAsync(
        Guid orgId,
        HealingLlmRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        HealingLlmRequestRules.EnsureValid(request);

        if (await AdmissionRefusalAsync(orgId, request, cancellationToken).ConfigureAwait(false) is { } refusal)
        {
            return refusal;
        }

        // Compiled before the first attempt: a declaration that does not compile is a caller bug, and refusing it here
        // keeps the provider, the budget and the table out of a failure that has nothing to do with them.
        var schema = HealingLlmResponseSchema.Compile(request.ResponseSchema);

        // The caller sees what the whole call spent, not only the attempt that happened to answer last (AC7).
        var totalTokens = 0L;
        var totalCost = 0m;
        HealingLlmInvocation? last = null;

        for (var attempt = 1; ; attempt++)
        {
            var (verdict, reservation) = await transactions
                .RunAsync(
                    orgId,
                    request.ProjectId,
                    ct => ledger.ReserveAsync(request.ProjectId, EstimatedTokens(request), llmOptions.Value.ReservedCostPerCall, ct),
                    cancellationToken)
                .ConfigureAwait(false);

            if (BudgetRefusal(verdict) is { } budgetReason)
            {
                // A refused retry leaves the first attempt's recorded failure as the outcome: it is final, and nothing
                // more may be spent.
                if (last is not null)
                {
                    EvidgateTelemetry.RecordHealingLlmRefused(budgetReason);
                    return Result.Success(last);
                }

                return Refuse(budgetReason);
            }

            var attemptResult = await RunAttemptAsync(orgId, request, attempt, schema, reservation, cancellationToken)
                .ConfigureAwait(false);

            // A token total that two attempts could push past int is clamped instead of wrapping negative.
            totalTokens = Math.Min(totalTokens + attemptResult.Tokens, int.MaxValue);
            totalCost += attemptResult.Cost;

            last = new HealingLlmInvocation(
                new HealingLlmResult(
                    attemptResult.Status,
                    attemptResult.Status == HealingLlmStatuses.Ok ? attemptResult.Content : null,
                    (int)totalTokens,
                    totalCost),
                attemptResult.CallId);

            // AC1: one fresh call answers a schema violation; a second refusal is final and the caller sees no content.
            if (attemptResult.Status == HealingLlmStatuses.SchemaViolation && attempt < HealingLlmRequestRules.MaxAttempts)
            {
                continue;
            }

            return Result.Success(last);
        }
    }

    /// <summary>
    /// One two-phase attempt: the reservation is already committed, the provider runs outside any transaction, and the
    /// settlement commits usage and the row together. A failure before the request is sent releases the reservation; once it may have been sent, the estimate stays charged.
    /// </summary>
    private async Task<(string Status, string? Content, int Tokens, decimal Cost, Guid CallId)> RunAttemptAsync(
        Guid orgId,
        HealingLlmRequest request,
        int attempt,
        Corvus.Json.Validator.JsonSchema? schema,
        HealingLlmReservation? reservation,
        CancellationToken cancellationToken)
    {
        HealingLlmResult result;

        if (cancellationToken.IsCancellationRequested)
        {
            // Nothing has left the process yet, so nothing can have been billed: the hold goes back whole.
            await ReleaseAsync(orgId, request.ProjectId, reservation).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }

        var notSent = false;

        try
        {
            // The provider's own numbers are brought inside the row's bounds before anything is recorded, so a provider
            // that reports an unknown status or an impossible cost cannot fail the INSERT or label the ledger.
            result = HealingLlmResultRules.Recordable(
                await provider.InvokeAsync(request, cancellationToken).ConfigureAwait(false));
        }
        catch (HealingLlmNotSentException)
        {
            // A handler refused before the send, so nothing can have been billed: settling zero usage records the error
            // row and returns the whole hold, unlike the unknown-outcome failures below that keep it charged.
            result = new HealingLlmResult(HealingLlmStatuses.Error, null, 0, 0m);
            notSent = true;
        }
        catch
        {
            // The request may have left the process, so the provider may bill it whatever the outcome was: the hold
            // stays as the charge (an over-count at worst) and an error row records it. Refunding here would let repeated
            // failing calls drain the budget past its cap.
            await ChargeEstimateAsync(orgId, request, attempt, reservation).ConfigureAwait(false);
            throw;
        }

        if (reservation is not null && !notSent && !ReportsUsage(result))
        {
            // The adapter reports a timeout or error as a result, and a result without usage says nothing about what the
            // provider billed: charge the held estimate rather than the zeros, the real status stays on the row.
            result = result with { Tokens = reservation.Tokens, Cost = reservation.Cost };
        }

        var status = result.Status;
        var content = result.Content;

        // Fail closed: an empty answer, a body that is not JSON, or one the declared schema refuses is not content.
        if (status == HealingLlmStatuses.Ok && !HealingLlmResponseSchema.Accepts(schema, content))
        {
            status = HealingLlmStatuses.SchemaViolation;
            content = null;
        }

        var callId = await SettleAttemptAsync(orgId, request, attempt, status, result, reservation).ConfigureAwait(false);
        return (status, content, result.Tokens, result.Cost, callId);
    }

    /// <summary>
    /// Commits the attempt row and the usage together. A settlement that fails keeps the hold (see the catch).
    /// </summary>
    private async Task<Guid> SettleAttemptAsync(
        Guid orgId,
        HealingLlmRequest request,
        int attempt,
        string status,
        HealingLlmResult result,
        HealingLlmReservation? reservation)
    {
        try
        {
            // The provider was paid whether or not the caller is still listening, so the settlement ignores cancellation.
            var callId = await transactions
                .RunAsync(
                    orgId,
                    request.ProjectId,
                    async ct =>
                    {
                        var id = await recorder
                            .RecordAsync(request, attempt, status, result.Tokens, result.Cost, ct)
                            .ConfigureAwait(false);

                        if (reservation is not null)
                        {
                            await ledger.SettleAsync(request.ProjectId, reservation, result.Tokens, result.Cost, ct)
                                .ConfigureAwait(false);
                        }

                        return id;
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);

            return callId;
        }
        catch (Exception exception) when (exception is DbException or IOException)
        {
            // The provider answered, so its usage is owed whether or not the ledger took it. The hold stays as the charge:
            // releasing after a refused or lost settlement would refund billed spend (or subtract it twice), so the
            // counters can only over-count until the period resets.
            LogSettlementUnknown(exception, request.ProjectId);
            throw;
        }
    }

    /// <summary>
    /// Records a call whose outcome is unknown as an error row at the held estimate and settles the hold to exactly that,
    /// so the counters keep the charge. Faults here are logged: the hold stays either way and must not mask the failure.
    /// </summary>
    private async Task ChargeEstimateAsync(
        Guid orgId,
        HealingLlmRequest request,
        int attempt,
        HealingLlmReservation? reservation)
    {
        if (reservation is null)
        {
            return;
        }

        try
        {
            _ = await SettleAttemptAsync(
                    orgId,
                    request,
                    attempt,
                    HealingLlmStatuses.Error,
                    new HealingLlmResult(HealingLlmStatuses.Error, null, reservation.Tokens, reservation.Cost),
                    reservation)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogSettlementUnknown(exception, request.ProjectId);
        }
    }

    /// <summary>
    /// Whether a returned result carries usage the ledger can take as real: a timeout, an error and a result with neither
    /// tokens nor cost (absent, or replaced by the bounds check) do not.
    /// </summary>
    /// <param name="result">The result after the bounds check.</param>
    private static bool ReportsUsage(HealingLlmResult result) =>
        result.Status != HealingLlmStatuses.Timeout
        && result.Status != HealingLlmStatuses.Error
        && (result.Tokens > 0 || result.Cost > 0m);

    /// <summary>
    /// Gives the hold back in a fresh scope, whatever the caller's token says. A release that itself fails is logged and
    /// not thrown, so it cannot mask the failure that made the release necessary; the leak is the bounded over-count
    /// described on the class.
    /// </summary>
    private async Task ReleaseAsync(Guid orgId, Guid projectId, HealingLlmReservation? reservation)
    {
        if (reservation is null)
        {
            return;
        }

        try
        {
            await transactions
                .RunAsync(
                    orgId,
                    projectId,
                    async ct =>
                    {
                        await ledger.SettleAsync(projectId, reservation, 0, 0m, ct).ConfigureAwait(false);
                        return true;
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException)
        {
            LogReleaseFailed(exception, projectId);
        }
    }

    /// <summary>The structured record of a reservation that could not be released (the project only).</summary>
    /// <param name="exception">The release failure.</param>
    /// <param name="projectId">The project whose estimate stays counted until the period resets.</param>
    [LoggerMessage(
        EventId = 8610,
        Level = LogLevel.Error,
        Message = "Healing LLM budget reservation could not be released; it stays counted until the period resets (project={ProjectId})")]
    private partial void LogReleaseFailed(Exception exception, Guid projectId);

    /// <summary>The structured record of a settlement whose outcome is unknown; the hold stays counted.</summary>
    /// <param name="exception">The connection-level failure.</param>
    /// <param name="projectId">The project whose estimate stays counted until the period resets.</param>
    [LoggerMessage(
        EventId = 8611,
        Level = LogLevel.Error,
        Message = "Healing LLM settlement outcome unknown; the reservation is kept counted until the period resets (project={ProjectId})")]
    private partial void LogSettlementUnknown(Exception exception, Guid projectId);

    /// <summary>The tokens to hold for one attempt: the prompt and schema estimate plus the output ceiling.</summary>
    /// <param name="request">The call about to be placed.</param>
    private int EstimatedTokens(HealingLlmRequest request) =>
        (int)Math.Min(
            (((long)request.Payload.Length + (request.ResponseSchema?.Length ?? 0)) / 4) + 1 + llmOptions.Value.ReservedOutputTokens,
            int.MaxValue);

    /// <summary>The refusal reason a budget verdict maps to, or null when the reservation was taken or is not needed.</summary>
    /// <param name="verdict">What the reservation came to.</param>
    private string? BudgetRefusal(HealingLlmBudgetVerdict verdict) => verdict switch
    {
        HealingLlmBudgetVerdict.Exceeded => HealingLlmRefusals.BudgetExceeded,
        HealingLlmBudgetVerdict.Unreadable => HealingLlmRefusals.BudgetUnreadable,

        // The offline adapter may run without a budget row, because it spends nothing.
        HealingLlmBudgetVerdict.NotProvisioned when options.Value.Kind != HealingProviderKind.Mock =>
            HealingLlmRefusals.BudgetNotProvisioned,
        _ => null,
    };

    /// <summary>
    /// The guard the call stops at before any reservation, or null when it may proceed: no selected provider, or a switch
    /// that forbids acting. Shadow and enabled both proceed — shadow healing still analyses.
    /// </summary>
    /// <param name="orgId">The tenant the call belongs to.</param>
    /// <param name="request">The call about to be placed.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    private async Task<Result<HealingLlmInvocation>?> AdmissionRefusalAsync(
        Guid orgId,
        HealingLlmRequest request,
        CancellationToken cancellationToken)
    {
        if (options.Value.Kind == HealingProviderKind.None)
        {
            return Refuse(HealingLlmRefusals.ProviderDisabled);
        }

        var mode = await transactions
            .RunAsync(
                orgId,
                request.ProjectId,
                async ct => await availability.ResolveAsync(request.ProjectId, ct).ConfigureAwait(false),
                cancellationToken)
            .ConfigureAwait(false);

        return mode is HealingAvailability.Enabled or HealingAvailability.Shadow
            ? null
            : Refuse(HealingLlmRefusals.HealingDisabled);
    }

    /// <summary>The refusal of one call: counted once, then returned as the typed failure.</summary>
    /// <param name="reason">One of <see cref="HealingLlmRefusals"/>' constants.</param>
    private static Result<HealingLlmInvocation> Refuse(string reason)
    {
        EvidgateTelemetry.RecordHealingLlmRefused(reason);
        return HealingLlmRefusals.Refuse(reason);
    }
}
