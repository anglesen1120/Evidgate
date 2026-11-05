using System.Text.Json.Serialization;

namespace Evidgate.BuildingBlocks.SharedKernel.Api;

/// <summary>
/// The 00 §9.2 error envelope: every <c>/v1</c> failure body is
/// <c>{"error": {code, message, request_id, retryable, details}}</c> and never
/// <c>ProblemDetails</c> (00 §5.5 C8). Field names are lower_snake_case on the
/// wire through <see cref="Serialization.EvidgateJsonContext"/>.
/// </summary>
public sealed record ErrorEnvelope(ErrorBody Error);

/// <summary>The body of the 00 §9.2 envelope.</summary>
/// <param name="Code">Stable machine code, e.g. <c>request_invalid</c>, <c>manifest_invalid</c>.</param>
/// <param name="Message">Human-readable, secret-free summary.</param>
/// <param name="RequestId">Echo of <c>X-Request-Id</c> (00 §9.1).</param>
/// <param name="Retryable">True only when the same request may be retried unchanged.</param>
/// <param name="Details">Per-field or per-item reasons; never contains secrets (00 §9.2).</param>
public sealed record ErrorBody(
    string Code,
    string Message,
    string RequestId,
    bool Retryable,
    IReadOnlyList<ErrorDetail> Details);

/// <summary>One entry of <see cref="ErrorBody.Details"/> (00 §9.2 example shape).</summary>
/// <param name="Path">JSON pointer or field name the reason belongs to.</param>
/// <param name="Reason">Machine-readable reason, e.g. <c>required</c>.</param>
/// <param name="Index">
/// For batch payloads, the 0-based position of the failing item (00 §9.2
/// <c>{"index": 3, "path": "/events/3/sequence", …}</c>); <c>null</c> — and
/// omitted on the wire — for document-level reasons (01.3.2-T2.1).
/// </param>
public sealed record ErrorDetail(
    string Path,
    string Reason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? Index = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? Limit = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? Actual = null);

/// <summary>Stable error codes shared by the pipeline's Result mapping and endpoint filters so failures use one wire vocabulary.</summary>
public static class ApiErrorCode
{
    /// <summary>The request itself is malformed or misses a required field.</summary>
    public const string RequestInvalid = "request_invalid";

    /// <summary>The manifest could not be accepted (00 §9.2 example code).</summary>
    public const string ManifestInvalid = "manifest_invalid";

    /// <summary>The addressed resource does not exist (or belongs to another tenant).</summary>
    public const string NotFound = "not_found";

    /// <summary>The row changed since the caller read it — the <c>version</c> precondition (00 §9.2: 412).</summary>
    public const string PreconditionFailed = "precondition_failed";

    /// <summary>No authenticated principal could be resolved (00 §9.2: 401 unauthenticated).</summary>
    public const string Unauthenticated = "unauthenticated";

    /// <summary>401: the CI token is not valid (signature, issuer, audience, lifetime or claims) (02.4.1).</summary>
    public const string TokenInvalid = "token_invalid";

    /// <summary>401: the OIDC token's <c>jti</c> was already exchanged (02.4.1 AC5).</summary>
    public const string TokenReplayed = "token_replayed";

    /// <summary>401: no such ingest token, or its secret is wrong (one code for both, 02.4.2 AC5).</summary>
    public const string TokenUnknown = "token_unknown";

    /// <summary>401: the ingest token was revoked (02.4.2 AC4).</summary>
    public const string TokenRevoked = "token_revoked";

    /// <summary>401: the ingest token expired or its rotation overlap ended (02.4.2 AC3).</summary>
    public const string TokenExpired = "token_expired";

    /// <summary>403: no binding admits this repository, ref or workflow (02.4.1 AC3).</summary>
    public const string BindingMismatch = "binding_mismatch";

    /// <summary>403: the binding has not proven repository control yet (T70).</summary>
    public const string BindingPending = "binding_pending";

    /// <summary>403: the binding was disabled.</summary>
    public const string BindingDisabled = "binding_disabled";

    /// <summary>409: another org already holds the active binding of this repository (T70).</summary>
    public const string BindingConflict = "binding_conflict";

    /// <summary>409: the project already binds this repository.</summary>
    public const string BindingExists = "binding_exists";

    /// <summary>429: a burst limit was hit; always with <c>Retry-After</c> (00 §9.2).</summary>
    public const string RateLimited = "rate_limited";

