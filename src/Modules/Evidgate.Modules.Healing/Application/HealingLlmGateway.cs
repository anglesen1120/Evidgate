using Evidgate.Modules.Healing.Contracts;

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
/// 08.5.1-T1: the bounds the ledger puts on a provider's own numbers, applied before the first row is written, so a
/// provider that reports nonsense cannot fail the INSERT (<c>tokens</c> CHECK, <c>numeric(12,6)</c> overflow) or write a
/// <c>status</c> the CHECK refuses.
/// </summary>
internal static class HealingLlmResultRules
{
    /// <summary>The largest cost <c>healing_llm_call.cost numeric(12,6)</c> can hold.</summary>
    internal const decimal MaxRecordableCost = 999_999.999999m;

    /// <summary>
    /// The result as it may be recorded: unchanged when its status is one of <see cref="HealingLlmStatuses"/> and its
    /// reported usage fits the column types, otherwise an <c>error</c> with no content and nothing spent.
    /// </summary>
    /// <param name="result">What the adapter reported for one attempt.</param>
    /// <returns>The result the gateway records and counts.</returns>
    internal static HealingLlmResult Recordable(HealingLlmResult result) =>
        HealingLlmStatuses.IsKnown(result.Status) && result.Tokens >= 0 && IsRecordableCost(result.Cost)
            ? result
            : new HealingLlmResult(HealingLlmStatuses.Error, null, 0, 0m);

    // Postgres rounds to the column's scale (6) before range-checking, so a cost just under one million can still
    // overflow numeric(12,6); the rounding is repeated here with the same half-away-from-zero rule.
    private static bool IsRecordableCost(decimal cost) =>
        cost >= 0m && decimal.Round(cost, 6, MidpointRounding.AwayFromZero) <= MaxRecordableCost;
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
/// Schema, retries a schema violation exactly once (AC1), fails closed, and records one row per attempt in the ambient
/// tenant transaction (AC4, AC7). It opens no tenant scope and no transaction of its own: it records inside whichever
/// tenant scope and transaction the caller already has open.
/// </summary>
/// <remarks>
/// <c>P5</c> is not delivered by this slice. The provider call runs in the caller's tenant scope today, and the attempt
/// rows are written on the same connection inside the caller's transaction, so a caller rollback discards them with the
/// rest of that unit of work. 08.5.1-T1.4 owns the two-phase shape — reserve, call the provider outside the transaction,
/// commit — that actually moves the provider call out of the caller's database work; until it lands, nothing here may be
/// read as claiming that isolation.
/// </remarks>
internal sealed class HealingLlmGateway(IHealingLlmProvider provider, HealingLlmCallRecorder recorder)
{
    /// <summary>Places the call and records every attempt.</summary>
    /// <param name="request">The call; its payload is already redacted by the caller.</param>
    /// <param name="cancellationToken">Cancellation of the call.</param>
    /// <returns>The outcome, with the id of the row that reports it.</returns>
    /// <exception cref="ArgumentException">
    /// The request cannot be attributed, or its declared schema is not a JSON Schema document. Both are refused before
    /// the provider is called, so neither spends a token nor writes a row.
    /// </exception>
    internal async Task<HealingLlmInvocation> InvokeAsync(
        HealingLlmRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        HealingLlmRequestRules.EnsureValid(request);

        // Compiled before the first attempt: a declaration that does not compile is a caller bug, and refusing it here
        // keeps both the provider and the table out of a failure that has nothing to do with either.
        var schema = HealingLlmResponseSchema.Compile(request.ResponseSchema);

        // The caller sees what the whole call spent, not only the attempt that happened to answer last (AC7).
        var totalTokens = 0L;
        var totalCost = 0m;

        for (var attempt = 1; ; attempt++)
        {
            // The provider's own numbers are brought inside the row's bounds before anything is recorded, so a provider
            // that reports an unknown status or an impossible cost cannot fail the INSERT or label the ledger.
            var result = HealingLlmResultRules.Recordable(
                await provider.InvokeAsync(request, cancellationToken).ConfigureAwait(false));

            var status = result.Status;
            var content = result.Content;

            // Fail closed: an empty answer, a body that is not JSON, or one the declared schema refuses is not content.
            if (status == HealingLlmStatuses.Ok && !HealingLlmResponseSchema.Accepts(schema, content))
            {
                status = HealingLlmStatuses.SchemaViolation;
                content = null;
            }

            var callId = await recorder
                .RecordAsync(request, attempt, status, result.Tokens, result.Cost, cancellationToken)
                .ConfigureAwait(false);

            // A token total that two attempts could push past int is clamped instead of wrapping negative.
            totalTokens = Math.Min(totalTokens + result.Tokens, (long)int.MaxValue);
            totalCost += result.Cost;

            // AC1: one fresh call answers a schema violation; a second refusal is final and the caller sees no content.
            if (status == HealingLlmStatuses.SchemaViolation && attempt < HealingLlmRequestRules.MaxAttempts)
            {
                continue;
            }

            return new HealingLlmInvocation(
                new HealingLlmResult(
                    status,
                    status == HealingLlmStatuses.Ok ? content : null,
                    (int)totalTokens,
                    totalCost),
                callId);
        }
    }
}
