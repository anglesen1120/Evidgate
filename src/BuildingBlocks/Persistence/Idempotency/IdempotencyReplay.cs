namespace Evidgate.BuildingBlocks.Persistence.Idempotency;

/// <summary>
/// The stored response of an already completed command: the status and JSON body
/// the original request returned, replayed verbatim (00 §9.1).
/// <para>
/// The members are named after the two columns they are projected from, because
/// Dapper binds a positional record by parameter name (underscore-folded) and not
/// by position alone.
/// </para>
/// </summary>
/// <param name="ResponseStatus">The HTTP status the original command returned.</param>
/// <param name="ResponseBody">The JSON body the original command returned.</param>
public sealed record IdempotencyReplay(int ResponseStatus, string ResponseBody);
