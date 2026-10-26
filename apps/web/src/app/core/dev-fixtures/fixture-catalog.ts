// 09.1.1-T12.1 (plan 09.1.1 §2 AC10, §10 T12; 01.6.1-T3 fixture corpus): the fixture documents
// the dev backend can answer with, keyed by the `operationId` of `contracts/openapi.yaml` and a
// `variant`.
//
// The corpus lives at `fixtures/web-api/<name>/` — one `input.json` plus a `meta.json` in the
// 01.6.1-T3 shape (`{"domain":"web-api","kind":"api-response","source":"handcrafted",…}`), no
// `expected.json` because a response is not a golden file. The JSON is imported directly, so the
// bundle carries exactly the corpus and there is no generated copy to drift: adding a state means
// adding a directory and one import here, and `meta.json` is the provenance record the fixture
// loaders (`packages/test-fixtures`) validate.
//
// `input.json` shape (fixed names, snake_case, because it is a file format and not a wire payload):
// `operation_id`, `variant`, `status`, `headers`, `latency_ms` and an optional `body` (omitted for
// a bodyless response). `variant: "ok"` is the answer the backend serves by default; every other
// variant is reached through `FixtureToggles` and exists so a review can show an empty list, a 403
// or a 412 without a server.
//
// Dev-only, like the rest of `core/dev-fixtures`: see the module comment of `fixture-toggles.ts`.

