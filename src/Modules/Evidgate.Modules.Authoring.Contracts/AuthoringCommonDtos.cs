namespace Evidgate.Modules.Authoring.Contracts;

/// <summary>
/// The optional body of a <c>DELETE</c> on an authoring entity (13.1.1 §5 routes 5, 10, 16): the reason recorded on the
/// <c>delete</c> version row. Published as the <c>AuthoringDeleteRequest</c> schema of <c>contracts/openapi.yaml</c>.
/// </summary>
/// <param name="Reason">Why the entity is removed, 1..500 characters, or null.</param>
public sealed record DeleteAuthoringRequest(string? Reason);

/// <summary>
/// The answer of a <c>PATCH</c> on an authoring entity (13.1.1 §5 routes 3, 9, 15): the entity's <c>version</c> after the
/// call and whether the call changed anything. A no-op writes no version row and returns the unchanged version (AC5).
/// Published as the <c>AuthoringChange</c> schema of <c>contracts/openapi.yaml</c>.
/// </summary>
/// <param name="Version">The entity's version after the call.</param>
/// <param name="Changed">False when the request equalled the stored content.</param>
public sealed record AuthoringChangeResponse(int Version, bool Changed);
