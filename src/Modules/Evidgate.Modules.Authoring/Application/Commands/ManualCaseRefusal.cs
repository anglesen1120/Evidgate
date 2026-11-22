using System.Text.Json.Nodes;
using Evidgate.BuildingBlocks.Contracts.Validation;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Messaging;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Contracts;
using Evidgate.Modules.Authoring.Infrastructure.Persistence;
using Mediator;
using Microsoft.AspNetCore.Http;

namespace Evidgate.Modules.Authoring.Application.Commands;

/// <summary>The writer's refusal as the 00 §9.2 error the pipeline renders, in the module's application layer.</summary>
internal static class ManualCaseRefusal
{
    /// <summary>Maps a writer refusal to its <see cref="Error"/>.</summary>
    /// <remarks>
    /// The writer reports one change's members under its own batch envelope (<c>/changes/0/content/checklist_item_id</c>)
    /// because it accepts several changes; every manual-case route of 13.1.1 §5 carries one member named exactly like the
    /// stored column, so <see cref="AuthoringErrors.RequestFacing"/> drops the prefix. Paths that are already the route's
    /// own (<c>/reason</c> of AC21, <c>/checklist_item_id</c> of AC22) pass through unchanged.
    /// </remarks>
    /// <param name="refusal">The refusal.</param>
    internal static Error ToError(AuthoringRefusal refusal)
    {
        ArgumentNullException.ThrowIfNull(refusal);

        return AuthoringErrors.RequestFacing(new Error(
            refusal.Code,
            refusal.Message,
            refusal.Status,
            Retryable: refusal.Status == StatusCodes.Status503ServiceUnavailable,
            Details: refusal.Details is { Count: > 0 } details
                ? [.. details.Select(detail => new ErrorDetail(detail.Path, detail.Reason))]
                : null));
    }
}
