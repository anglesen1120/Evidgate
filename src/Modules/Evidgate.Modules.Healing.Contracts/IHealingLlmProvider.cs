using System.Globalization;
using System.Text;

namespace Evidgate.Modules.Healing.Contracts;

/// <summary>
/// 08.5.1-T1: the closed <c>healing_llm_call.status</c> vocabulary (migration 0054's CHECK). The gateway records
/// exactly these values, so the constants and the DDL cannot drift: a value that is not here is not storable.
/// </summary>
public static class HealingLlmStatuses
{
    /// <summary>The provider answered and the declared schema accepted the answer.</summary>
    public const string Ok = "ok";

    /// <summary>The per-call time budget was exhausted (AC2).</summary>
    public const string Timeout = "timeout";

    /// <summary>The answer failed the caller-declared JSON Schema on both attempts (AC1).</summary>
    public const string SchemaViolation = "llm_schema_violation";

    /// <summary>The period budget of <c>healing_llm_budget</c> was spent, so no call was placed (AC3).</summary>
    public const string BudgetExceeded = "budget_exceeded";

    /// <summary>The provider could not answer (transport failure, non-success status, unreadable body).</summary>
    public const string Error = "error";

    /// <summary>
    /// Whether <paramref name="status"/> is one of this closed vocabulary — the only values the DDL accepts. The verdict
    /// lives next to the constants so a new status cannot be storable in SQL and rejected here, or the reverse.
    /// </summary>
    /// <param name="status">A status, as a provider or the gateway reported it.</param>
    /// <returns><see langword="true"/> when the value may be written to <c>healing_llm_call.status</c>.</returns>
    public static bool IsKnown(string? status) =>
        status is Ok or Timeout or SchemaViolation or BudgetExceeded or Error;
}

/// <summary>
/// 08.5.1-T1: the closed <c>healing_llm_call.purpose</c> vocabulary (migration 0054's CHECK) and the attribution rule
/// that goes with it: an <see cref="AuthoringGeneration"/> call serves a project generation and names no incident,
/// every other purpose serves an incident, and a candidate may only be named alongside its incident (AC4, AC7).
/// </summary>
public static class HealingLlmPurposes
{
    /// <summary>A project generation (13.3.1-T2): <c>project_id</c> set, <c>incident_id</c>/<c>candidate_id</c> null.</summary>
    public const string AuthoringGeneration = "authoring_generation";

    /// <summary>A triage hypothesis (08.2.2): the incident is always named, a candidate usually is not yet.</summary>
    public const string TriageHypothesis = "triage_hypothesis";

    /// <summary>A candidate generation (08.5.4): incident and candidate both named.</summary>
    public const string CandidateGeneration = "candidate_generation";
}

