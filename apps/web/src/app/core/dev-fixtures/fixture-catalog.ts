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

import membershipCreateInviteExists from '../../../../../../fixtures/web-api/membership-create-invite-exists/input.json';
import membershipCreateOk from '../../../../../../fixtures/web-api/membership-create-ok/input.json';
import membershipRemoveInvalidTransition from '../../../../../../fixtures/web-api/membership-remove-invalid-transition/input.json';
import membershipRemoveLastAdmin from '../../../../../../fixtures/web-api/membership-remove-last-admin/input.json';
import membershipRemoveOk from '../../../../../../fixtures/web-api/membership-remove-ok/input.json';
import membershipsList from '../../../../../../fixtures/web-api/memberships-list/input.json';
import membershipsListEmpty from '../../../../../../fixtures/web-api/memberships-list-empty/input.json';
import orgsList from '../../../../../../fixtures/web-api/orgs-list/input.json';
import orgsListUnauthenticated from '../../../../../../fixtures/web-api/orgs-list-unauthenticated/input.json';
import orgsListUnavailable from '../../../../../../fixtures/web-api/orgs-list-unavailable/input.json';
import permissionsList from '../../../../../../fixtures/web-api/permissions-list/input.json';
import permissionsListLimited from '../../../../../../fixtures/web-api/permissions-list-limited/input.json';
import permissionsListSettingsAdmin from '../../../../../../fixtures/web-api/permissions-list-settings-admin/input.json';
import permissionsListSettingsAuditOnly from '../../../../../../fixtures/web-api/permissions-list-settings-audit-only/input.json';
import permissionsListSettingsMembersReadonly from '../../../../../../fixtures/web-api/permissions-list-settings-members-readonly/input.json';
import permissionsListSettingsNoTab from '../../../../../../fixtures/web-api/permissions-list-settings-no-tab/input.json';
import projectUpdatePreconditionFailed from '../../../../../../fixtures/web-api/project-update-precondition-failed/input.json';
import projectsList from '../../../../../../fixtures/web-api/projects-list/input.json';
import rolesList from '../../../../../../fixtures/web-api/roles-list/input.json';
import runsListForbidden from '../../../../../../fixtures/web-api/runs-list-forbidden/input.json';
import runsList from '../../../../../../fixtures/web-api/runs-list/input.json';

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
 */
export const FIXTURE_CATALOG: readonly {
  readonly name: string;
  readonly document: FixtureDocument;
}[] = [
  { name: 'membership-create-invite-exists', document: membershipCreateInviteExists },
  { name: 'membership-create-ok', document: membershipCreateOk },
  { name: 'membership-remove-invalid-transition', document: membershipRemoveInvalidTransition },
  { name: 'membership-remove-last-admin', document: membershipRemoveLastAdmin },
  { name: 'membership-remove-ok', document: membershipRemoveOk },
  { name: 'memberships-list', document: membershipsList },
  { name: 'memberships-list-empty', document: membershipsListEmpty },
  { name: 'orgs-list', document: orgsList },
  { name: 'orgs-list-unauthenticated', document: orgsListUnauthenticated },
  { name: 'orgs-list-unavailable', document: orgsListUnavailable },
  { name: 'permissions-list', document: permissionsList },
  { name: 'permissions-list-limited', document: permissionsListLimited },
  { name: 'permissions-list-settings-admin', document: permissionsListSettingsAdmin },
  { name: 'permissions-list-settings-audit-only', document: permissionsListSettingsAuditOnly },
  {
    name: 'permissions-list-settings-members-readonly',
    document: permissionsListSettingsMembersReadonly,
  },
  { name: 'permissions-list-settings-no-tab', document: permissionsListSettingsNoTab },
  { name: 'project-update-precondition-failed', document: projectUpdatePreconditionFailed },
  { name: 'projects-list', document: projectsList },
  { name: 'roles-list', document: rolesList },
  { name: 'runs-list', document: runsList },
  { name: 'runs-list-forbidden', document: runsListForbidden },
];

/** One catalog entry: the corpus directory name and the document it holds. */
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
