using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Evidence.Domain;
using Evidgate.Modules.Evidence.Infrastructure.Persistence;

namespace Evidgate.Modules.Evidence.Application.Capture;

/// <summary>
/// The one read of a project's capture options, shared by the route that serves them and the contract reader another
/// module calls. It runs one statement whatever the number of kinds, so the switch and the rules it answers beside it
/// come from the same snapshot, and the effective capture switch always comes from
/// <see cref="ArtifactKindDefaults.Resolve"/>, so a kind's answer here can never disagree with the upload route's
/// <c>kind_disabled</c> gate.
/// </summary>
internal static class CaptureOptionsRead
{
    /// <summary>Every contract kind, in contract order, with its effective switch and declared rules.</summary>
    /// <param name="db">The Dapper path over the ambient scope; it demands an active scope before it touches the database.</param>
    /// <param name="orgId">The caller's trusted tenant id.</param>
    /// <param name="projectId">The project whose options are read.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal static async ValueTask<IReadOnlyList<CaptureOptionsKind>> LoadAsync(
        ITenantDb db,
        Guid orgId,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var rows = await db.QueryAsync<OptionRow>(
            CaptureOptionsSql.LoadOptions,
            new { org_id = orgId, project_id = projectId },
            cancellationToken).ConfigureAwait(false);

        var byKind = new Dictionary<string, KindPolicyRow>(StringComparer.Ordinal);
        var rulesByKind = new Dictionary<string, List<RedactionRule>>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            if (string.Equals(row.RowKind, CaptureOptionsSql.PolicyRowKind, StringComparison.Ordinal))
            {
                if (row is { MaxBytes: { } maxBytes, MediaTypes: { } mediaTypes, CaptureEnabled: { } captureEnabled, DefaultSensitivity: { } sensitivity })
                {
                    byKind[row.Kind] = new KindPolicyRow(maxBytes, mediaTypes, captureEnabled, sensitivity);
                }

                continue;
            }

            if (row is { RuleType: { } ruleType, NamePattern: { } namePattern })
            {
                if (!rulesByKind.TryGetValue(row.Kind, out var declared))
                {
                    declared = [];
                    rulesByKind.Add(row.Kind, declared);
                }

                declared.Add(new RedactionRule(ruleType, namePattern));
            }
        }

        return
        [
            .. ArtifactKinds.All.Select(kind => new CaptureOptionsKind(
                kind,
                ArtifactKindDefaults.Resolve(kind, byKind.TryGetValue(kind, out var row) ? row : null)?.CaptureEnabled
                    ?? false,
                rulesByKind.TryGetValue(kind, out var declared) ? declared : [])),
        ];
    }

    /// <summary>
    /// One row of the union: a policy row fills the five policy members and leaves the rule pair null, a rule row fills
    /// the pair and leaves the policy members null. Every policy column is NOT NULL in the table, which is why the
    /// consumer only has to prove the shape the branch it matched promises.
    /// </summary>
    private sealed record OptionRow(
        string RowKind,
        string Kind,
        long? MaxBytes,
        string[]? MediaTypes,
        bool? CaptureEnabled,
        string? DefaultSensitivity,
        string? RuleType,
        string? NamePattern);
}
