using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Messaging;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Domain;
using Evidgate.Modules.Authoring.Infrastructure.Persistence;
using FluentValidation;
using Mediator;
using Npgsql;

namespace Evidgate.Modules.Authoring.Application.Commands;

/// <summary>
/// The limits of one sibling group (AC2). The reorder's list cap and the size a group may grow to live here together, so
/// a group a caller can build is exactly a group they can renumber (review F5/perf F9: the reorder refused more than
/// <see cref="ReorderNodesCommandValidator.MaxOrderedIds"/> ids while node create had no cap at all, which left a group
/// past the cap that no renumber could ever order).
/// </summary>
internal static class NodeLimits
{
    /// <summary>
    /// The most live siblings one parent group may hold — the reorder validator's list cap, because the two-phase renumber
    /// takes the whole set in one request. A create or a move into a full group is refused with
    /// <see cref="Refusal"/>: <c>422 request_invalid</c>, <c>details[].path = /parent_id</c>,
    /// <c>details[].reason = children_limit_exceeded</c>.
    /// </summary>
    internal const int MaximumSiblingsPerGroup = 1000;

    /// <summary>The <c>422</c> of a group that already holds <see cref="MaximumSiblingsPerGroup"/> live siblings.</summary>
    internal static Error Refusal() => AuthoringErrors.Invalid(
        "/parent_id",
        "children_limit_exceeded",
        "a sibling group holds at most "
            + MaximumSiblingsPerGroup.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + " nodes; renumber or move some of them first");
}