    /// <summary>503: a dependency or lock is busy; the request is retryable (00 §9.2, always with <c>Retry-After</c>).</summary>
    public const string Unavailable = "unavailable";

    /// <summary>The principal does not hold the operation's permission (00 §9.2: 403 forbidden).</summary>
    public const string Forbidden = "forbidden";

    /// <summary>The request addresses a project outside the principal's token scope (00 §9.1, 02 §4.3: 403).</summary>
    public const string ProjectScopeMismatch = "project_scope_mismatch";

    /// <summary>The principal has no active membership in the org it addressed (02.1.2 AC2, 02.3.1 AC3: 403).</summary>
    public const string NoMembership = "no_membership";

    /// <summary>An <c>Idempotency-Key</c> was reused for a different request (00 §9.1/§9.2: 409).</summary>
    public const string IdempotencyConflict = "idempotency_conflict";

    /// <summary>422: the healing switch mode is not one of <c>enabled</c>, <c>shadow</c>, <c>disabled</c> — <c>unavailable</c> included (08.6.3 AC2).</summary>
    public const string InvalidSwitchMode = "invalid_switch_mode";

    /// <summary>422: <c>enabled</c> was requested while the deployment runs with <c>Healing:Apply</c> off (08.6.3 AC7, ruling T33).</summary>
    public const string ModeNotAvailable = "mode_not_available";

    /// <summary>403: the platform-wide healing switch is not writable from a tenant route (08.6.3 AC1).</summary>
    public const string ForbiddenScope = "forbidden_scope";


    /// <summary>A built-in role is immutable (02.2.1 AC6: 409 <c>builtin_role_immutable</c>).</summary>
    public const string BuiltinRoleImmutable = "builtin_role_immutable";

    /// <summary>A submitted permission code is not in the catalogue (02.2.1 §5: 422 <c>unknown_permission</c>).</summary>
    public const string UnknownPermission = "unknown_permission";

    /// <summary>A member principal may never hold that code (TL T79, 02.2.1 AC7: 422 <c>permission_not_assignable</c>).</summary>
    public const string PermissionNotAssignable = "permission_not_assignable";

    /// <summary>The custom-role code is already taken in this org (02.2.1 §4 <c>unique (org_id, code)</c>: 409).</summary>
    public const string RoleCodeTaken = "role_code_taken";

    /// <summary>A pending invite for that address already exists in this org (02.1.2 AC1: 409).</summary>
    public const string InviteExists = "invite_exists";

    /// <summary>A removed, user-bound membership prevents re-inviting that address to the organization (409).</summary>
    public const string MemberRemoved = "member_removed";

    /// <summary>The org's last active <c>tenant_admin</c> cannot be removed or demoted (02.1.2 AC4: 409).</summary>
    public const string LastAdmin = "last_admin";

    /// <summary>The role id is neither a built-in catalogue role nor one of the caller's org roles (02.1.2 §5: 409).</summary>
    public const string UnknownRole = "unknown_role";

    /// <summary>The membership's current status does not allow the requested transition (02.1.2 §6: 409).</summary>
    public const string InvalidTransition = "invalid_transition";

    /// <summary>
    /// The membership carries no pending invite to resend or cancel, because it is already bound,
    /// cancelled or expired (02.1.2 §5 resend/cancel routes: 409).
    /// </summary>
    public const string InviteNotPending = "invite_not_pending";

    /// <summary>The slug is already used by another organization, or by another project of the same org (02.1.1 AC2: 409).</summary>
    public const string SlugTaken = "slug_taken";

    /// <summary>A tenant limit such as <c>organization.max_projects</c> is reached (02.1.1-T3: 429).</summary>
    public const string QuotaExceeded = "quota_exceeded";

    /// <summary>The organization is suspended, so tenant mutations are refused (02.1.1 §6: 409).</summary>
    public const string OrgSuspended = "org_suspended";

    /// <summary>The route exists but not for this HTTP method (00 §9.2 does not name 405).</summary>
    public const string MethodNotAllowed = "method_not_allowed";

    /// <summary>The request body exceeds the endpoint's limit (00 §9.2: 413 too large).</summary>
    public const string PayloadTooLarge = "payload_too_large";

    /// <summary>422: an ingestion count exceeds its configured budget.</summary>
    public const string CapExceeded = "cap_exceeded";