import ciBindingCreate from '../../../../../../fixtures/web-api/ci-binding-create/input.json';
import ciBindingCreateConflict from '../../../../../../fixtures/web-api/ci-binding-create-conflict/input.json';
import auditList from '../../../../../../fixtures/web-api/audit-list/input.json';
import auditListEmpty from '../../../../../../fixtures/web-api/audit-list-empty/input.json';
import auditListSupport from '../../../../../../fixtures/web-api/audit-list-support/input.json';
import ciBindingsList from '../../../../../../fixtures/web-api/ci-bindings-list/input.json';
import ciBindingsListOnboardingConnected from '../../../../../../fixtures/web-api/ci-bindings-list-onboarding-connected/input.json';
import ciBindingsListOnboardingProject from '../../../../../../fixtures/web-api/ci-bindings-list-onboarding-project/input.json';
import ciBindingsListOnboardingRun from '../../../../../../fixtures/web-api/ci-bindings-list-onboarding-run/input.json';
import ciBindingsListSettings from '../../../../../../fixtures/web-api/ci-bindings-list-settings/input.json';
import ciBindingsListSettingsEmpty from '../../../../../../fixtures/web-api/ci-bindings-list-settings-empty/input.json';
import ciBindingsListSettingsForbidden from '../../../../../../fixtures/web-api/ci-bindings-list-settings-forbidden/input.json';
import ciBindingsListSettingsPage1 from '../../../../../../fixtures/web-api/ci-bindings-list-settings-page-1/input.json';
import ciBindingsListSettingsPage2 from '../../../../../../fixtures/web-api/ci-bindings-list-settings-page-2/input.json';
import ingestTokenCreate from '../../../../../../fixtures/web-api/ingest-token-create/input.json';
import ingestTokenRotate from '../../../../../../fixtures/web-api/ingest-token-rotate/input.json';
import ingestTokenRotateConflict from '../../../../../../fixtures/web-api/ingest-token-rotate-conflict/input.json';
import ingestTokenRotateRateLimited from '../../../../../../fixtures/web-api/ingest-token-rotate-rate-limited/input.json';
import ingestTokensList from '../../../../../../fixtures/web-api/ingest-tokens-list/input.json';
import ingestTokensListOnboardingConnected from '../../../../../../fixtures/web-api/ingest-tokens-list-onboarding-connected/input.json';
import ingestTokensListOnboardingForbidden from '../../../../../../fixtures/web-api/ingest-tokens-list-onboarding-forbidden/input.json';
import ingestTokensListOnboardingProject from '../../../../../../fixtures/web-api/ingest-tokens-list-onboarding-project/input.json';
import ingestTokensListOnboardingProjectNotFound from '../../../../../../fixtures/web-api/ingest-tokens-list-onboarding-project-not-found/input.json';
import ingestTokensListOnboardingRun from '../../../../../../fixtures/web-api/ingest-tokens-list-onboarding-run/input.json';
import ingestTokensListOnboardingToken from '../../../../../../fixtures/web-api/ingest-tokens-list-onboarding-token/input.json';
import ingestTokensListOnboardingTokenUsed from '../../../../../../fixtures/web-api/ingest-tokens-list-onboarding-token-used/input.json';
import ingestTokensListOnboardingUnusable from '../../../../../../fixtures/web-api/ingest-tokens-list-onboarding-unusable/input.json';
import ingestTokensListSettings from '../../../../../../fixtures/web-api/ingest-tokens-list-settings/input.json';
import membershipCancelNotPending from '../../../../../../fixtures/web-api/membership-cancel-not-pending/input.json';
import membershipCancelOk from '../../../../../../fixtures/web-api/membership-cancel-ok/input.json';
import membershipCreateInviteExists from '../../../../../../fixtures/web-api/membership-create-invite-exists/input.json';
import membershipCreateOk from '../../../../../../fixtures/web-api/membership-create-ok/input.json';
import membershipCreateReplay from '../../../../../../fixtures/web-api/membership-create-replay/input.json';
import membershipGetAfterGrant from '../../../../../../fixtures/web-api/membership-get-after-grant/input.json';
import membershipRemoveInvalidTransition from '../../../../../../fixtures/web-api/membership-remove-invalid-transition/input.json';
import membershipRemoveLastAdmin from '../../../../../../fixtures/web-api/membership-remove-last-admin/input.json';
import membershipRemoveOk from '../../../../../../fixtures/web-api/membership-remove-ok/input.json';
import membershipResendNotPending from '../../../../../../fixtures/web-api/membership-resend-not-pending/input.json';
import membershipResendOk from '../../../../../../fixtures/web-api/membership-resend-ok/input.json';
import membershipResendReplay from '../../../../../../fixtures/web-api/membership-resend-replay/input.json';
import membershipRoleGrantForbidden from '../../../../../../fixtures/web-api/membership-role-grant-forbidden/input.json';
import membershipRoleGrantOk from '../../../../../../fixtures/web-api/membership-role-grant-ok/input.json';
import membershipRoleGrantUnknownRole from '../../../../../../fixtures/web-api/membership-role-grant-unknown-role/input.json';
import membershipRoleRevokeLastAdmin from '../../../../../../fixtures/web-api/membership-role-revoke-last-admin/input.json';
import membershipRoleRevokeOk from '../../../../../../fixtures/web-api/membership-role-revoke-ok/input.json';
import membershipsList from '../../../../../../fixtures/web-api/memberships-list/input.json';
import membershipsListEmpty from '../../../../../../fixtures/web-api/memberships-list-empty/input.json';
import orgsList from '../../../../../../fixtures/web-api/orgs-list/input.json';
import orgsListUnauthenticated from '../../../../../../fixtures/web-api/orgs-list-unauthenticated/input.json';
import orgsListUnavailable from '../../../../../../fixtures/web-api/orgs-list-unavailable/input.json';
import permissionsList from '../../../../../../fixtures/web-api/permissions-list/input.json';
import permissionsListLimited from '../../../../../../fixtures/web-api/permissions-list-limited/input.json';
import permissionsListOnboarding from '../../../../../../fixtures/web-api/permissions-list-onboarding/input.json';
import permissionsListOnboardingEnable from '../../../../../../fixtures/web-api/permissions-list-onboarding-enable/input.json';
import permissionsListOnboardingNoWrites from '../../../../../../fixtures/web-api/permissions-list-onboarding-no-writes/input.json';
import permissionsListSettingsAdmin from '../../../../../../fixtures/web-api/permissions-list-settings-admin/input.json';
import permissionsListSettingsAuditOnly from '../../../../../../fixtures/web-api/permissions-list-settings-audit-only/input.json';
import permissionsListSettingsMembersReadonly from '../../../../../../fixtures/web-api/permissions-list-settings-members-readonly/input.json';
import permissionsListSettingsNoTab from '../../../../../../fixtures/web-api/permissions-list-settings-no-tab/input.json';
import permissionsListSettingsPolicyReadonly from '../../../../../../fixtures/web-api/permissions-list-settings-policy-readonly/input.json';
import permissionsListSettingsPolicyWrite from '../../../../../../fixtures/web-api/permissions-list-settings-policy-write/input.json';
import permissionsListSettingsSecretsWrite from '../../../../../../fixtures/web-api/permissions-list-settings-secrets-write/input.json';
import policyActivationForbidden from '../../../../../../fixtures/web-api/policy-activation-forbidden/input.json';
import policyActivationInvalidTransition from '../../../../../../fixtures/web-api/policy-activation-invalid-transition/input.json';
import policyActivationOk from '../../../../../../fixtures/web-api/policy-activation-ok/input.json';
import policyActivationUnchanged from '../../../../../../fixtures/web-api/policy-activation-unchanged/input.json';
import policyRevisionCreate from '../../../../../../fixtures/web-api/policy-revision-create/input.json';
import policyRevisionCreateCriticalCaseQuarantined from '../../../../../../fixtures/web-api/policy-revision-create-critical-case-quarantined/input.json';
import policyRevisionCreatePreconditionFailed from '../../../../../../fixtures/web-api/policy-revision-create-precondition-failed/input.json';
import policyRevisionCreateUnchanged from '../../../../../../fixtures/web-api/policy-revision-create-unchanged/input.json';
import policyRevisionCreateUnknownField from '../../../../../../fixtures/web-api/policy-revision-create-unknown-field/input.json';
import policyRevisionFirst from '../../../../../../fixtures/web-api/policy-revision-first/input.json';
import policyRevisionHead from '../../../../../../fixtures/web-api/policy-revision-head/input.json';
import policyRevisionPrevious from '../../../../../../fixtures/web-api/policy-revision-previous/input.json';
import policyRevisionPublished from '../../../../../../fixtures/web-api/policy-revision-published/input.json';
import policyRevisionsList from '../../../../../../fixtures/web-api/policy-revisions-list/input.json';
import policyRevisionsListAfterPublish from '../../../../../../fixtures/web-api/policy-revisions-list-after-publish/input.json';
import policyRevisionsListConflictHead from '../../../../../../fixtures/web-api/policy-revisions-list-conflict-head/input.json';
import projectCreate from '../../../../../../fixtures/web-api/project-create/input.json';
import projectDetail from '../../../../../../fixtures/web-api/project-detail/input.json';
import projectDetailOnboardingConnected from '../../../../../../fixtures/web-api/project-detail-onboarding-connected/input.json';
import projectDetailOnboardingEmpty from '../../../../../../fixtures/web-api/project-detail-onboarding-empty/input.json';
import projectDetailOnboardingForbidden from '../../../../../../fixtures/web-api/project-detail-onboarding-forbidden/input.json';
import projectDetailOnboardingProjectNotFound from '../../../../../../fixtures/web-api/project-detail-onboarding-project-not-found/input.json';
import projectDetailOnboardingProject from '../../../../../../fixtures/web-api/project-detail-onboarding-project/input.json';
import projectDetailOnboardingRun from '../../../../../../fixtures/web-api/project-detail-onboarding-run/input.json';
import projectUpdatePreconditionFailed from '../../../../../../fixtures/web-api/project-update-precondition-failed/input.json';
import projectsList from '../../../../../../fixtures/web-api/projects-list/input.json';
import projectsListOnboardingEmpty from '../../../../../../fixtures/web-api/projects-list-onboarding-empty/input.json';
import projectsListOnboardingForbidden from '../../../../../../fixtures/web-api/projects-list-onboarding-forbidden/input.json';
import projectsListUnauthenticated from '../../../../../../fixtures/web-api/projects-list-unauthenticated/input.json';
import projectsListOnboardingProjectNotFound from '../../../../../../fixtures/web-api/projects-list-onboarding-project-not-found/input.json';
import rolesList from '../../../../../../fixtures/web-api/roles-list/input.json';
import runList from '../../../../../../fixtures/web-api/run-list/input.json';
import runList250 from '../../../../../../fixtures/web-api/run-list-250/input.json';
import runListEmpty from '../../../../../../fixtures/web-api/run-list-empty/input.json';
import runListNomatchPages from '../../../../../../fixtures/web-api/run-list-nomatch-pages/input.json';
import runListPage1 from '../../../../../../fixtures/web-api/run-list-page-1/input.json';
import runListPage2 from '../../../../../../fixtures/web-api/run-list-page-2/input.json';
import runListUnavailable from '../../../../../../fixtures/web-api/run-list-unavailable/input.json';
import runState from '../../../../../../fixtures/web-api/run-state/input.json';
import runStateOnboardingEvidence from '../../../../../../fixtures/web-api/run-state-onboarding-evidence/input.json';
import runStateOnboardingRunComplete from '../../../../../../fixtures/web-api/run-state-onboarding-run-complete/input.json';
import runsListForbidden from '../../../../../../fixtures/web-api/runs-list-forbidden/input.json';
import runsList from '../../../../../../fixtures/web-api/runs-list/input.json';
import runsListOnboardingConnected from '../../../../../../fixtures/web-api/runs-list-onboarding-connected/input.json';
import runsListOnboardingProject from '../../../../../../fixtures/web-api/runs-list-onboarding-project/input.json';
import runsListOnboardingRun from '../../../../../../fixtures/web-api/runs-list-onboarding-run/input.json';

/**
 * One fixture document, exactly as `fixtures/web-api/<name>/input.json` stores it.
 *
 * The `evidgate/no-hand-written-wire-dto` exemption below is intentional and narrow: the other
 * declarations of `apps/web` may not mirror an API payload because `contracts/openapi.yaml` and
 * `@evidgate/sdk` own the wire format, but this type describes a *fixture corpus file* whose
 * snake_case member names are the 01.6.1-T3 file format shared with the .NET loader — not a
 * response body the app reads. Coercing these names to camelCase would make the catalog and the
 * corpus disagree, and there is no SDK type to import instead: a fixture stands in for a response
 * of any operation, so it must stay operation-agnostic (`body` is `unknown`, and the contract
 * check of 09.1.1-T12.2 validates it against the operation's own schema).
 */
