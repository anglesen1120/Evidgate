using Evidgate.Modules.Policy.Application.Evaluation;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The stored row as the matrix reads it back, and the projection that makes two rows comparable: the columns a
/// recomputation must reproduce, arrays joined so equality is structural and the id, mode and instant stay out of it.
/// </summary>
public sealed partial class EvaluationTaskTests
{
    /// <summary>One stored evaluation's deciding columns and both content addresses, read from the committed row.</summary>
    private sealed record StoredRow(
        Guid Id,
        string Completeness,
        string[] Missing,
        string Outcome,
        string Decision,
        string[] Reasons,
        string PolicyHash,
        string ManifestHash,
        string EvaluatorVersion,
        string Mode,
        string ExceptionsHash,
        DateTime ExceptionsAsOf)
    {
        /// <summary>The columns a recomputation must reproduce, arrays joined so equality is structural.</summary>
        public Deciding Deciding => new(
            Completeness,
            string.Join(",", Missing),
            Outcome,
            Decision,
            string.Join(",", Reasons),
            PolicyHash,
            ManifestHash,
            EvaluatorVersion,
            ExceptionsHash);
    }

    /// <summary>The stored columns that are a function of the inputs alone: no id, mode or timestamp.</summary>
    private sealed record Deciding(
        string Completeness,
        string Missing,
        string Outcome,
        string Decision,
        string Reasons,
        string PolicyHash,
        string ManifestHash,
        string EvaluatorVersion,
        string ExceptionsHash);

    private static Deciding DecidingOf(EvaluationRecord record) => new(
        record.Completeness,
        string.Join(",", record.Missing),
        record.Outcome,
        record.Decision,
        string.Join(",", record.Reasons),
        record.PolicyHash,
        record.ManifestHash,
        record.EvaluatorVersion,
        record.ExceptionsHash);
}
