namespace Evidgate.Modules.Orchestrator.Contracts;

/// <summary>
/// The body of <c>GET /v1/runs/{r}</c> (05.3.1 §5, AC7; identity block added by 05.2.2-T1, 00 §8.1): where one run
/// stands in the state machine, which evidence revision its current verdict was frozen from, its optimistic-concurrency
/// token, its purpose, the identity it permanently records, and — since F44 (S143) — the release inputs its creation
/// locked (the pinned manifest, its suite and policy revisions, its environment revision and the producer slots it
/// registered). The wire names are <c>run_id</c>, <c>state</c>, <c>current_evidence_revision_id</c>, <c>version</c>,
/// <c>purpose</c>, <c>ref</c>, <c>unverified_ref</c>, <c>manifest_hash</c>, <c>suite_revision_id</c>,
/// <c>policy_revision_id</c>, <c>environment</c>, <c>shard_count</c>, <c>declared_producers</c>, <c>releases</c>,
/// <c>after_cancel_events</c> and <c>state_history</c> (the host's <c>SnakeCaseLower</c> policy). Another tenant's — or,
/// for a project-scoped credential, another project's — run is <c>404 not_found</c>, the same code and body as an
/// unknown id (00 §5.4).
/// <para>
/// Every member is required and comes from a stored column: nothing here is a placeholder. Evidence
/// <c>completeness</c>/<c>missing[]</c> are a later, separate read (09.3.1-T3 with 07.5.2), not part of this body.
/// </para>
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
/// <param name="ManifestHash">
/// The pinned manifest's <c>manifest_hash</c> (F44, S143), joined from <c>manifest</c> through the run's
/// <c>manifest_id</c>. Never null on a stored run: the column and its foreign key are not nullable (0031).
/// </param>
/// <param name="SuiteRevisionId">
/// The suite revision the run locked at creation — the run's own column (0031), not a re-read of the manifest.
/// </param>
/// <param name="PolicyRevisionId">
/// The policy revision the run pinned (07.1.1-T4.2); null for a run created while its project had no active
/// revision, and never moved by a later activation.
/// </param>
/// <param name="Environment">The environment revision the run locked; its identity only, never its content.</param>
/// <param name="ShardCount">
/// How many producer slots the run registered — one per manifest shard (0029/0031); zero for a run whose slots were
/// never registered.
/// </param>
/// <param name="DeclaredProducers">
/// The run's producer slot ids in <c>shard_index</c> order, the values the manifest's shards declare
/// (<c>shard-&lt;n&gt;</c>, written at run creation, 0029).
/// </param>
/// <param name="Releases">
/// The releases the run is bound to, one item per stored <c>release_run</c> row, ordered by that row's
/// <c>created_at</c> then id. An unbound run carries an empty array — never null — and a release of another tenant,
/// or of a sibling project the bind path refused, is never listed.
/// </param>
/// <param name="AfterCancelEvents">
/// How many of the run's <c>event</c> rows are flagged <c>after_cancel</c>: late events a canceled run kept accepting.
/// It counts events, not case attempts, and a run with none reports zero.
/// </param>
/// <param name="StateHistory">
/// The run's recorded transitions, oldest first, from the table the write trigger appends to; the creation row — the
/// one with no from state — is the marker that the history is known, so it is never served. An empty array is a run
/// whose history is known and which has not transitioned yet; null is a run created before that table existed, whose
/// history nothing can reconstruct. At most <c>64</c> items are served.
/// </param>
public sealed record RunStateDto(
    Guid RunId,
    string State,
    Guid? CurrentEvidenceRevisionId,
    int Version,
    string Purpose,
    RunRef Ref,
    bool UnverifiedRef,
    string ManifestHash,
    Guid SuiteRevisionId,
    Guid? PolicyRevisionId,
    RunEnvironmentRef Environment,
    int ShardCount,
    IReadOnlyList<string> DeclaredProducers,
    IReadOnlyList<RunReleaseRef> Releases,
    int AfterCancelEvents,
    IReadOnlyList<RunStateTransition>? StateHistory);

/// <summary>
/// One recorded transition of a run (<c>GET /v1/runs/{r}</c>): the state it left, the state it entered and when the
/// database recorded the move. The wire names are <c>from_state</c>, <c>to_state</c> and <c>occurred_at</c>, and all
/// three are required — the creation row, which has no from state, is the marker and is never served.
/// </summary>
/// <param name="FromState">The state the run left, one of the 00 §8.1 vocabulary; never null in a served item.</param>
/// <param name="ToState">The state the run entered, one of the same vocabulary.</param>
/// <param name="OccurredAt">
/// The database clock time the transition was recorded, so two values of one run are ordered by the row's sequence,
/// which a clock that steps backwards would not preserve.
/// </param>
public sealed record RunStateTransition(string FromState, string ToState, DateTimeOffset OccurredAt);

/// <summary>
/// One release a run is bound to (<c>GET /v1/runs/{r}</c>): the release's id and name, and the component key the stored
/// binding covers, so a run bound to a three-component release carries three items of one release. The wire names are
/// <c>release_id</c>, <c>release_name</c> and <c>component_key</c>.
/// </summary>
/// <param name="ReleaseId"><c>release.id</c> the run is bound to.</param>
/// <param name="ReleaseName">The release's declared name (<c>release.name</c>).</param>
/// <param name="ComponentKey">The release component this binding row covers.</param>
public sealed record RunReleaseRef(Guid ReleaseId, string ReleaseName, string ComponentKey);

/// <summary>
/// The run's pinned environment revision (<c>GET /v1/runs/{r}</c>, F44, S143): the identity of the
/// <c>environment_revision</c> row the run locked, joined through <c>run.environment_revision_id</c> (not null, FK
/// <c>run_environment_revision_fk</c>, 0031), plus the environment's own key. The wire names are <c>revision_id</c>,
/// <c>environment_id</c>, <c>environment_key</c>, <c>revision_no</c> and <c>revision_hash</c>, and every one is
/// required. No <c>secret_refs</c>, no <c>base_urls</c>, no <c>dependency_versions</c>: the run detail names the
/// revision, it does not re-publish its content (<c>GET /v1/environment-revisions/{id}</c> does that).
/// </summary>
/// <param name="RevisionId"><c>environment_revision.id</c>.</param>
/// <param name="EnvironmentId"><c>environment_revision.environment_id</c>.</param>
/// <param name="EnvironmentKey">The environment's key (<c>environment.environment_key</c>, 0024).</param>
/// <param name="RevisionNo"><c>environment_revision.revision_no</c>, 1-based per environment.</param>
/// <param name="RevisionHash">
/// <c>environment_revision.revision_hash</c> — the RFC 8785 content hash pinned by the run.
/// </param>
public sealed record RunEnvironmentRef(
    Guid RevisionId,
    Guid EnvironmentId,
    string EnvironmentKey,
    int RevisionNo,
    string RevisionHash);

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