    /// <summary>422: a PATCH named an identity field of a test case (<c>annotation_id</c>, <c>file_path</c>, …) that never changes (03.1.1 §5).</summary>
    public const string CaseFieldImmutable = "case_field_immutable";

    /// <summary>422: a suite member's case revision belongs to another project or does not exist (03.2.1 AC2).</summary>
    public const string CrossProjectMember = "cross_project_member";

    /// <summary>422: the suite's dependency edges form a cycle; the path is in <c>details</c> (03.2.1 AC3).</summary>
    public const string DependencyCycle = "dependency_cycle";

    /// <summary>422: a variant's params or the environment requirements carry a secret-shaped key or value (03.2.1 AC4, AC8).</summary>
    public const string VariantNotSecretFree = "variant_not_secret_free";

    /// <summary>409: the suite revision is published (referenced by a run) and cannot be edited (03.2.1 AC6).</summary>
    public const string RevisionFrozen = "revision_frozen";

    /// <summary>409: the suite slug is already used in this project (03.2.1 §5).</summary>
    public const string SuiteSlugTaken = "suite_slug_taken";

    /// <summary>422: an environment revision carries a secret value or a malformed member (05.1.2 AC3).</summary>
    public const string EnvironmentRevisionInvalid = "environment_revision_invalid";

    /// <summary>422: a release body is JSON of the declared shape but breaks a release rule — a bad digest, a duplicate component key, an unknown state.</summary>
    public const string ReleaseInvalid = "release_invalid";

    /// <summary>409: the project already declares a release with that name (<c>unique (org_id, project_id, name)</c>).</summary>
    public const string ReleaseNameConflict = "release_name_conflict";

    /// <summary>422: a <c>draft</c> release accepts no run binds, so the release must be <c>active</c> first.</summary>
    public const string ReleaseNotActive = "release_not_active";

    /// <summary>409: the run already has a stored <c>live</c> evaluation, so its release binding is closed.</summary>
    public const string ReleaseBindClosed = "release_bind_closed";

    /// <summary>422: no component of the run matches the release's digests, so an explicit bind would store nothing.</summary>
    public const string ReleaseComponentMismatch = "release_component_mismatch";

    /// <summary>422: a policy body carries a member the 00 §8.6 field set does not declare (07.1.1 AC3).</summary>
    public const string PolicyUnknownField = "policy_unknown_field";

    /// <summary>422: a policy body is not JSON, does not match <c>policy-revision.v1</c>, or cannot be canonicalised (07.1.1 AC3).</summary>
    public const string PolicyInvalid = "policy_invalid";

    /// <summary>409: the environment's next revision number is not the <c>revision_no</c> the request expected (05.1.2 AC5).</summary>
    public const string RevisionConflict = "revision_conflict";

    /// <summary>An unexpected failure; carries no internal detail (00 §5.5 C8).</summary>
    public const string InternalError = "internal_error";

    /// <summary>No task row exists with that id (01.5.1 §5 operator replay: 404).</summary>
    public const string TaskNotFound = "task_not_found";

    /// <summary>The row exists but is not `dead_letter` (01.5.1 §5 operator replay: 409).</summary>
    public const string TaskNotDeadLetter = "task_not_dead_letter";

    /// <summary>422: the capability.v1 document fails its schema; <c>details[].path</c> points at the offending member (04.7.1 AC2, contracts/adapter/README.md).</summary>
    public const string CapabilityInvalid = "capability_invalid";

    /// <summary>422: the adapter's <c>protocol.major</c> is not one the server supports; <c>details</c> names both majors (04.7.1 AC2, contracts/adapter/README.md).</summary>
    public const string UnsupportedProtocolMajor = "unsupported_protocol_major";

    /// <summary>409: the natural key tuple matches a stored row that declares a different <c>mode</c>/<c>supports</c> set (04.7.1 AC3, contracts/adapter/README.md).</summary>
    public const string CapabilityConflict = "capability_conflict";

    /// <summary>422: a write or <c>finalize</c> names a <c>producer_id</c> with no registered slot on the run (00 §9.4, TL T9).</summary>
    public const string UnknownProducer = "unknown_producer";

    /// <summary>403: a write or <c>finalize</c> names a slot bound to another token identity (00 §9.4, TL T9).</summary>
    public const string ProducerSlotTaken = "producer_slot_taken";

