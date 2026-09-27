using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Infrastructure.Persistence;

namespace Evidgate.Modules.Identity.Application.Commands;

/// <summary>
/// The shared write-time validation of a submitted permission set (02.2.1 §5,
/// T79): every code must be a catalogue code, and every code must be
/// <c>tenant_assignable</c> — a custom role is a member role, and a member
/// principal must never hold <c>billing:*</c>, <c>support:*</c>, <c>platform:*</c>
/// or an agent-only code (TL T79). Both commands call it before they touch a row,
/// so a rejected set writes nothing.
/// </summary>
internal static class RolePermissionCodes
{
    /// <summary>
    /// The first error the catalogue raises for <paramref name="requested"/>, or
    /// null when every code is known and assignable. Unknown codes are reported
    /// first and alone: a code the catalogue does not carry has no assignability to
    /// report, and the client's fix is the same for all of them.
    /// </summary>
    internal static async Task<Error?> ValidateAsync(
        ITenantDb db,
        IReadOnlyList<string> requested,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(requested);

        if (requested.Count == 0)
        {
            return null;
        }

        var flags = await db.QueryAsync<CatalogueFlagRow>(
            IdentityAccessSql.CatalogueFlags,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var requestedSet = requested.ToHashSet(StringComparer.Ordinal);
        var known = flags.Select(flag => flag.Code).ToHashSet(StringComparer.Ordinal);
        var unknown = requested
            .Where(code => !known.Contains(code))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (unknown.Count > 0)
        {
            return Error.Unprocessable(
                ApiErrorCode.UnknownPermission,
                "the permission set contains codes the catalogue does not declare",
                Details(requested, unknown, "unknown_permission"));
        }

        // Read-time half of T79 lives in the evaluator's SQL; this is the
        // write-time half, and the two must agree (02.2.1 §8 "T79 assignability").
        // Only the submitted codes matter here — the catalogue carries codes no
        // member role may hold that the request simply does not mention.
        var notAssignable = flags
            .Where(flag => !flag.TenantAssignable && requestedSet.Contains(flag.Code))
            .Select(flag => flag.Code)
            .ToList();

        if (notAssignable.Count > 0)
        {
            return Error.Unprocessable(
                ApiErrorCode.PermissionNotAssignable,
                "a member role cannot hold a code that is not tenant-assignable (T79)",
                Details(requested, notAssignable, "permission_not_assignable"));
        }

        return null;
    }

    /// <summary>
    /// One 00 §9.2 detail per offending code, carrying its position in the submitted
    /// list so the client can point at the field it sent.
    /// </summary>
    private static List<ErrorDetail> Details(
        IReadOnlyList<string> requested,
        List<string> offenders,
        string reason)
    {
        var details = new List<ErrorDetail>(offenders.Count);

        foreach (var offender in offenders)
        {
            for (var index = 0; index < requested.Count; index++)
            {
                if (string.Equals(requested[index], offender, StringComparison.Ordinal))
                {
                    details.Add(new ErrorDetail("permissions", reason, index));
                    break;
                }
            }
        }

        return details;
    }
}
