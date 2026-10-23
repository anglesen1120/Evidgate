namespace Evidgate.Modules.Orchestrator.Contracts;

/// <summary>
/// The body of <c>GET /v1/runs/{r}</c> (05.3.1 §5, AC7; identity block added by 05.2.2-T1, 00 §8.1): where one run
/// stands in the state machine, which evidence revision its current verdict was frozen from, its optimistic-concurrency
/// token, its purpose, and the identity it permanently records. The wire names are <c>run_id</c>, <c>state</c>,
/// <c>current_evidence_revision_id</c>, <c>version</c>, <c>purpose</c>, <c>ref</c> and <c>unverified_ref</c> (the host's
/// <c>SnakeCaseLower</c> policy). Another tenant's — or, for a project-scoped credential, another project's — run is
/// <c>404 not_found</c>, the same code and body as an unknown id (00 §5.4).
/// </summary>
/// <param name="RunId">The run.</param>
/// <param name="State">Its state, one of the 00 §8.1 vocabulary.</param>
/// <param name="CurrentEvidenceRevisionId">
/// The evidence revision the run's current verdict is computed from; null while no revision has been frozen yet
/// (05.3.2-T5 owns the pointer and the write side, this story exposes the column).
/// </param>
/// <param name="Version">The optimistic-concurrency token (00 §5.5 C4); a transition against a stale value is 412.</param>
/// <param name="Purpose">
/// The run's purpose, <c>regular</c> or <c>replay</c> (T67, 05.2.1-T5). A replay run is 05.3.1's read-model hook for
/// 09's regression-count exclusion; the column is written once by the create handler and never updated.
/// </param>
/// <param name="Ref">
/// What the run was created for (05.2.2 §5, AC1–AC3). Every member is present; the ones no source supplied are null,
/// and <see cref="RunRef.IsFork"/> is always a boolean. 05.2.2-T3.2 adds the derived <c>advisory_only</c> member on
/// top of this block, not here.
/// </param>
/// <param name="UnverifiedRef">
/// True when at least one member of <see cref="Ref"/> came from the request body rather than a verified GitHub OIDC
/// claim, i.e. the identity is advisory input and never a gating one (05.2.2 §8, AC2). False when every value was
/// verified, and false when the run records no identity at all.
/// </param>
public sealed record RunStateDto(
    Guid RunId,
    string State,
    Guid? CurrentEvidenceRevisionId,
    int Version,
    string Purpose,
    RunRef Ref,
    bool UnverifiedRef);

/// <summary>
/// The identity a run records (05.2.2 §4–§5): the seven wire members <c>kind</c>, <c>name</c>, <c>pr_number</c>,
/// <c>head_sha</c>, <c>base_sha</c>, <c>trigger_event</c>, <c>is_fork</c>. The names drop the <c>ref_</c> column
/// prefix, and the three reserved words of the 00 §7 identifier contract stay snake_case on the wire
/// (<c>pr_number</c>, <c>head_sha</c>, <c>base_sha</c>, <c>trigger_event</c>).
/// <para>
/// The values are written once by the create handler and are immutable afterwards (the migration's trigger, AC5). A
/// member no source supplied is null, so a run created without an identity carries the block with every value null and
/// <see cref="IsFork"/> false.
/// </para>
/// </summary>
/// <param name="Kind">
/// <c>branch</c>, <c>tag</c> or <c>pull_request</c>, or null when nothing identified the ref; the read model reports a
/// fork run as <c>fork</c> in 05.2.2-T3's label.
/// </param>
/// <param name="Name">
/// The ref's name: the branch of <c>refs/heads/&lt;n&gt;</c>, the tag of <c>refs/tags/&lt;n&gt;</c>, or GitHub's
/// <c>GITHUB_REF_NAME</c> form for a pull request (<c>&lt;N&gt;/merge</c>). At most 255 characters.
/// </param>
/// <param name="PrNumber">The pull request number of a <c>pull_request</c> identity; null otherwise.</param>
/// <param name="HeadSha">
/// The commit the run was built from (<c>^[0-9a-f]{40}([0-9a-f]{24})?$</c>). It is the verified <c>sha</c> claim for a
/// branch or tag; for a pull-request ref GitHub's claim is the test-merge commit, so that value is not used and this is
/// null unless the body supplied it.
/// </param>
/// <param name="BaseSha">The base commit; only the request body can supply one.</param>
/// <param name="TriggerEvent"><c>push</c>, <c>pull_request</c>, <c>schedule</c>, <c>manual</c> or <c>dispatch</c>; only the request body can supply one.</param>
/// <param name="IsFork">
/// True only when the request body said so (a fork PR cannot exchange a token, so a verified caller is never a fork).
/// Derived <c>advisory_only</c> is 05.2.2-T3.2's, and a fork run is never bound to a release nor counted by a gating
/// policy (AC7, AC8).
/// </param>
public sealed record RunRef(
    string? Kind,
    string? Name,
    int? PrNumber,
    string? HeadSha,
    string? BaseSha,
    string? TriggerEvent,
    bool IsFork);
