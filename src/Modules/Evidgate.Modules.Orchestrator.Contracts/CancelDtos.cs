namespace Evidgate.Modules.Orchestrator.Contracts;

/// <summary>
/// The optional body of <c>POST /v1/runs/{r}/cancel</c> (05.4.1 §5): one member, <c>reason</c> (the host's
/// <c>SnakeCaseLower</c> policy), the caller's business context for the cancel. It is never personal data and never
/// required: an absent body and <c>{"reason": null}</c> mean the same thing.
/// <para>
/// The type documents the wire shape for the served OpenAPI document and is declared on the route with
/// <c>Accepts</c>; the cancel handler parses the raw body itself (it must, to bound the member set, the trimmed length
/// and the idempotency hash), so this record is never deserialized.
/// </para>
/// </summary>
/// <param name="Reason">
/// Free text: trimmed 1..1000 characters when present. A blank string is 422 <c>request_invalid</c>
/// (<c>details[].path</c> <c>/reason</c>), because "no reason" already has its own spelling.
/// </param>
public sealed record CancelRunRequest(string? Reason);