/* eslint-disable evidgate/no-hand-written-wire-dto -- the 01.6.1-T3 fixture file format (see above). */
export interface FixtureDocument {
  /** The `operationId` of `contracts/openapi.yaml` this document answers. */
  operation_id: string;
  /** `ok` for the default answer; any other name is reachable only through a toggle. */
  variant: string;
  /** The HTTP status the dev backend answers with; `>= 400` carries a 00 §9.2 envelope body. */
  status: number;
  /** Response headers the fixture pins, on top of the backend's `Content-Type: application/json`. */
  headers: Record<string, string>;
  /** Delay in milliseconds the backend applies when no latency toggle overrides it. */
  latency_ms: number;
  /** The response body; absent for a bodyless status (204/304). */
  body?: unknown;
}
/* eslint-enable evidgate/no-hand-written-wire-dto */

/**
 * One catalog row: the corpus directory name, the document it holds, and — for a route that
 * declares a `cursorQueryParameter` (`fixture-routes.ts`, 09.7.1-F15 note 4) — the request cursor
 * that document answers.
 *
 * `cursor` is a catalog fact, not a corpus field: `input.json` is the 01.6.1-T3 file format shared
 * with the .NET loader and holds a *response*, while which request a page answers is the dev
 * backend's business. A row without one answers a request that carries no cursor, which is why the
 * first page of a paged list needs nothing but its variant.
 */
export interface FixtureCatalogRow {
  readonly name: string;
  readonly document: FixtureDocument;
  /** The `cursor` query value this page answers; absent on the first page and every unpaged row. */
  readonly cursor?: string;
}

