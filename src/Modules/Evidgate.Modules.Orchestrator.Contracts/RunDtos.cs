using System.Text.Json;

namespace Evidgate.Modules.Orchestrator.Contracts;

/// <summary>
/// The body of <c>POST /v1/projects/{p}/runs</c> (05.2.1 §5). The wire names are <c>manifest_id</c>, <c>release</c>,
/// <c>components</c>, <c>purpose</c> and <c>replay_grant_id</c> (the host's <c>SnakeCaseLower</c> policy).
/// <c>release</c> is syntactically validated but resolves to 422 <c>release_unresolvable</c> at M1 (AC10);
/// <c>components</c> is an optional JSON object. <c>purpose</c> is <c>regular</c> (the default) or <c>replay</c>
/// (05.2.1-T5): a replay run is refused 422 <c>replay_grant_invalid</c> until the grant store lands with 08.4.3
/// (AC8), and a replay run that also names a <c>release</c> is refused 422 <c>replay_release_not_allowed</c> (AC9).
/// <c>replay_grant_id</c> is the grant a replay run executes under; it is meaningless for a regular run.
/// </summary>
/// <param name="ManifestId">The verified manifest the run is created from.</param>
/// <param name="Release">Optional release name; never resolvable at M1, and refused outright for a replay run.</param>
/// <param name="Components">Optional free-form component map (build identifiers); never personal data.</param>
/// <param name="Purpose">Optional run purpose, <c>regular</c> (default) or <c>replay</c>; the wire name is <c>purpose</c>.</param>
/// <param name="ReplayGrantId">Optional replay grant, valid only with <c>purpose: "replay"</c>; the wire name is <c>replay_grant_id</c>.</param>
public sealed record CreateRunRequest(Guid ManifestId, string? Release, JsonElement? Components, string? Purpose, Guid? ReplayGrantId);

/// <summary>The 201 body of <c>POST /v1/projects/{p}/runs</c> (05.2.1 §5): the new run and its state, always <c>queued</c>.</summary>
/// <param name="RunId">The created run.</param>
/// <param name="State">Its state; <c>queued</c> because the producer slots commit in the create transaction (T13).</param>
public sealed record RunCreated(Guid RunId, string State);
