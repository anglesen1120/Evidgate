using System.Buffers;
using System.Text.Json;
using Evidgate.BuildingBlocks.Canonicalization;

namespace Evidgate.Modules.Healing.Application;

/// <summary>
/// One line of a redaction report: how many times a rule fired in one field. It names the rule and the place and never
/// the value, so a report can be stored, hashed and shown without becoming a second copy of the secret.
/// </summary>
/// <param name="RuleId">A rule id of <see cref="RedactionRules"/>, or <c>residual_*</c> for a hit of the second pass.</param>
/// <param name="FieldPath">Where in the payload: <c>preamble</c>, <c>segment:&lt;ordinal&gt;</c> (1-based, never the source id) or <c>payload</c>.</param>
/// <param name="Count">How many replacements (or residual hits) the rule made in that field; at least 1.</param>
internal sealed record RedactionFinding(string RuleId, string FieldPath, int Count);

/// <summary>
/// What the pipeline did to one payload, in a form that can be persisted and re-verified: the rule-catalogue version and
/// the findings in a fixed order. <see cref="Hash"/> is <c>sha256:</c> over the RFC 8785 canonical JSON of
/// <see cref="ToJson"/>, so the same findings always give the same hash and any edit to a stored report changes it.
/// </summary>
internal sealed record RedactionReport(string RuleVersion, IReadOnlyList<RedactionFinding> Findings)
{
    /// <summary>The report as JSON (the <c>healing_redaction_report.report</c> value); member order is irrelevant to the hash.</summary>
    internal string ToJson()
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("rule_version", RuleVersion);
            writer.WriteStartArray("findings");
            foreach (var finding in Findings)
            {
                writer.WriteStartObject();
                writer.WriteNumber("count", finding.Count);
                writer.WriteString("field_path", finding.FieldPath);
                writer.WriteString("rule_id", finding.RuleId);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>The <c>sha256:</c> digest of the canonical form of <see cref="ToJson"/>.</summary>
    internal string Hash => HashOf(ToJson());

    /// <summary>The digest of a stored report document; the same function the report's own <see cref="Hash"/> uses.</summary>
    /// <param name="reportJson">A report as <see cref="ToJson"/> wrote it, or as read back from the database.</param>
    internal static string HashOf(string reportJson) => CanonicalHash.Compute(reportJson);

    /// <summary>Whether <paramref name="reportJson"/> still has the digest recorded for it.</summary>
    /// <param name="reportJson">The stored report.</param>
    /// <param name="recordedHash">The stored <c>report_hash</c>.</param>
    internal static bool Matches(string reportJson, string recordedHash) =>
        string.Equals(HashOf(reportJson), recordedHash, StringComparison.Ordinal);

    /// <summary>Only counts are printed: a report is never a place a value could hide, but the habit is the same as for a payload.</summary>
    public override string ToString() => $"RedactionReport {{ RuleVersion = {RuleVersion}, Findings = {Findings.Count} }}";
}

/// <summary>
/// The result of <see cref="IRedactionPipeline.Redact"/>. When <see cref="Blocked"/> is set the second pass still found a
/// secret-shaped value, <see cref="Text"/> is empty and the caller must not send anything.
/// </summary>
/// <param name="Text">The masked payload; empty when blocked, so a caller that forgets to check cannot send the original.</param>
/// <param name="Report">The findings of both passes.</param>
/// <param name="Blocked">Whether the residual scan (or the cost budget) refused the payload.</param>
internal sealed record RedactionOutcome(string Text, RedactionReport Report, bool Blocked)
{
    /// <summary>The text is a prompt body: only its length is printed.</summary>
    public override string ToString() => $"RedactionOutcome {{ Text = {Text.Length} chars, Blocked = {Blocked}, {Report} }}";
}