    /// <summary>409: a run already carries a different number of producer slots, so a second registration cannot be a no-op (04.1.2-T4, T13).</summary>
    public const string ProducerSlotsConflict = "producer_slots_conflict";

    /// <summary>422: two planned executions of a manifest share an <c>execution_key</c> (05.2.1 AC6, 00 §7).</summary>
    public const string ExecutionKeyConflict = "execution_key_conflict";

    /// <summary>422: a request named a <c>release</c> the platform cannot resolve (05.2.1 AC10, T15; the bind path lands with 05.6.1).</summary>
    public const string ReleaseUnresolvable = "release_unresolvable";

    /// <summary>422: a <c>purpose: "replay"</c> request carries no usable replay grant (05.2.1 AC8, T5; the grant store lands with 08.4.3, so every replay request is refused until then).</summary>
    public const string ReplayGrantInvalid = "replay_grant_invalid";

    /// <summary>422: a <c>purpose: "replay"</c> request also names a <c>release</c>; the two are mutually exclusive (05.2.1 AC9, T5).</summary>
    public const string ReplayReleaseNotAllowed = "replay_release_not_allowed";

    /// <summary>403: a waiver may not be approved by the member who requested it (07.3.1 AC1, FR-07-8; 00 §9.3).</summary>
    public const string WaiverSelfApproval = "waiver_self_approval";

    /// <summary>409: the waiver is not in a state, or is past its expiry, that allows the requested transition (07.3.1 §6).</summary>
    public const string WaiverStateConflict = "waiver_state_conflict";

    /// <summary>422: the case is on the active policy revision's critical list and can never be quarantined (07.3.1 AC4, T23).</summary>
    public const string CriticalCaseNotQuarantinable = "critical_case_not_quarantinable";

    /// <summary>422: the revision lists a critical case that has an active quarantine entry in the project (07.1.1-T4.1, owner decision Q2).</summary>
    public const string CriticalCaseQuarantined = "critical_case_quarantined";

    /// <summary>409: an active quarantine entry for that case already exists in the project (07.3.1 §5, T23).</summary>
    public const string QuarantineConflict = "quarantine_conflict";

    /// <summary>422: the mint command carried a client-supplied object key; keys are server-chosen only (06.1.1 AC1, FR-06-1).</summary>
    public const string ObjectKeyNotAllowed = "object_key_not_allowed";

    /// <summary>422: the artifact <c>kind</c> is outside the 00 §7 closed list (06.1.1 §5).</summary>
    public const string KindUnknown = "kind_unknown";

    /// <summary>422: the declared checksum is not <c>sha256:&lt;64 lowercase hex&gt;</c> (06.1.1 §5).</summary>
    public const string ChecksumInvalid = "checksum_invalid";

    /// <summary>422: the declared artifact size is not positive (06.1.1 §5).</summary>
    public const string SizeOutOfRange = "size_out_of_range";

    /// <summary>422: the request named an <c>execution_key</c> that is not a planned execution of the run (00 §9.4, TL T10/T17).</summary>
    public const string UnknownExecutionKey = "unknown_execution_key";

    /// <summary>409: the project is being deleted, so a new upload is refused (06.4.4 AC2; the <c>deleted</c> project state at M1).</summary>
    public const string ProjectDeleting = "project_deleting";

    /// <summary>422: the declared media type is not in the kind policy's allowlist (06.1.2 AC1, 06.1.2-T3).</summary>
    public const string MediaTypeNotAllowed = "media_type_not_allowed";

    /// <summary>422: capture of the artifact kind is switched off for the project, or the project has no policy row for it (06.1.2 AC1, 06.2.2 AC2).</summary>
    public const string KindDisabled = "kind_disabled";

    /// <summary>422: a declared redaction rule names a field class outside the four contract types (06.2.2 AC3).</summary>
    public const string RedactionRuleTypeUnknown = "redaction_rule_type_unknown";

    /// <summary>422: the event batch is not a well-formed envelope batch — schema, canonicalisation or per-event shape (04.1.1 AC1/AC4, 00 §9.4).</summary>
    public const string EventInvalid = "event_invalid";

    /// <summary>422: an event of the batch reuses a <c>(run, producer, sequence)</c> slot already stored for the run (04.1.1 AC6).</summary>
    public const string SequenceReused = "sequence_reused";

