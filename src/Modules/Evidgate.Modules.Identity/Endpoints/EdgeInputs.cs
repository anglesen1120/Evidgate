using System.Globalization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Microsoft.AspNetCore.Http;

namespace Evidgate.Modules.Identity.Endpoints;

/// <summary>
/// The 00 §9.1 inputs both endpoint classes bind at the edge: the <c>?limit=</c>
/// bound, the <c>cursor</c> parameter and the <c>Idempotency-Key</c> header. They
/// live in one place so the two route classes cannot drift into two spellings of
/// the same rule.
/// </summary>
internal static class EdgeInputs
{
    /// <summary>00 §9.1: <c>?limit=</c> defaults to 50 and is capped at 200.</summary>
    internal const int DefaultLimit = 50;

    /// <summary>00 §9.1: the largest page a client may ask for.</summary>
    internal const int MaxLimit = 200;

    /// <summary>The client's <c>Idempotency-Key</c> (00 §9.1); the C2 idempotency stage decides what a missing one means.</summary>
    internal static string IdempotencyKey(HttpContext context) =>
        context.Request.Headers["Idempotency-Key"].ToString();

    /// <summary>The raw 00 §9.1 cursor parameter, or null when absent or empty.</summary>
    internal static string? Cursor(HttpContext context)
    {
        var value = context.Request.Query["cursor"].ToString();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>
    /// Validates <c>?limit=</c> against 00 §9.1 at the edge — a binding concern, like
    /// reading a body (the skeleton slice's pattern), so an out-of-range page size
    /// never opens a transaction. Null means "valid"; the default 50 applies when the
    /// parameter is absent.
    /// </summary>
    internal static Error? LimitError(string? raw, out int limit)
    {
        limit = DefaultLimit;

        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            || parsed < 1
            || parsed > MaxLimit)
        {
            return Error.Invalid(
                string.Create(CultureInfo.InvariantCulture, $"limit must be an integer between 1 and {MaxLimit} (00 §9.1)"),
                [new ErrorDetail("limit", "out_of_range")]);
        }

        limit = parsed;
        return null;
    }
}
