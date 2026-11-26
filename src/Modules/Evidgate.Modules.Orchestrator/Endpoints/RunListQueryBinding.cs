using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Orchestrator.Application.Queries;
using Microsoft.AspNetCore.Http;

namespace Evidgate.Modules.Orchestrator.Endpoints;

/// <summary>
/// Binds the run list's <c>order</c> and <c>completeness</c> query parameters. Both are single-valued closed
/// enums: a blank, unknown, differently cased or repeated value is <c>422 request_invalid</c> naming the parameter,
/// because a repeated value would otherwise silently narrow the page to whichever occurrence came first.
/// </summary>
internal static class RunListQueryBinding
{
    /// <summary>Null when both parameters are absent or valid; otherwise the 422 for the first invalid one.</summary>
    internal static Error? Bind(IQueryCollection query, out RunListOrder order, out string? completeness)
    {
        order = RunListOrder.Ascending;
        completeness = null;

        if (query.TryGetValue("order", out var orderValues))
        {
            if (orderValues.Count != 1 || !RunListFilterRules.TryParseOrder(orderValues[0] ?? string.Empty, out order))
            {
                return Invalid("order");
            }
        }

        if (query.TryGetValue("completeness", out var completenessValues))
        {
            var raw = completenessValues.Count == 1 ? completenessValues[0] : null;
            if (raw is null || !RunListFilterRules.Completenesses.Contains(raw, StringComparer.Ordinal))
            {
                return Invalid("completeness");
            }

            completeness = raw;
        }

        return null;
    }

    private static Error Invalid(string name) =>
        Error.Invalid($"{name} has an invalid value", [new ErrorDetail(name, RunListFilterRules.InvalidValue)]);
}