/// <summary>
/// 08.5.1-T1: one gateway call, as the caller hands it to <see cref="IHealingLlmProvider"/>.
/// </summary>
/// <param name="ProjectId">
/// The project the call is attributed to and costed against. Every row of <c>healing_llm_call</c> carries it (AC4).
/// </param>
/// <param name="Purpose">One of <see cref="HealingLlmPurposes"/>; it decides whether an incident is required.</param>
/// <param name="IncidentId">
/// The incident the call serves, for every purpose except <see cref="HealingLlmPurposes.AuthoringGeneration"/>.
/// </param>
/// <param name="CandidateId">
/// The candidate the call serves, when one is already named; only ever set together with
/// <paramref name="IncidentId"/>.
/// </param>
/// <param name="Model">The model the adapter is asked for; it is recorded on the row, never derived after the fact.</param>
/// <param name="PromptVersion">The prompt template version, so output lineage survives a prompt edit (08.7.2).</param>
/// <param name="Payload">
/// The prompt body, <em>already redacted and opt-in approved</em> by the caller. 08.5.1 assumes this and never runs the
/// redaction pipeline itself (08.5.3 owns it); the gateway, the recorder and the adapters must never log it, and no
/// column of <c>healing_llm_call</c> can hold it (AC6).
/// </param>
/// <param name="ResponseSchema">
/// The JSON Schema the answer must satisfy, or null when the caller accepts any JSON answer. It is validated by the
/// gateway before the answer is returned; a schema that does not compile is refused before any provider call.
/// </param>
public sealed record HealingLlmRequest(
    Guid ProjectId,
    string Purpose,
    Guid? IncidentId,
    Guid? CandidateId,
    string Model,
    string PromptVersion,
    string Payload,
    string? ResponseSchema)
{
    /// <summary>
    /// The record's textual form without the two members that carry text (AC6). A request can reach a log line, an
    /// exception message or a debugger through <see cref="object.ToString"/> alone, so the prompt body and the declared
    /// schema are reported as their lengths and never as their content. <see cref="object.ToString"/> is built from this
    /// method, so overriding it here redacts every form the record prints in.
    /// </summary>
    /// <param name="builder">The builder the record's text is written into.</param>
    /// <returns><see langword="true"/>, because this method always writes members.</returns>
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("ProjectId = ").Append(ProjectId);
        builder.Append(", Purpose = ").Append(Purpose);
        builder.Append(", IncidentId = ").Append(IncidentId);
        builder.Append(", CandidateId = ").Append(CandidateId);
        builder.Append(", Model = ").Append(Model);
        builder.Append(", PromptVersion = ").Append(PromptVersion);
        builder.Append(", Payload = ").Append(Describe(Payload));
        builder.Append(", ResponseSchema = ").Append(Describe(ResponseSchema));
        return true;
    }

    /// <summary>The length of a text member, or <c>null</c>, so the member's own text never leaves the request.</summary>
    /// <param name="text">The member's value.</param>
    private static string Describe(string? text) =>
        text is null ? "null" : string.Concat(text.Length.ToString(CultureInfo.InvariantCulture), " chars");
}

/// <summary>
/// 08.5.1-T1: one provider answer, before the gateway's schema verdict.
/// </summary>
/// <param name="Status">One of <see cref="HealingLlmStatuses"/>.</param>
/// <param name="Content">The answer body when <paramref name="Status"/> is <see cref="HealingLlmStatuses.Ok"/>.</param>
/// <param name="Tokens">Tokens the provider reports having spent on this attempt.</param>
/// <param name="Cost">Cost the provider reports for this attempt, in the ledger's currency unit.</param>
public sealed record HealingLlmResult(string Status, string? Content, int Tokens, decimal Cost);

/// <summary>
/// 08.5.1-T1: the provider adapter seam (00 §5.5). The gateway owns schema validation, retry, recording and fail-closed
/// behaviour; an adapter owns transport and provider-specific mapping only, and must never log or persist the payload.
/// </summary>
/// <remarks>
/// Redaction seam: <see cref="HealingLlmRequest.Payload"/> is accepted already redacted and opt-in approved. The gateway
/// does not redact, and the redaction pipeline is the caller's step before it builds the request. A later redaction
/// stage hooks in at that call site (before <see cref="HealingLlmRequest"/> is constructed), so no adapter ever sees an
/// unredacted prompt and no adapter changes when it lands.
/// </remarks>
public interface IHealingLlmProvider
{
    /// <summary>Invokes the provider once for <paramref name="request"/>.</summary>
    /// <param name="request">The call, with a payload the caller has already redacted.</param>
    /// <param name="cancellationToken">Cancellation of the call.</param>
    /// <returns>
    /// The answer, or one of the failure statuses — an adapter maps its own transport failure, time budget and non-success
    /// status into <see cref="HealingLlmStatuses"/> instead of throwing them, so every attempt can be recorded.
    /// </returns>
    Task<HealingLlmResult> InvokeAsync(HealingLlmRequest request, CancellationToken cancellationToken);
}