    /// <summary>409: an <c>event_id</c> of the batch is already stored with a different canonical payload (04.1.1 AC5, T50).</summary>
    public const string DuplicateDifferentPayload = "duplicate_different_payload";

    /// <summary>409: the run is <c>complete</c>/<c>incomplete</c>, so it no longer accepts events (04.1.1 §5, 00 §8.3).</summary>
    public const string RunClosed = "run_closed";

    /// <summary>413: the event batch exceeds the route's 1 MiB transport cap (04.1.1 AC7, 00 §9.3).</summary>
    public const string BatchTooLarge = "batch_too_large";

    /// <summary>404: no such artifact in the caller's project — unknown, another tenant's or another project's (06.1.2-T2 AC6, §5).</summary>
    public const string ArtifactNotFound = "artifact_not_found";

    /// <summary>409: the artifact's state does not allow the requested transition (06.1.2-T2 §6).</summary>
    public const string ArtifactStateConflict = "artifact_state_conflict";

    /// <summary>403: the artifact's content is sensitive and the caller holds no <c>artifact:read_sensitive</c>.</summary>
    public const string SensitivityForbidden = "sensitivity_forbidden";

    /// <summary>422: a requested retention window is outside the deployment's configured range, or shorter than the artifact window; <c>details</c> names the member and the bound.</summary>
    public const string RetentionOutOfRange = "retention_out_of_range";

    /// <summary>422: the artifact's bytes violate the server-side parser profile — external entity or DTD, depth, size or time cap, or malformed content (06.3.1 AC1, AC2, 00 §5.7 S5).</summary>
    public const string ArtifactInvalid = "artifact_invalid";

    /// <summary>403: the route names no project and the caller's credential names none (06.1.2-T1 decision 10).</summary>
    public const string ProjectRequired = "project_required";

    /// <summary>422: a feature-tree placement breaks the kind ladder, names a parent that is missing, soft-deleted or in another project, or restores under a deleted parent (13.1.1 AC1/AC3/AC8).</summary>
    public const string AuthoringParentInvalid = "authoring_parent_invalid";

    /// <summary>409: a delete is refused because the node still holds live children or items, or the item still holds live manual cases (13.1.1 AC7).</summary>
    public const string AuthoringNotEmpty = "authoring_not_empty";

    /// <summary>409: another project of the same organization already uses the requested case-code prefix (13.1.1 AC4, OI-13-101); retryable false, message key <c>error.case_prefix_taken</c>.</summary>
    public const string CasePrefixTaken = "case_prefix_taken";

    /// <summary>
    /// 403: the account is disabled (<c>user.status = 'disabled'</c>, 02.3.1 §6), so a verified federated
    /// login issues no session. Not retryable: only an administrator re-enables the account.
    /// </summary>
    public const string AccountDisabled = "account_disabled";

    /// <summary>
    /// 401: the session cookie is past its sliding (<c>expires_at</c>) or its absolute
    /// (<c>absolute_expires_at</c>) deadline, so the request is refused before any handler runs and the browser
    /// must sign in again (02.3.2 AC2, AC3, §9.2).
    /// </summary>
    public const string SessionExpired = "session_expired";

    /// <summary>
    /// 401: the session row's <c>revoked_at</c> is set — logout, the user's or an admin's revoke, rotation, or
    /// the member removal that killed it — so the old cookie value stays dead on every later replay
    /// (02.3.2 AC4, AC8).
    /// </summary>
    public const string SessionRevoked = "session_revoked";

    /// <summary>
    /// 403: a cookie-authenticated mutation carried no <c>X-XSRF-TOKEN</c> header or one that does not match the
    /// antiforgery pair, so it is refused before the handler (02.3.2 AC5, T38).
    /// </summary>
    public const string CsrfFailed = "csrf_failed";

    /// <summary>
    /// 422: <c>PATCH /v1/me</c> carried a <c>preferred_locale</c> that is not <c>vi</c> or <c>en</c> — the two
    /// languages the contract names — so nothing is written (02.3.1 AC9).
    /// </summary>
    public const string LocaleInvalid = "locale_invalid";

    /// <summary>
    /// 422: <c>PATCH /v1/me</c> carried a member the own-profile contract does not patch (any name besides
    /// <c>preferred_locale</c>), so nothing is written (02.3.1 AC9).
    /// </summary>
    public const string FieldNotPatchable = "field_not_patchable";
}
