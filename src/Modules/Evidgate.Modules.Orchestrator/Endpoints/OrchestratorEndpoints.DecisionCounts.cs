using System.Globalization;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Orchestrator.Application.Queries;
using Evidgate.Modules.Orchestrator.Contracts;
using Mediator;
using Microsoft.AspNetCore.Http;

namespace Evidgate.Modules.Orchestrator.Endpoints;

public static partial class OrchestratorEndpoints
{
    /// <summary>
    /// <c>GET /v1/projects/{p}/runs/decision-counts</c>: the project's runs from <c>since</c> on, counted by their
    /// newest live verdict. The delegate only binds <c>since</c> (an RFC 3339 date-time; a present value that does not
    /// parse, blank included, is <c>422 request_invalid</c> naming the parameter); the handler owns the project
    /// check, the permissions and the bounded scan.
    /// </summary>
    private static async Task<Result<RunDecisionCounts>> ListRunDecisionCountsAsync(
        Guid p,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        DateTimeOffset? since = null;
        if (Present(context.Request.Query, "since", out var rawSince))
        {
            if (!DateTimeOffset.TryParse(
                    rawSince,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var parsed))
            {
                return Result.Failure<RunDecisionCounts>(InvalidFilter("since"));
            }

            since = parsed;
        }

        return await mediator.Send(new ListRunDecisionCountsQuery(p, since), cancellationToken).ConfigureAwait(false);
    }
}
