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
/// 08.5.1-T1: one gateway call's outcome. The gateway records every attempt and returns the last one, together with the
/// id of that row — the value a generation stores as <c>authoring_version.ai_run_id</c> so the artefact it produced is
/// attributable and costed (AC7).
/// </summary>
/// <param name="Result">
/// The accepted answer, or the failing status with no content: a refused answer never leaves the gateway (AC1).
/// </param>
/// <param name="CallId">The <c>healing_llm_call.id</c> of the attempt this outcome reports.</param>
internal sealed record HealingLlmInvocation(HealingLlmResult Result, Guid CallId);

/// <summary>
/// 08.5.1-T1: the provider-adapter gateway. It invokes the adapter, validates the answer against the caller-declared JSON
/// Schema, retries a schema violation exactly once (AC1), fails closed, and records one row per attempt in the ambient
/// tenant transaction (AC4, AC7). It never opens a transaction of its own: <c>P5</c> puts the provider call outside the
/// database work of its callers, and the recorder then writes inside whichever tenant transaction the caller has open.
/// </summary>
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

        for (var attempt = 1; ; attempt++)
        {
            var result = await provider.InvokeAsync(request, cancellationToken).ConfigureAwait(false);

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

            // AC1: one fresh call answers a schema violation; a second refusal is final and the caller sees no content.
            if (status == HealingLlmStatuses.SchemaViolation && attempt < HealingLlmRequestRules.MaxAttempts)
            {
                continue;
            }

            return new HealingLlmInvocation(
                new HealingLlmResult(status, status == HealingLlmStatuses.Ok ? content : null, result.Tokens, result.Cost),
                callId);
        }
    }
}
