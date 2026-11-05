using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Evidence.Domain;
using Evidgate.Modules.Evidence.Infrastructure.Persistence;

namespace Evidgate.Modules.Evidence.Application.Capture;

/// <summary>
/// The one read of a project's capture options, shared by the route that serves them and the contract reader another
/// module calls at run create. Two statements whatever the number of kinds, and the effective capture switch always comes
/// from <see cref="ArtifactKindDefaults.Resolve"/>, so a kind's answer here can never disagree with the upload route's
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
        var parameters = new { org_id = orgId, project_id = projectId };

        var policies = await db.QueryAsync<PolicyRow>(CaptureOptionsSql.LoadPolicies, parameters, cancellationToken)
            .ConfigureAwait(false);
        var rules = await db.QueryAsync<RuleRow>(CaptureOptionsSql.LoadRules, parameters, cancellationToken)
            .ConfigureAwait(false);

        var byKind = new Dictionary<string, KindPolicyRow>(StringComparer.Ordinal);
        foreach (var policy in policies)
        {
            byKind[policy.Kind] = new KindPolicyRow(
                policy.MaxBytes,
                policy.MediaTypes,
                policy.CaptureEnabled,
                policy.DefaultSensitivity);
        }

        var rulesByKind = new Dictionary<string, List<RedactionRule>>(StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            if (!rulesByKind.TryGetValue(rule.Kind, out var declared))
            {
                declared = [];
                rulesByKind.Add(rule.Kind, declared);
            }

            declared.Add(new RedactionRule(rule.RuleType, rule.NamePattern));
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

    /// <summary>One stored policy row, before it becomes the resolution's input.</summary>
    private sealed record PolicyRow(
        string Kind,
        long MaxBytes,
        string[] MediaTypes,
        bool CaptureEnabled,
        string DefaultSensitivity);

    /// <summary>One stored rule, before it becomes the contract record.</summary>
    private sealed record RuleRow(string Kind, string RuleType, string NamePattern);
}
