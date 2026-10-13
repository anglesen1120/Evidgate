using System.Text.Json;

namespace Evidgate.Modules.Orchestrator.Contracts;

/// <summary>
/// The body of <c>POST /v1/projects/{p}/runs</c> (05.2.1 §5). The wire names are <c>manifest_id</c>, <c>release</c>,
/// <c>components</c>, <c>purpose</c>, <c>replay_grant_id</c> and <c>ref</c> (the host's <c>SnakeCaseLower</c> policy).
/// <c>release</c> is syntactically validated but resolves to 422 <c>release_unresolvable</c> at M1 (AC10);
/// <c>components</c> is an optional JSON object. <c>purpose</c> is <c>regular</c> (the default) or <c>replay</c>
/// (05.2.1-T5): a replay run is refused 422 <c>replay_grant_invalid</c> until the grant store lands with 08.4.3
/// (AC8), and a replay run that also names a <c>release</c> is refused 422 <c>replay_release_not_allowed</c> (AC9).
/// <c>replay_grant_id</c> is the grant a replay run executes under; it is meaningless for a regular run.
/// <c>ref</c> is the optional run identity (05.2.2-T1); see <see cref="RunRefRequest"/>.
/// </summary>
/// <param name="ManifestId">The verified manifest the run is created from.</param>
/// <param name="Release">Optional release name; never resolvable at M1, and refused outright for a replay run.</param>
/// <param name="Components">Optional free-form component map (build identifiers); never personal data.</param>
/// <param name="Purpose">Optional run purpose, <c>regular</c> (default) or <c>replay</c>; the wire name is <c>purpose</c>.</param>
/// <param name="ReplayGrantId">Optional replay grant, valid only with <c>purpose: "replay"</c>; the wire name is <c>replay_grant_id</c>.</param>
/// <param name="Ref">Optional run identity; the wire name is <c>ref</c> (05.2.2-T1).</param>
public sealed record CreateRunRequest(Guid ManifestId, string? Release, JsonElement? Components, string? Purpose, Guid? ReplayGrantId, RunRefRequest? Ref);

/// <summary>
/// The optional <c>ref</c> block of <see cref="CreateRunRequest"/> (05.2.2 §5, AC1–AC4): the identity the caller claims
/// for the run. Members are <c>kind</c>, <c>name</c>, <c>pr_number</c>, <c>head_sha</c>, <c>base_sha</c>,
/// <c>trigger_event</c>, <c>is_fork</c>; every one is optional, and an unknown member is 422 <c>request_invalid</c>
/// (<c>details[].path</c> <c>/ref/&lt;member&gt;</c>, reason <c>additional_properties</c>) like every other shape
/// failure of this body. A verified <c>ci_producer</c> caller's OIDC claims always win over the body and a conflicting
/// member is ignored, not rejected (AC3), but the body is still shape-validated first (AC4) and every value stored from
/// it marks the run <c>unverified_ref</c> (see <see cref="RunCreated"/>). <c>head_sha</c>/<c>base_sha</c> are 40 hex
/// digits for a SHA-1 repository and 64 for a SHA-256 one.
/// <para>
/// The type documents the wire shape for the served OpenAPI document; the create handler parses the raw body itself
/// (it must, to validate members this record cannot express and to fold the body into the idempotency hash), so this
/// record is never deserialized.
/// </para>
/// </summary>
/// <param name="Kind"><c>branch</c>, <c>tag</c> or <c>pull_request</c>; a <c>pull_request</c> without <see cref="PrNumber"/> is 422.</param>
/// <param name="Name">The branch or tag name, or GitHub's <c>GITHUB_REF_NAME</c> token for a pull request; 1..255 characters.</param>
/// <param name="PrNumber">The pull-request number; required with <c>kind: "pull_request"</c>.</param>
/// <param name="HeadSha">The built commit; only trusted for a non-fork CI claim (05.2.2-T1).</param>
/// <param name="BaseSha">The base commit; there is no verified source, so a stored one is always body-supplied.</param>
/// <param name="TriggerEvent"><c>push</c>, <c>pull_request</c>, <c>schedule</c>, <c>manual</c> or <c>dispatch</c>; body-only.</param>
/// <param name="IsFork">True only when the body says so; a verified CI caller is never a fork (02.4.1 refuses a fork exchange 403).</param>
public sealed record RunRefRequest(
    string? Kind,
    string? Name,
    int? PrNumber,
    string? HeadSha,
    string? BaseSha,
    string? TriggerEvent,
    bool? IsFork);

/// <summary>The 201 body of <c>POST /v1/projects/{p}/runs</c> (05.2.1 §5): the new run and its state, always <c>queued</c>.</summary>
/// <param name="RunId">The created run.</param>
/// <param name="State">Its state; <c>queued</c> because the producer slots commit in the create transaction (T13).</param>
/// <param name="UnverifiedRef">
/// True when the run's recorded identity came from the request body rather than a verified GitHub OIDC claim (05.2.2
/// AC2, T1): the run is advisory input, never a gating one. False for a caller whose identity was fully verified, and
/// for a request that carried no identity at all. The wire name is <c>unverified_ref</c>.
/// </param>
public sealed record RunCreated(Guid RunId, string State, bool UnverifiedRef);
