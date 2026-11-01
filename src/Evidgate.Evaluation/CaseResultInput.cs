namespace Evidgate.Evaluation;

/// <summary>One planned execution's normalized result, as the caller read it from the run's attempts.</summary>
/// <param name="CaseId">The catalog case id, which is what the policy's critical list names.</param>
/// <param name="CaseKey">The case key, which is what the policy's required and allowed-skip lists name.</param>
/// <param name="SuiteKey">The suite key the execution belongs to.</param>
/// <param name="Status">The normalized terminal status of the execution.</param>
/// <param name="Required">
/// The manifest's own <c>required</c> flag for the execution. It decides the required scope only when the policy
/// names no required case or suite; a policy-named case is required whatever the manifest said.
/// </param>
public sealed record CaseResultInput(
    Guid CaseId,
    string CaseKey,
    string SuiteKey,
    CaseStatus Status,
    bool Required = true);
