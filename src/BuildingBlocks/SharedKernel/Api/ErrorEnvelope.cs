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
    int? Index = null);

/// <summary>Stable error codes of the 00 §9.2 envelope, shared by the skeleton slice, the C2 pipeline's Result mapping and the endpoint filter.</summary>
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

    /// <summary>The org's last active <c>tenant_admin</c> cannot be removed or demoted (02.1.2 AC4: 409).</summary>
    public const string LastAdmin = "last_admin";

    /// <summary>The role id is neither a built-in catalogue role nor one of the caller's org roles (02.1.2 §5: 409).</summary>
    public const string UnknownRole = "unknown_role";

    /// <summary>The membership's current status does not allow the requested transition (02.1.2 §6: 409).</summary>
    public const string InvalidTransition = "invalid_transition";

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

    /// <summary>An unexpected failure; carries no internal detail (00 §5.5 C8).</summary>
    public const string InternalError = "internal_error";

    /// <summary>No task row exists with that id (01.5.1 §5 operator replay: 404).</summary>
    public const string TaskNotFound = "task_not_found";

    /// <summary>The row exists but is not `dead_letter` (01.5.1 §5 operator replay: 409).</summary>
    public const string TaskNotDeadLetter = "task_not_dead_letter";
}