/**
 * Every document the dev backend can answer, by fixture directory name. Alphabetical by name so a
 * diff of this list is a diff of the corpus.
 *
 * 09.1.1-T4 added three rows for the shell: `permissions-list-limited` (the shell with a trimmed
 * grant set — `?fixture.variant.listPermissions=limited`), `orgs-list-unauthenticated` (the
 * boot's 401 — `?fixture.variant.listMyOrganizations=unauthenticated`) and
 * `orgs-list-unavailable` (the boot's 503 — `?fixture.variant.listMyOrganizations=unavailable`,
 * the state the `/unavailable` pattern renders; `GET /orgs` declares no 500 response, so the
 * contract's own "the read could not be completed" status is the one the corpus pins). The
 * limited subset holds
 * `audit:read`, `authoring:read`, `membership:read`, `project:read` and `role:read`, copied verbatim
 * from `contracts/permissions.json` (only `authoring:read` is on the first page of
 * `permissions-list/`). It leaves `organization:read` out on purpose: the limited session needs one
 * G1 route it may not open, and `settings` (gated by `organization:read`) is that route — the e2e
 * suite asserts its 403 (`apps/web/e2e/shell/adversarial.spec.ts`). The T12 leak gate
 * (`scripts/check-no-fixture-code.mjs`) no longer forces the omission: it skips a catalogue row's
 * `code`, `resource` and `action` as protocol vocabulary the production registry shares.
 *
 * 09.2.1-T1 added the onboarding wizard's seeds. The wizard derives its step from the project and
 * the four project-scoped reads (`getProject` for an explicit `:projectId`, `listProjects` for the
 * org's oldest project, `listCiBindings`, `listIngestTokens`, `listRuns`, `getRun`) and creates the
 * project with `createProject`, so one seed is a *set* of toggles rather than one document:
 *
 *   `onboarding-empty`            no project (`listProjects` empty, `getProject` 404) → step 1;
 *   `onboarding-project`          project, empty binding/token pages → step 2;
 *   `onboarding-token`            that project with one active `ingest_token` and an empty
 *                                 binding page → step 3 (either connect path satisfies step 2);
 *   `onboarding-connected`        one active `ci_binding`, no run → step 3;
 *   `onboarding-run`              those bindings plus one `complete` run with decision `FAIL`,
 *                                 so the AC6 no-PASS/FAIL check runs on a failing run;
 *   `onboarding-run-complete`     that run's `getRun` has `current_evidence_revision_id: null`
 *                                 → step 4;
 *   `onboarding-evidence`         the same run with an evidence revision → step 5 (activation is
 *                                 not readable yet, so the wizard reports it as not done).
 *
 * `onboarding-forbidden` (403) and `onboarding-project-not-found` (404) answer both project reads,
 * so a failing progress read renders the T8 pattern instead of a wrong step whether the wizard was
 * opened at the org's root or at a project address.
 * Every wizard URL also carries `permissions-list-onboarding`: the catalogue's own rows for the
 * seven codes the wizard gates steps 1–4 on (the step-5 code `policy:write` lives in
 * `permissions-list-onboarding-enable` since 09.2.1-T5), because the default page holds no
 * `project:*` code and the create submission is disabled without `project:create`.
 * `project-create` answers 201 with the
 * project of the default `project-detail` page, so the post-create reload — which carries no seed,
 * the wizard navigating to a plain `/orgs/<org>/onboarding/<projectId>` — resolves the created
 * project from that one read. `project-detail`, `ci-bindings-list`, `ingest-tokens-list` and
 * `run-state` are the `ok` defaults the same reload falls back to (the `Storefront web` project,
 * empty binding/token pages, the run of the default `runs-list` page). `onboarding-token` is the
 * one seed whose ingest-token page is not empty; every other seed pins it empty, so that seed is
 * the only one deriving step 3 from a token instead of from a `ci_binding`.
 *
 * 09.2.1-T2 added the step-2 write path. `ci-binding-create` (`POST /projects/{p}/ci-bindings`,
 * 201 with a `pending_verification` binding and the one-time `binding_challenge`),
 * `ingest-token-create` (`POST /projects/{p}/ingest-tokens`, 201 with the token's metadata and its
 * one-time `egt_…` secret) and `ingest-token-rotate` (`POST /ingest-tokens/{t}/rotate`, 200 with
 * the successor token, its secret and the `overlap_ends_at` the retired token keeps verifying
 * until) are each their operation's `ok` answer, so a review reaches all three with no toggle.
 * What the corpus cannot do is stay stateful: an answer is fixed, so a create cannot add a row to
 * a later list read. The step-2 states are therefore separate seeds, not one flow — the
 * `onboarding-token` seed proves the metadata-only resume (one token, never a secret in the page)
 * and the create documents prove the one-shot reveal. Their secret values are `egt_FIXTURE_ONLY_…`
 * / `evg_fixture_only_…` placeholders no issuer produces, and only the three documents above carry
 * one: `CiBindingView` and `IngestTokenView` each say they never hold the challenge or the secret,
 * so no list page can show a raw value again.
 *
 * The two refusal states the browser suite asserts on are documents too, because the `fixtures`
 * build answers the API in process (`HttpBackend`), so Playwright cannot intercept a submit:
 * `ci-binding-create-conflict` (variant `conflict` of `createCiBinding`: the binding panel's 409
 * `binding_exists`) and `ingest-token-rotate-conflict` (variant `conflict` of `rotateIngestToken`:
 * the token panel's 409 `invalid_transition`, the refusal of the path a token has instead of
 * "create a second one"). `createIngestToken` pins no 409 on purpose — its operation declares none
 * (contracts/openapi.yaml:2376-2397), so a token create cannot be refused with a status the client
 * is contracted to map. `permissions-list-onboarding-no-writes` (variant `onboarding-no-writes` of
 * `listPermissions`) is `permissions-list-onboarding` minus `ci_binding:write` and
 * `ingest_token:write` plus `policy:write`: it was authored (09.2.1-T2) while the wizard's set
 * still carried that code, and 09.2.1-T5 moved `policy:write` to
 * `permissions-list-onboarding-enable` instead. So step 2's two controls render disabled with their
 * localized reason instead of disappearing, while the step-5 confirm stays enabled under this set.
 *
 * One shape note for future rows: `allowed_workflow_refs` holds `job_workflow_ref` values,
 * `owner/repo/.github/workflows/<file>@refs/heads/main` — what the contract describes
 * (contracts/openapi.yaml:6654) and what the server's pattern accepts (`Ci/RefGlob.cs:69,77`; a
 * create with the other spelling is a 422 through `CiBindingCommands.cs:45`).
 * `ci-binding-create` uses that form; the T1 rows `ci-bindings-list-onboarding-connected` and
 * `ci-bindings-list-onboarding-run` still spell it `refs/heads/main:<path>` (line 18 of each), which
 * the server would refuse, so they are an open corpus item handed to 09.2.1-T7.1 rather than an
 * established convention.
 *
 * 09.7.1-T1 added three rows for the Settings tab strip: `permissions-list-settings-admin`
 * (`?fixture.variant.listPermissions=settings-admin`: the grant set that reaches every tab code of
 * the strip, G1 and later phases alike), `permissions-list-settings-audit-only` (`audit` is the one
 * tab left) and `permissions-list-settings-no-tab` (`organization:read` alone, so the shell renders
 * its empty state). Every row is copied verbatim from `contracts/permissions.json`, and the client
 * side derives the grants from the `tenant_assignable` rows of this catalogue, never from a role
 * name (`core/session/effective-permissions.source.ts`).
 *
 * 09.7.1-T2 adds the Members and roles tab's own states. `memberships-list` is the `listMemberships`
 * page the tab opens with: one sole active `tenant_admin` (`owner@acme.test`, the row the
 * last-admin guard protects), two other active memberships with different built-in roles (one of
 * them holding two roles, so the matrix has a multi-role row), one `pending` invite and one
 * `removed` row — the state whose Remove button the page must not offer. `memberships-list-empty`
 * (`?fixture.variant.listMemberships=empty`) is the empty page the shared empty pattern answers.
 * `membership-create-ok` is the `createMembership` 201 (`pending`, no initial roles);
 * `membership-create-invite-exists` (`?fixture.variant.createMembership=invite-exists`) is the
 * 409 `invite_exists` AC2's invite path surfaces verbatim. `membership-remove-ok` is the
 * `removeMember` 204 — the corpus's first bodyless document, which the T12.1 document contract
 * already allows (`body` is optional). `membership-remove-last-admin`
 * (`?fixture.variant.removeMember=last-admin`) is the 409 `last_admin` of AC2 and
 * `membership-remove-invalid-transition` (`?fixture.variant.removeMember=invalid-transition`) the
 * 409 `invalid_transition` a `pending` invite answers. This row also grows `settings-admin` by
 * `membership:write`: administering the Settings area includes inviting and removing members, and
 * `permissions-list-settings-members-readonly`
 * (`?fixture.variant.listPermissions=settings-members-readonly`) is the set that holds
 * `membership:read` (plus `organization:read` and `role:read`) but not `membership:write`, so the
 * suite can prove the invite and remove controls are absent from the DOM rather than disabled.
 * Error variants are named after the state (`invite-exists`, `last-admin`, `invalid-transition`),
 * not after the wire code, matching the corpus's `settings-*` rows.
 *
 * 09.2.1-T7 added four `listIngestTokens` rows for step 2's connection-health panel and its CLI
 * quickstart, each a variant of the same operation the wizard already reads:
 *
 *   `onboarding-token-used`     one `active` token (never used before: `last_used_at` set to
 *                               2026-10-24T09:12:34Z), so the panel has a healthy credential and a
 *                               recorded use, and the quickstart names that token's metadata;
 *   `onboarding-unusable`       a `revoked` and an `expired` token and nothing else — both lists
 *                               non-empty, so the step-2 derivation still completes on presence,
 *                               while the health panel has no usable credential (`unhealthy`);
 *   `onboarding-forbidden`      the 403 of that same read;
 *   `onboarding-project-not-found` its 404.
 *
 * The two refusals exist because the credential read is one of the three project reads
 * `OnboardingProgressService.load` issues together: a request the server refuses must reject the
 * resume (the shell renders the forbidden / not-found pattern) instead of publishing a step with an
 * empty health panel — a wrong step is exactly what 00 §9.2 forbids. Their `X-Request-Id` values
 * (`…f1`–`…f4`) are distinct from the older onboarding rows so a page that echoes one can only echo
 * the answer it actually received.
 *
 * 09.7.1-T6 adds the Audit tab's page. `audit-list` is the `listAudit` page the tab opens with:
 * six `AuditEntryDto` rows newest first on one hash chain (`seq` 1041–1046, each row's
 * `prev_hash` the older row's `entry_hash`), a mix of `user`, `ci_producer` and `system` actors,
 * one row carrying `metadata`, one with a null `object_id` (the system prune) and
 * `next_cursor: null`, so the tab renders a complete page without a follow-up read. No row there
 * is a support-access row — that is the state in which the plain rows must not grow a chip.
 * `audit-list-support` (`?fixture.variant.listAudit=support`) is that same page with two `user`
 * rows on top that carry `metadata.support_grant_id`: the marker alone is what flags a
 * support-access row, so AC8's flagged-versus-plain rendering has both cases in one page. No
 * document here names the wire enum's `platform_operator` actor, although the contract has it and
 * a row so flagged would be a support-access row too: the corpus-wide value scan of
 * `scripts/check-no-fixture-code.mjs` reads every string of 12+ characters outside its protocol
 * slots as fixture data, and that actor name is already a production literal
 * (`features/settings/audit/support-access.ts`), so a row carrying it fails the production build
 * for a word that is not data. That half of the flag rule is pinned by the `AuditStore` and
 * `AuditTableComponent` specs instead. `audit-list-empty`
 * (`?fixture.variant.listAudit=empty`) is the empty page the shared empty pattern answers.
 *
 * 09.7.1-F6 adds the role-edit dialog's three calls. `getMembership` is answered by
 * `membership-get-after-grant`: the corpus page's second row (`qa@acme.test`) with `tenant_admin`
 * added and `version` 3 — the state that member must show *after* a confirmed grant, which is what
 * the store re-reads instead of patching optimistically. `membership-role-grant-ok` is
 * `grantMembershipRole`'s 200 with the same membership and its `ETag`, so the grant answer and the
 * follow-up read cannot describe different role sets; both name that one member because the corpus
 * is static and the dialog's reachable grant is `tenant_admin` on that row (the one role the corpus
 * member does not hold). `membership-role-revoke-ok` is the 204 — the corpus's second bodyless
 * document, after `membership-remove-ok`.
 *
 * The three refusals are documents for the reason 09.7.1-T2's are: the `fixtures` build answers the
 * API in process (`HttpBackend`), so Playwright cannot intercept a submit and the state has to be
 * selected per operation (`?fixture.variant.<operationId>=<variant>`, `withVariants` in
 * `apps/web/e2e/shell/support.ts`). `membership-role-grant-forbidden`
 * (`?fixture.variant.grantMembershipRole=forbidden`) is the 403 `forbidden` of the self-escalation
 * rule carrying `SelfEscalation.Refused`'s own text
 * (`src/Modules/Evidgate.Modules.Identity/Application/Commands/SelfEscalation.cs:46-47`);
 * `membership-role-grant-unknown-role` (`?fixture.variant.grantMembershipRole=unknown-role`) is that
 * operation's 409 `unknown_role` (`GrantRole.cs`); `membership-role-revoke-last-admin`
 * (`?fixture.variant.revokeMembershipRole=last-admin`) is the 409 `last_admin` of demoting the last
 * admin (`RevokeRole.cs:108-110` — the same sentence is the guard's own wording in
 * `RemoveMember.cs:97-98` and `UpdateMembership.cs:134-135`, so it is the organization's rule, not
 * one operation's). Variant names follow the corpus convention (`forbidden`, `last-admin`,
 * `unknown-role`) rather than the wire code, and each refusal text is the server's current one: the
 * revoke row spells `tenant_admin` with the underscore, unlike the older `membership-remove-*` rows,
 * which are left as they were instead of being rewritten behind their own suite.
 *
 * 09.7.1-T3 (with `F12`) adds the Settings area's own pages. `permissions-list-settings-secrets-write`
 * (`?fixture.variant.listPermissions=settings-secrets-write`) is `permissions-list-settings-admin`
 * plus `ingest_token:write` — eleven rows copied verbatim from `contracts/permissions.json`, the
 * write row's `sensitive: true` included — because the Secrets page's rotate control needs
 * `ingest_token:write` and `settings-admin` holds the read alone: one grant set proves the control
 * absent, this one proves it present with the same tab strip.
 * `ingest-tokens-list-settings`
 * (`?fixture.variant.listIngestTokens=settings`) is the Secrets tab's token page: six
 * `IngestTokenView` rows on the `Storefront web` project (`…0011`, the first of `projects-list`),
 * covering an `active` token with a far `expires_at`, one expiring in under fourteen days
 * (2026-11-05), an `active` row whose `expires_at` has already passed, a `rotated` row carrying the
 * `overlap_ends_at` it keeps verifying until and the `rotated_to_id` of the row that replaced it,
 * that successor (`active`, `expires_at: null`) and a `revoked` row. Every `prefix` is a short
 * `egt_fx_…` placeholder, never a token: the tab renders metadata only, so the page could not show
 * a credential even if a component asked for one. Every timestamp is a literal, as the corpus
 * requires — the consequence being that the "expires soon" row is only soon for a review run near
 * those dates, and the already-expired `active` row is the state a period sweep has not reached
 * yet, which is exactly what the page's `expired` reminder must report. `ingest-tokens-list` and
 * every `onboarding-*` row stay byte-identical.
 *
 * The Members tab's invite lifecycle is the second half. `membership-create-ok` is now the full
 * `InviteIssuedView` — the T2 document plus `invite_token` — and `membership-create-replay`
 * (`?fixture.variant.createMembership=replay`) is the same body with `invite_token: null`, which is
 * the stored answer an `Idempotency-Key` replay returns. `membership-resend-ok` is
 * `resendMembershipInvite`'s 202: the `memberships-list` `pending` row (`newhire@acme.test`) with a
 * rotated token, `updated_at` and `version` 2, plus the `ETag` the contract declares;
 * `membership-resend-replay` is that body without the token. `membership-cancel-ok` is the 204 —
 * the corpus's third bodyless document — and `membership-resend-not-pending` /
 * `membership-cancel-not-pending` (`?fixture.variant.<operationId>=not-pending`) are that pair's 409
 * `invite_not_pending`, carrying the server's own sentence (`InviteErrors.NotPending`). The
 * `invite_token` values are `evgi_FIXTURE_ONLY_…` placeholders no issuer produces, and the
 * one-time-secret pin in `fixture-contract.spec.ts` names both invite operations beside the step-2
 * three, so no list or detail read may grow the member. Because the catalogue is static, the
 * lifecycle needs no new list document: `memberships-list` already holds the `pending` row these
 * calls address.
 *
 * 09.7.1-T4 adds the Settings > Policy tab's states. The corpus holds one project's revision
 * history — `018f0000-0000-7000-8000-000000000011`, the `Storefront web` project of
 * `projects-list` and `PROJECT_STOREFRONT` in `apps/web/e2e/shell/support.ts` — as four
 * `policy-revision.v1` bodies plus the three list pages they add up to. Revision 1 (`…a001`,
 * retired), 2 (`…a002`, retired) and 3 (`…a003`, `active`, the head) are joined by revision 4
 * (`…a004`, `draft`), which is what a publish of the tab's seeded body returns; a diff of 2 against
 * 3 therefore carries added (`storefront-catalog`, `storefront/catalog/price`, `video`,
 * `quarantine_ids`), removed (`legacy-cart`, `checkout/billing/refund-manual`, `freshness_minutes`)
 * and changed rows (`flaky_budget`, `collection_timeout_minutes`, and a nested change inside
 * `gating_refs`), and a diff of 3 against 4 adds `checkout/pay/apple-pay`. Every `hash` is the
 * `sha256:` digest of the RFC 8785 canonical body — this corpus has no body whose hash a reader
 * cannot recompute — and every body is far below the 64 KiB transport cap. The FOUR bodies carry
 * four distinct `id`s (their own revision ids), so `getPolicyRevision` is the one ID-KEYED route
 * (09.7.1-F15 note 3, `FIXTURE_ROUTES`): a read for `…a002` answers the revision-2 document and a
 * read for `…a003` the head, which is what lets the browser's revision-vs-revision diff and the
 * 412 dialog's server column hold two different bodies. The four variants remain for a review
 * that wants one document without naming an id in the path.
 *
 * Because the fixture backend still selects by variant for every id-agnostic route, the same four
 * bodies are also four variants of `getPolicyRevision`: `ok` is the head, and
 * `?fixture.variant.getPolicyRevision=previous` (revision 2), `=first` (revision 1) and `=created`
 * (revision 4) are the other three, so a review opens any single revision it needs, and they are
 * the answer for an id no document holds (`…a005`, the head another writer published). The list pages
 * are `policy-revisions-list` (`ok`, revisions 1..3, `ETag` the head `…a003`),
 * `policy-revisions-list-after-publish` (`?fixture.variant.listPolicyRevisions=after-publish`, the
 * same page with revision 4 on it and the head moved to `…a004`: the head is the highest
 * `revision_no`, so a published DRAFT is the `ETag` while `…a003` stays the one `active` row the
 * store reports as `activeRevision`) and
 * `policy-revisions-list-conflict-head` (`=conflict-head`, head `…a005`, the revision another writer
 * published while this session held `…a003` — the state the 412 below refuses; on that page `…a003`
 * is `retired` `version: 3`, because activating `…a005` retires it: one `active` revision per
 * project is a database invariant, `policy_revision_active_key` in
 * `migrations/0033_policy_add_policy_activation.sql`, so a page with two `active` rows would not
 * exist).
 *
 * `policy-revision-create` is publish's `201`: revision 4, `state: draft`, `ETag` its id.
 * `policy-revision-create-unchanged` (`?fixture.variant.createPolicyRevision=unchanged`) is the
 * contract's `200` — a body already stored returns that revision, here the head `…a003` — and is
 * the one answer a publish may not report as a new revision. The three refusals are the error
 * states the tab must render verbatim: `=precondition_failed` (`PolicyErrors.Stale`, details
 * `If-Match`/`stale` plus the current head's id in `revision_id`, as `PolicyRules.cs:45-52` builds
 * it), `=unknown-field` (`422 policy_unknown_field`, the closed schema's refusal with the offending
 * member's JSON pointer and the `additional_properties` reason `PolicyBodyPaths` reports) and
 * `=critical-case-quarantined` (`422 critical_case_quarantined`, the detail naming the quarantined
 * critical case the way `PolicyActivationStore.cs:128` writes it: `critical_cases/<case id>`,
 * reason `quarantined`). The quarantine guard gates ACTIVATION today — the create path has no
 * quarantine read — so this last document pins the envelope the tab must render verbatim when the
 * server sends it, not a route that answers it yet.
 *
 * `permissions-list-settings-policy-write` and `permissions-list-settings-policy-readonly` are the
 * Policy tab's two grant sets: the `release_approver` and `viewer` grants of
 * `contracts/permissions.json`, rows copied verbatim and ordered by `code`, so the pair differs in
 * `policy:write` — the one code that decides whether `[data-policy-publish-open]` exists at all —
 * and both hold `policy:read`, which is what keeps the tab itself reachable.
 *
 * 09.7.1-F15 adds the Secrets tab's CI-binding section: `listCiBindings` is list-only and read-only
 * (the contract declares no `GET /ci-bindings/{b}`, and the only writes are `PATCH`/`DELETE`), so
 * the three documents below are the three states that section renders.
 * `ci-bindings-list-settings` (`?fixture.variant.listCiBindings=settings`) is that page: five
 * `CiBindingView` rows on the `Storefront web` project (`…0011`, as `ingest-tokens-list-settings`),
 * covering the three verification states — three `active` bindings (one with three ref patterns and
 * two workflow refs, one with a single pattern, so the multi-line and single-line layouts both have
 * a row, and one that is the overflow case), a `pending_verification` binding with
 * `activated_at: null` (the OIDC handshake has not finished yet) and a `disabled` one. The overflow
 * row carries a 57-character repository full name and allowlists of eight ref patterns and four
 * workflow refs, so a cell that does not wrap has something to truncate. Every
 * `allowed_workflow_refs` entry uses the form the server accepts —
 * `owner/repo/.github/workflows/<file>@refs/…`, `Ci/RefGlob.cs:77` — not the
 * `refs/heads/main:<path>` spelling the 09.2.1-T1 rows above still carry. No document here has a
 * `binding_challenge` member: the challenge exists only in `ci-binding-create`'s answer, and the
 * corpus-wide one-time-secret pin in `fixture-contract.spec.ts` holds that for every list read.
 * `ci-bindings-list-settings-empty` (`=settings-empty`) is the unbound project's page (`items: []`,
 * `next_cursor: null`), the state the section's empty pattern answers, and
 * `ci-bindings-list-settings-forbidden` (`=settings-forbidden`) is the 403 of that same read, which
 * the store renders as its own `forbidden` phase. The three `X-Request-Id` values (`…c5`–`…c7`) are
 * distinct from the older `ci-bindings-list*` rows, so a page that echoes one can only echo the
 * answer it actually received. `ci-bindings-list` and every `onboarding-*` row stay byte-identical.
 *
 * 09.7.1-F15 note (4) adds the two pages of that same section's cursor walk, because every document
 * above ends with `next_cursor: null` and a list that never names another page cannot exercise the
 * section's Load more in a browser at all:
 * `ci-bindings-list-settings-page-1` (`=settings-page-1`) is the first page — three bindings, one per
 * verification state, and a `next_cursor` in the `<utc ticks in hex>-<uuid>` form `KeysetCursor`
 * issues (`run-list-page-1` below spells it the same way) — and `ci-bindings-list-settings-page-2`
 * (`=settings-page-2`) is the second, two further bindings with `next_cursor: null`, the terminal
 * page. That row and — since 09.3.1-T1 — `run-list-page-2` below are the catalog rows carrying a
 * `cursor`, and each value is exactly the `next_cursor` its first page names, so the backend answers
 * the cursor the screen actually got — not a cursor a test made up. Both pages sit on the
 * `Storefront web` project, their five `id`s are
 * disjoint (the store de-duplicates by `id`, so a repeated one would be silently dropped and the
 * second page would look empty), and the two `X-Request-Id` values (`…c8`, `…c9`) are distinct from
 * the three above.
 *
 * 09.1.1-T5 adds the one refusal the web sign-in flow needs and the corpus did not have:
 * `projects-list-unauthenticated` (`?fixture.variant.listProjects=unauthenticated`) is the `401` of
 * the scope read. `orgs-list-unauthenticated` covers the FIRST read of the session (a deep link with
 * no session renders `/login`), but an expiry happens after that read succeeded: the org is known and
 * the project list is the request that answers `session_expired`, which is what puts the store on its
 * `/login?reason=expired&returnUrl=…` path with a route to return to. The `code` is the contract's
 * `session_expired` (`contracts/openapi.yaml:6812-6818`) rather than `unauthenticated`, because the
 * two are the same client-side kind and only this one describes a session that existed a moment ago.
 *
 * 09.3.1-T1 adds the Runs list's own page (`listRuns`). `run-list` (`?fixture.variant.listRuns=runs`)
 * is the page's own set — without that toggle the operation still answers its `ok` document
 * (`runs-list` above), which is what a stray visit to `/runs` renders — and holds five `RunSummary`
 * rows
 * on the `Storefront web` project, one per state the page must render apart: a `complete` run with
 * `decision: PASS`; a `running` run whose `decision` is `null` (pending — the row must never render
 * as a verdict); a `complete` pull-request run with `decision: FAIL`, a `parent_run_id` and a
 * `rerun_label`, so the rerun label and the rerun-scope filter have a row; an `incomplete` run with
 * `decision: null`, `unverified_ref: true`, an `evidence:reprocess` label of its own and a
 * `ref.name` carrying U+202E, the format character `escapeFormatChars`
 * (`features/runs/list/ref-name-display.ts`) exists to expose; and a `canceled` run with no identity
 * at all (`kind`, `name`, `trigger_event` null, no `decision` member). No row carries a
 * completeness member because the wire has none — the Evidence column is the badge's unavailable
 * state for every row. `run-list-empty` (`=empty`) is the project with no runs, `run-list-250`
 * (`=250-rows`) is one full page of exactly 250 deterministic rows (`018f0000-…-0000000020xx`) for
 * the CDK virtual-scroll and axe checks, its decision a function of its state (a `complete` row
 * carries PASS or FAIL by alternating position, an `incomplete` row null (pending), every other state
 * `null`), and `run-list-nomatch-pages` (`=nomatch-pages`) is two
 * `queued` rows: with `scope`/`state` narrowing to `complete` every loaded row fails the filter, so
 * the page can show its "filtered from N loaded runs" note instead of the truly-empty copy.
 *
 * `run-list-page-1` (`=page-1`, three rows and a `next_cursor` in the `KeysetCursor` form) and
 * `run-list-page-2` (`=page-2`, two further rows, disjoint ids `…0084`/`…0085`, `next_cursor: null`)
 * are the paging pair. `run-list-page-2` declares that cursor (`08df2f51afe86800-…0083`, the keyset
 * position of page 1's last row) and `listRuns` declares `cursorQueryParameter: 'cursor'`, so a
 * browser `Load more` is answered with page 2 — the second cursor-keyed walk of the corpus after
 * `listCiBindings`' — and `fixture-contract.spec.ts` pins both explicitly, one per walk. The older
 * `runs-list` page ends the walk (`next_cursor: null`): its cursor was declared by no row, so a
 * follow-up answered the same page again, which the store refuses as a repeated cursor — the
 * screens that want a paged runs list take the pair above.
 * `run-list-unavailable` (`=unavailable`) is the same read's 503 `unavailable` (always
 * `Retry-After`, 00 §9.2), the state the list's error pattern renders with the server's retry hint.
 * Every `X-Request-Id` above (`…e10`–`…e17`) is distinct from the older `runs-list*` rows.
 *
 * 09.2.1-T5 adds step 5's documents and splits the wizard's grant set in two. The contract has no
 * project-keyed read of a `policy_activation` (`activatePolicyRevision` is the POST alone), so the
 * wizard cannot derive step 5 as done and the corpus answers that POST only: a reload shows the
 * confirm again and a repeat submit is the replay state below.
 * `permissions-list-onboarding` (`=onboarding`) is the seven codes the wizard gates steps 1–4 on
 * and holds NO `policy:write`, so step 5's confirm is natively disabled for it — the state of the
 * session whose `release_approver` role was reassigned away (plan §8 row 123 asks for the copy
 * "ask your release approver" in exactly this state, never for a hidden control).
 * `permissions-list-onboarding-enable` (`=onboarding-enable`, `X-Request-Id …a109`) is that same
 * seven-code set plus `policy:write`: the lone org creator's grants (T98, `release_approver` from
 * org creation), which makes step 5 confirmable without another member. The two sets therefore
 * differ in exactly the one code the confirm is derived from, and
 * `permissions-list-onboarding-no-writes` stays the separate step-2 state (no `ci_binding:write` /
 * `ingest_token:write`, `policy:write` kept) — its name is about step 2's creates, not step 5.
 *
 * The four activation answers are one project's pointer (`…0011`, as the policy revision documents
 * above) on revision 4 (`…a004`, the revision `policy-revision-create` returns):
 * `policy-activation-ok` is the `200` that moves the pointer, so it carries the same `hash` as the
 * create document and
 * `previous_policy_revision_id: …a003` — the seeded `active` head this call retires, because
 * `PolicyActivationStore.cs:18` defines that member as "the revision this call retired, or null when
 * nothing changed" (`activated_by` is the org-owner user id `…e1` `memberships-list` holds).
 * `policy-activation-unchanged` (`=unchanged`) is the replay of that same submit: same revision,
 * same hash, `changed: false` and `previous_policy_revision_id: null`, which is what a second click
 * and a post-reload confirm answer (00 §9.1: a repeated submit is not an error).
 * `policy-activation-invalid-transition` (`=invalid-transition`) is `409 invalid_transition`, the
 * retired-revision refusal verbatim ("a retired policy revision can never be activated again",
 * `PolicyActivationStore.cs:277-279`), and `policy-activation-forbidden` (`=forbidden`) is the 403 a
 * principal without `policy:write` gets; both are 00 §9.2 envelopes the page renders as its own
 * error state. Their `X-Request-Id`s (`…a105`–`…a108`) are distinct from every older policy row.
 */
