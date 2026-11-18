// The catalog list and the corpus narrative that explains every row group. The rows themselves
// live in the two halves below; concatenating them in this order is what keeps the served list,
// and therefore `findFixtureEntry` selection, identical to the single array it replaced.

import { FIXTURE_CATALOG_RUN_ROWS } from './fixture-catalog-run-rows';
import { FIXTURE_CATALOG_SESSION_ROWS } from './fixture-catalog-session-rows';
import { FIXTURE_CATALOG_RELEASE_ROWS } from './fixture-catalog-release-rows';
import { FIXTURE_CATALOG_SECURITY_ROWS } from './fixture-catalog-security-rows';
import type { FixtureCatalogRow } from './fixture-document';

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
 *   `onboarding-evidence`         the same run with an evidence revision → step 5 (its
 *                                 `getPolicyActivation` pin answers 404, so step 5 is not done).
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
 * `permissions-list-usage-read` (`?fixture.variant.listPermissions=usage-read`) holds `organization:read`
 * and `usage:read` alone, the one grant the Usage page needs, so it is reachable as a rendered surface.
 *
 * 09.7.1-T3 (with `F12`) adds the Settings area's own pages. `permissions-list-settings-secrets-write`
 * (`?fixture.variant.listPermissions=settings-secrets-write`) is `permissions-list-settings-admin`
 * plus `ingest_token:write` — twelve rows copied verbatim from `contracts/permissions.json`, the
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
 * the one answer a publish may not report as a new revision. The three refusals below are the
 * error states the tab must render verbatim: `=precondition_failed` (`PolicyErrors.Stale`, details
 * `If-Match`/`stale` plus the current head's id in `revision_id`, as `PolicyRules.cs:45-52` builds
 * it), `=unknown-field` (`422 policy_unknown_field`, the closed schema's refusal with the offending
 * member's JSON pointer and the `additional_properties` reason `PolicyBodyPaths` reports) and
 * `=critical-case-quarantined` (`422 critical_case_quarantined`, the detail naming the quarantined
 * critical case the way `PolicyActivationStore.cs:128` writes it: `critical_cases/<case id>`,
 * reason `quarantined`). The quarantine guard gates ACTIVATION today — the create path has no
 * quarantine read — so this last document pins the envelope the tab must render verbatim when the
 * server sends it, not a route that answers it yet.
 *
 * `policy-revision-create-version-pointer` (`?fixture.variant.createPolicyRevision=version-pointer`)
 * is that same `412` in the pointer spelling the authoring routes emit — `/If-Match` `stale`
 * beside `/version` (`AuthoringRequestScope.cs:62-71`) — and is a shape pin for the same reason:
 * the create path answers `revision_id` today, because the authoring screens are still route
 * stubs. Its detail's reason is the head's id, since the shared conflict dialog reloads whatever
 * identifier a detail names and this publish dialog is the one web screen that opens it; its code,
 * message and `retryable` members are copied from `policy-revision-create-precondition-failed`, so
 * the detail spelling is the only difference a browser case can attribute a dialog state to.
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
 * (`shared/ui/values/ref-name-display.ts`) exists to expose; and a `canceled` run with no identity
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
 * `listCiBindings`' — and `fixture-contract.spec.ts` pins both explicitly, one per walk. A three-row
 * first page is topped up inside the same trigger (`fill()` in
 * `features/runs/list/run-list.store.ts` follows the cursor until the view holds
 * `RUN_LIST_PAGE_LIMIT`), so on `page-1` the control is gone before a visitor can press it.
 * `run-list-page-1-full` (`=page-1-full`, 09.8.1-T1.2) is that same first page at the wire's own
 * page size — 47 deterministic rows (`…0052`–`…0080`, `2026-10-21T00:00`–`00:46`) ahead of
 * `run-list-page-1`'s three unchanged rows — so the read holds all 50 rows it asked for and stops
 * with the cursor still named: the browser keeps its `Load more` control, and following it is
 * answered by `run-list-page-2` exactly as `page-1`'s cursor is. The older `runs-list` page ends the
 * walk (`next_cursor: null`): its cursor was declared by no row, so a follow-up answered the same
 * page again, which the store refuses as a repeated cursor — the screens that want a paged runs list
 * take the pages above.
 * 09.3.1-T4 adds the run detail's attempts read (`listRunAttempts`, `GET /v1/runs/{r}/attempts`).
 * `run-attempts` (`ok`) is seven attempts of six executions (a failed-then-passed retry chain,
 * `blocked`, `timed_out`, `error`, `skipped`, `healing_candidate: null` on every row as on the wire
 * today); `run-attempts-page-1`/`-page-2` are the keyset pair (`listRunAttempts` declares
 * `cursorQueryParameter: 'cursor'`, page 2 declares page 1's `next_cursor`), `run-attempts-empty`
 * is a run with no attempts and `run-attempts-unavailable` the retryable 503.
 *
 * `run-list-unavailable` (`=unavailable`) is the same read's 503 `unavailable` (always
 * `Retry-After`, 00 §9.2), the state the list's error pattern renders with the server's retry hint.
 * Every `X-Request-Id` above (`…e10`–`…e17`) is distinct from the older `runs-list*` rows.
 *
 * 09.2.1-T5 adds step 5's documents and splits the wizard's grant set in two.
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
 *
 * 09.2.1-T5.2 adds the read those documents could not stand in for
 * (`getPolicyActivation`, `GET /v1/projects/{p}/policy-activation`, 07.1.1 F18), which is now how
 * the wizard derives step 5 instead of assuming it is never done. Its four documents are the same
 * pointer on the same revision, and — being a read — carry `PolicyActivationStateDto`'s six
 * members and none of the POST's two extra ones (`previous_policy_revision_id`, `changed` are not
 * stored by migration `0033` and are not on this route):
 * `policy-activation-read-ok` (`=ok`) is the activated project in `advisory` mode, the state that
 * completes step 5 and opens the terminal screen; `policy-activation-read-enforcing` (`=enforcing`)
 * is the same pointer in `enforcing` mode, so a review can show the read answering a mode the
 * wizard's own confirm never sends (07.7.1); `policy-activation-read-not-found` (`=not-found`) is
 * the contract's one undifferentiated `404` — never activated, unknown, soft-deleted and another
 * tenant's project all read here (00 §9.2) — which the client reports as "no activation", never as
 * a not-found error; and `policy-activation-read-forbidden` (`=forbidden`) is the `403` of a
 * principal without `policy:read`, the state in which steps 1–4 still derive and step 5 alone stays
 * open. `X-Request-Id`s `…a10a`–`…a10d` are distinct from every row above, and the `404`/`403`
 * bodies name their own request id, as every 00 §9.2 envelope in the corpus does.
 *
 * F63 adds the read's own throttled answer, `policy-activation-read-rate-limited`
 * (`=rate-limited`): the contract's `429 RateLimited` response — `Retry-After: 30` and a
 * `retryable: true` envelope — which is the state the page's own wait is built from. The document
 * pins that answer and `fixture-backend.spec.ts` replays it into `ApiError.retryAfterSeconds`; the
 * countdown itself is proven in the page's Vitest spec, because a `429` is not a state the wizard
 * reaches in the fixtures build (the progress read rejects the whole load for any refusal other
 * than `403`, so step 5 does not mount under it).
 * Its `X-Request-Id` (`…a10e`) is distinct again, and the body names the same id.
 *
 * `getPolicyHead` answers the head read the same tab's revision display needs, so the tab can show
 * the active and the head revision from two small reads instead of summing a list page.
 * `policy-head-read-ok` is the summary of the revision `policy-revision-head` serves (`…a003`,
 * revision 3) with the `ETag` that id quoted, exactly as the revision read issues it — the header a
 * publish sends back as `If-Match`. `policy-head-read-not-found` is the contract's one
 * undifferentiated `404` (a project with no revision at all, an unknown, soft-deleted or
 * other-tenant project all read the same envelope), which the client reports as "no revisions",
 * never as a failed load. Their `X-Request-Id`s (`…a10f`, `…a110`) are distinct from every row
 * above, and the `404` body names its own.
 *
 * The seven `suite-revision*` rows answer the pinned revision read (`getSuiteRevision`,
 * `GET /v1/suite-revisions/{r}`) the composition screen mounts: `ok` is a draft whose shard
 * strategy is `count`, `published` is the immutable revision (status and the read-only notice come
 * from the body, not from a client guess), `empty` is a revision with no member, `revision-not-found`
 * and `revision-forbidden` are the two refusals the screen renders as its own states, and
 * `revision-unavailable` is the `503` with a `Retry-After`. `large` is the one revision no matrix can
 * draw — 64 case revisions over 45 variants is 2880 cells, past `MATRIX_CELL_LIMIT` — so it is also
 * the only row that opens the flat table and its `Show more` control (288 members, one chunk of 200
 * first). Every 2xx document carries the wire
 * members and none of them a warnings member, because the read has none to send.
 *
 * The project API-token pair is one path's two verbs. `api-token-create` (`createApiToken`,
 * `POST /projects/{p}/api-tokens`) is the one-time plaintext answer: the token's metadata plus the
 * `evg_…_FIXTURE_ONLY_…` secret the page reveals once and no later read can return again, so the
 * document is the operation's `ok` answer and needs no toggle. `api-tokens-list` (`listApiTokens`,
 * `GET` on the same path) is the metadata page — one `active` and one `revoked` row, the two states
 * the list must render apart — and it ends the walk (`next_cursor: null`), so the route is not
 * cursor-keyed and no catalog row declares a cursor for it. The pair's `X-Request-Id`s (`…f1`,
 * `…f2`) are distinct from every row above.
 */

export const FIXTURE_CATALOG: readonly FixtureCatalogRow[] = [
  ...FIXTURE_CATALOG_SESSION_ROWS,
  ...FIXTURE_CATALOG_RUN_ROWS,
  ...FIXTURE_CATALOG_RELEASE_ROWS,
  ...FIXTURE_CATALOG_SECURITY_ROWS,
];

/** One catalog entry: the corpus directory name, the document it holds and the cursor it answers. */
export type FixtureEntry = (typeof FIXTURE_CATALOG)[number];
