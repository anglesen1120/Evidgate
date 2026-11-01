using System.Globalization;
using System.Text.Json;
using Evidgate.BuildingBlocks.Canonicalization;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Policy.Infrastructure.Persistence;

namespace Evidgate.Modules.Policy.Application.Evaluation;

/// <summary>The waivers and quarantine entries in force at one instant, with the content address of that set.</summary>
/// <param name="AsOf">The instant the set was read at.</param>
/// <param name="WaiverIds">Active waiver ids, sorted.</param>
/// <param name="QuarantineIds">Active quarantine entry ids, sorted.</param>
/// <param name="Hash">The <c>sha256:</c> digest of the canonical JSON of both sorted id lists.</param>
internal sealed record ExceptionsSnapshot(
    DateTimeOffset AsOf,
    IReadOnlyList<Guid> WaiverIds,
    IReadOnlyList<Guid> QuarantineIds,
    string Hash);

/// <summary>
/// Resolves a project's exception set at an instant the caller captured once. The hash covers the sorted ids of both
/// kinds, so the same set always yields the same digest and a different set never does.
/// </summary>
internal sealed class ExceptionsSnapshotResolver(ITenantDb db, ITenantScopeAccessor scopes)
{
    private sealed record ActiveRow(string Kind, Guid Id);

    /// <summary>Reads the active ids of <paramref name="projectId"/> at <paramref name="asOf"/> and hashes them.</summary>
    internal async Task<ExceptionsSnapshot> ResolveAsync(
        Guid projectId,
        DateTimeOffset asOf,
        CancellationToken cancellationToken)
    {
        var rows = await db.QueryAsync<ActiveRow>(
            ExceptionsSnapshotSql.LoadActive,
            new { org_id = scopes.Require().OrgId, project_id = projectId, as_of = asOf },
            cancellationToken).ConfigureAwait(false);

        var waivers = Sorted(rows, "waiver");
        var quarantines = Sorted(rows, "quarantine");
        return new ExceptionsSnapshot(asOf, waivers, quarantines, HashOf(waivers, quarantines));
    }

    /// <summary>The digest of the canonical JSON <c>{"quarantine_ids":[…],"waiver_ids":[…]}</c>.</summary>
    internal static string HashOf(IReadOnlyList<Guid> waiverIds, IReadOnlyList<Guid> quarantineIds) =>
        CanonicalHash.Compute(JsonSerializer.Serialize(new
        {
            waiver_ids = waiverIds.Select(Text),
            quarantine_ids = quarantineIds.Select(Text),
        }));

    // Sorted by the text the hash carries, so the order never depends on Guid's byte comparison or the row order.
    private static Guid[] Sorted(IEnumerable<ActiveRow> rows, string kind) =>
        [.. rows.Where(row => row.Kind == kind).Select(row => row.Id).OrderBy(Text, StringComparer.Ordinal)];

    private static string Text(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);
}