export const FIXTURE_CATALOG: readonly FixtureCatalogRow[] = [
  { name: 'ci-binding-create', document: ciBindingCreate },
  { name: 'ci-binding-create-conflict', document: ciBindingCreateConflict },
  { name: 'audit-list', document: auditList },
  { name: 'audit-list-empty', document: auditListEmpty },
  { name: 'audit-list-support', document: auditListSupport },
  { name: 'ci-bindings-list', document: ciBindingsList },
  { name: 'ci-bindings-list-onboarding-connected', document: ciBindingsListOnboardingConnected },
  { name: 'ci-bindings-list-onboarding-project', document: ciBindingsListOnboardingProject },
  { name: 'ci-bindings-list-onboarding-run', document: ciBindingsListOnboardingRun },
  { name: 'ci-bindings-list-settings', document: ciBindingsListSettings },
  { name: 'ci-bindings-list-settings-empty', document: ciBindingsListSettingsEmpty },
  { name: 'ci-bindings-list-settings-forbidden', document: ciBindingsListSettingsForbidden },
  { name: 'ci-bindings-list-settings-page-1', document: ciBindingsListSettingsPage1 },
  {
    name: 'ci-bindings-list-settings-page-2',
    document: ciBindingsListSettingsPage2,
    cursor: '08df2cdfd69f6800-018f0000-0000-7000-8000-000000000063',
  },
  { name: 'ingest-token-create', document: ingestTokenCreate },
  { name: 'ingest-token-rotate', document: ingestTokenRotate },
  { name: 'ingest-token-rotate-conflict', document: ingestTokenRotateConflict },
  { name: 'ingest-token-rotate-rate-limited', document: ingestTokenRotateRateLimited },
  { name: 'ingest-tokens-list', document: ingestTokensList },
  {
    name: 'ingest-tokens-list-onboarding-connected',
    document: ingestTokensListOnboardingConnected,
  },
  {
    name: 'ingest-tokens-list-onboarding-forbidden',
    document: ingestTokensListOnboardingForbidden,
  },
  { name: 'ingest-tokens-list-onboarding-project', document: ingestTokensListOnboardingProject },
  {
    name: 'ingest-tokens-list-onboarding-project-not-found',
    document: ingestTokensListOnboardingProjectNotFound,
  },
  { name: 'ingest-tokens-list-onboarding-run', document: ingestTokensListOnboardingRun },
  { name: 'ingest-tokens-list-onboarding-token', document: ingestTokensListOnboardingToken },
  {
    name: 'ingest-tokens-list-onboarding-token-used',
    document: ingestTokensListOnboardingTokenUsed,
  },
  {
    name: 'ingest-tokens-list-onboarding-unusable',
    document: ingestTokensListOnboardingUnusable,
  },
  { name: 'ingest-tokens-list-settings', document: ingestTokensListSettings },
  { name: 'membership-cancel-not-pending', document: membershipCancelNotPending },
  { name: 'membership-cancel-ok', document: membershipCancelOk },
  { name: 'membership-create-invite-exists', document: membershipCreateInviteExists },
  { name: 'membership-create-ok', document: membershipCreateOk },
  { name: 'membership-create-replay', document: membershipCreateReplay },
  { name: 'membership-get-after-grant', document: membershipGetAfterGrant },
  { name: 'membership-remove-invalid-transition', document: membershipRemoveInvalidTransition },
  { name: 'membership-remove-last-admin', document: membershipRemoveLastAdmin },
  { name: 'membership-remove-ok', document: membershipRemoveOk },
  { name: 'membership-resend-not-pending', document: membershipResendNotPending },
  { name: 'membership-resend-ok', document: membershipResendOk },
  { name: 'membership-resend-replay', document: membershipResendReplay },
  { name: 'membership-role-grant-forbidden', document: membershipRoleGrantForbidden },
  { name: 'membership-role-grant-ok', document: membershipRoleGrantOk },
  { name: 'membership-role-grant-unknown-role', document: membershipRoleGrantUnknownRole },
  { name: 'membership-role-revoke-last-admin', document: membershipRoleRevokeLastAdmin },
  { name: 'membership-role-revoke-ok', document: membershipRoleRevokeOk },
  { name: 'memberships-list', document: membershipsList },
  { name: 'memberships-list-empty', document: membershipsListEmpty },
  { name: 'orgs-list', document: orgsList },
  { name: 'orgs-list-unauthenticated', document: orgsListUnauthenticated },
  { name: 'orgs-list-unavailable', document: orgsListUnavailable },
  { name: 'permissions-list', document: permissionsList },
  { name: 'permissions-list-limited', document: permissionsListLimited },
  { name: 'permissions-list-onboarding', document: permissionsListOnboarding },
  { name: 'permissions-list-onboarding-enable', document: permissionsListOnboardingEnable },
  {
    name: 'permissions-list-onboarding-no-writes',
    document: permissionsListOnboardingNoWrites,
  },
  { name: 'permissions-list-settings-admin', document: permissionsListSettingsAdmin },
  { name: 'permissions-list-settings-audit-only', document: permissionsListSettingsAuditOnly },
  {
    name: 'permissions-list-settings-members-readonly',
    document: permissionsListSettingsMembersReadonly,
  },
  { name: 'permissions-list-settings-no-tab', document: permissionsListSettingsNoTab },
  {
    name: 'permissions-list-settings-policy-readonly',
    document: permissionsListSettingsPolicyReadonly,
  },
  {
    name: 'permissions-list-settings-policy-write',
    document: permissionsListSettingsPolicyWrite,
  },
  {
    name: 'permissions-list-settings-secrets-write',
    document: permissionsListSettingsSecretsWrite,
  },
  { name: 'policy-activation-forbidden', document: policyActivationForbidden },
  { name: 'policy-activation-invalid-transition', document: policyActivationInvalidTransition },
  { name: 'policy-activation-ok', document: policyActivationOk },
  { name: 'policy-activation-unchanged', document: policyActivationUnchanged },
  { name: 'policy-revision-create', document: policyRevisionCreate },
  {
    name: 'policy-revision-create-critical-case-quarantined',
    document: policyRevisionCreateCriticalCaseQuarantined,
  },
  {
    name: 'policy-revision-create-precondition-failed',
    document: policyRevisionCreatePreconditionFailed,
  },
  { name: 'policy-revision-create-unchanged', document: policyRevisionCreateUnchanged },
  { name: 'policy-revision-create-unknown-field', document: policyRevisionCreateUnknownField },
  { name: 'policy-revision-first', document: policyRevisionFirst },
  { name: 'policy-revision-head', document: policyRevisionHead },
  { name: 'policy-revision-previous', document: policyRevisionPrevious },
  { name: 'policy-revision-published', document: policyRevisionPublished },
  { name: 'policy-revisions-list', document: policyRevisionsList },
  {
    name: 'policy-revisions-list-after-publish',
    document: policyRevisionsListAfterPublish,
  },
  {
    name: 'policy-revisions-list-conflict-head',
    document: policyRevisionsListConflictHead,
  },
  { name: 'project-create', document: projectCreate },
  { name: 'project-detail', document: projectDetail },
  { name: 'project-detail-onboarding-connected', document: projectDetailOnboardingConnected },
  { name: 'project-detail-onboarding-empty', document: projectDetailOnboardingEmpty },
  { name: 'project-detail-onboarding-forbidden', document: projectDetailOnboardingForbidden },
  { name: 'project-detail-onboarding-project', document: projectDetailOnboardingProject },
  {
    name: 'project-detail-onboarding-project-not-found',
    document: projectDetailOnboardingProjectNotFound,
  },
  { name: 'project-detail-onboarding-run', document: projectDetailOnboardingRun },
  { name: 'project-update-precondition-failed', document: projectUpdatePreconditionFailed },
  { name: 'projects-list', document: projectsList },
  { name: 'projects-list-onboarding-empty', document: projectsListOnboardingEmpty },
  { name: 'projects-list-onboarding-forbidden', document: projectsListOnboardingForbidden },
  { name: 'projects-list-unauthenticated', document: projectsListUnauthenticated },
  {
    name: 'projects-list-onboarding-project-not-found',
    document: projectsListOnboardingProjectNotFound,
  },
  { name: 'roles-list', document: rolesList },
  { name: 'run-list', document: runList },
  { name: 'run-list-250', document: runList250 },
  { name: 'run-list-empty', document: runListEmpty },
  { name: 'run-list-nomatch-pages', document: runListNomatchPages },
  { name: 'run-list-page-1', document: runListPage1 },
  {
    name: 'run-list-page-2',
    document: runListPage2,
    cursor: '08df2f51afe86800-018f0000-0000-7000-8000-000000000083',
  },
  { name: 'run-list-unavailable', document: runListUnavailable },
  { name: 'run-state', document: runState },
  { name: 'run-state-onboarding-evidence', document: runStateOnboardingEvidence },
  { name: 'run-state-onboarding-run-complete', document: runStateOnboardingRunComplete },
  { name: 'runs-list', document: runsList },
  { name: 'runs-list-forbidden', document: runsListForbidden },
  { name: 'runs-list-onboarding-connected', document: runsListOnboardingConnected },
  { name: 'runs-list-onboarding-project', document: runsListOnboardingProject },
  { name: 'runs-list-onboarding-run', document: runsListOnboardingRun },
];

