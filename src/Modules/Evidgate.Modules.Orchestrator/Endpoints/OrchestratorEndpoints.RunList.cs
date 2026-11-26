using System.Globalization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Orchestrator.Application.Queries;
using Evidgate.Modules.Orchestrator.Application.Runs;
using Evidgate.Modules.Orchestrator.Contracts;
using Mediator;
using Microsoft.AspNetCore.Http;

namespace Evidgate.Modules.Orchestrator.Endpoints;

public static partial class OrchestratorEndpoints
{
    /// <summary>
    /// <c>GET /v1/projects/{p}/runs</c>: the project's runs in <c>(created_at, id)</c> order, or newest first with
    /// <c>order=desc</c>, narrowed by identity, state, decision, rerun and current-revision completeness filters.
    /// The delegate only binds and validates the query parameters; the handler owns the project check, the tenant
    /// scope and the keyset page. An unknown query parameter is ignored. A present filter value that is invalid
    /// (blank included) is <c>422 request_invalid</c> with <c>details[].path</c> = the parameter and reason
    /// <c>invalid_value</c>; a blank <c>cursor</c> is treated as absent and <c>limit</c> has its own range error.
    /// </summary>
    private static async Task<Result<RunPage>> ListRunsAsync(
        Guid p,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        if (LimitError(context.Request.Query["limit"], out var limit) is { } error)
        {
            return Result.Failure<RunPage>(error);
        }

        var query = context.Request.Query;

        string? refName = null;
        if (Present(query, "ref_name", out var rawRefName))
        {
            if (!RunIdentityRules.IsStorableName(rawRefName))
            {
                return Result.Failure<RunPage>(InvalidFilter("ref_name"));
            }

            refName = rawRefName;
        }

        int? prNumber = null;
        if (Present(query, "pr_number", out var rawPrNumber))
        {
            if (!int.TryParse(rawPrNumber, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < 1)
            {
                return Result.Failure<RunPage>(InvalidFilter("pr_number"));
            }

            prNumber = parsed;
        }

        string? headSha = null;
        if (Present(query, "head_sha", out var rawHeadSha))
        {
            if (!RunIdentityRules.IsSha(rawHeadSha))
            {
                return Result.Failure<RunPage>(InvalidFilter("head_sha"));
            }

            headSha = rawHeadSha;
        }

        string? triggerEvent = null;
        if (Present(query, "trigger_event", out var rawTriggerEvent))
        {
            if (!RunIdentityRules.TriggerEvents.Contains(rawTriggerEvent, StringComparer.Ordinal))
            {
                return Result.Failure<RunPage>(InvalidFilter("trigger_event"));
            }

            triggerEvent = rawTriggerEvent;
        }

        bool? isFork = null;
        if (Present(query, "is_fork", out var rawIsFork))
        {
            if (rawIsFork is not ("true" or "false"))
            {
                return Result.Failure<RunPage>(InvalidFilter("is_fork"));
            }

            isFork = rawIsFork == "true";
        }

        // Each value is checked against the closed vocabulary its column admits, so a present-but-invalid value is
        // a 422 naming the parameter and never a silently narrowed (or widened) page. `decision` is a lower-snake
        // filter token the handler maps to the stored verdict; `parent_run_id` binds to a Guid, so only this
        // binding can refuse it.
        string? state = null;
        if (Present(query, "state", out var rawState))
        {
            if (!RunListFilterRules.IsKnownState(rawState))
            {
                return Result.Failure<RunPage>(InvalidFilter("state"));
            }

            state = rawState;
        }

        string? decision = null;
        if (Present(query, "decision", out var rawDecision))
        {
            if (!RunListFilterRules.Decisions.Contains(rawDecision, StringComparer.Ordinal))
            {
                return Result.Failure<RunPage>(InvalidFilter("decision"));
            }

            decision = rawDecision;
        }

        string? rerunScope = null;
        if (Present(query, "rerun_scope", out var rawRerunScope))
        {
            if (!RunListFilterRules.RerunScopes.Contains(rawRerunScope, StringComparer.Ordinal))
            {
                return Result.Failure<RunPage>(InvalidFilter("rerun_scope"));
            }

            rerunScope = rawRerunScope;
        }

        Guid? parentRunId = null;
        if (Present(query, "parent_run_id", out var rawParentRunId))
        {
            if (!Guid.TryParse(rawParentRunId, out var parsedParentRunId))
            {
                return Result.Failure<RunPage>(InvalidFilter("parent_run_id"));
            }

            parentRunId = parsedParentRunId;
        }

        if (RunListQueryBinding.Bind(query, out var order, out var completeness) is { } bindError)
        {
            return Result.Failure<RunPage>(bindError);
        }

        return await mediator.Send(
            new ListRunsQuery(
                p,
                new RunListFilters(
                    refName, prNumber, headSha, triggerEvent, isFork, state, decision, rerunScope, parentRunId, completeness),
                Blank(query["cursor"]),
                limit,
                order),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>True when the query carries <paramref name="name"/> at least once; <paramref name="value"/> is then its first value (possibly blank).</summary>
    private static bool Present(IQueryCollection query, string name, out string value)
    {
        if (query.TryGetValue(name, out var values) && values.Count > 0)
        {
            value = values[0] ?? string.Empty;
            return true;
        }

        value = string.Empty;
        return false;
    }

    /// <summary>422 for a filter value its column cannot hold, naming the parameter.</summary>
    private static Error InvalidFilter(string name) =>
        Error.Invalid($"{name} has an invalid value", [new ErrorDetail(name, "invalid_value")]);
}