/** One catalog entry: the corpus directory name, the document it holds and the cursor it answers. */
export type FixtureEntry = (typeof FIXTURE_CATALOG)[number];

/**
 * The entry answering `(operationId, variant)`, or `undefined` when the corpus has none — the dev
 * backend turns that into `501 fixture_not_found` rather than substituting another document, so a
 * stale toggle or a corpus that lost a state is visible in the page, not hidden behind a body that
 * happens to parse.
 */
export function findFixtureEntry(operationId: string, variant: string): FixtureEntry | undefined {
  return FIXTURE_CATALOG.find(
    (entry) => entry.document.operation_id === operationId && entry.document.variant === variant,
  );
}

/**
 * The `id` a document answers for: its own `body.id`, or `undefined` when the body is absent or
 * carries no string `id`. Nothing is inferred — an operation whose body names its resource another
 * way simply never matches by id, and its route must not declare an `idKeyParameter`.
 */
function documentId(document: FixtureDocument): string | undefined {
  const body = document.body;
  if (typeof body !== 'object' || body === null || Array.isArray(body)) {
    return undefined;
  }
  const id = (body as Record<string, unknown>)['id'];
  return typeof id === 'string' ? id : undefined;
}

/**
 * The entry of `operationId` whose own `body.id` equals `id` — the id-keyed selection
 * `fixture-backend.ts` makes for a route that declares an `idKeyParameter`, ahead of the variant
 * (09.7.1-F15 note 3). `undefined` means no document of that operation answers this id, and the
 * caller then falls back to the variant it would have used anyway.
 */
export function findFixtureEntryById(operationId: string, id: string): FixtureEntry | undefined {
  return FIXTURE_CATALOG.find(
    (entry) => entry.document.operation_id === operationId && documentId(entry.document) === id,
  );
}

/**
 * The entry of `operationId` whose catalog row declares `cursor` — the cursor-keyed selection
 * `fixture-backend.ts` makes for a route that declares a `cursorQueryParameter`, ahead of the variant
 * (09.7.1-F15 note 4). `undefined` means no page of that operation answers this cursor — the first
 * request of a list carries none at all — and the caller then falls back to the variant it would
 * have used anyway.
 */
export function findFixtureEntryByCursor(
  operationId: string,
  cursor: string,
): FixtureEntry | undefined {
  return FIXTURE_CATALOG.find(
    (entry) => entry.document.operation_id === operationId && entry.cursor === cursor,
  );
}
