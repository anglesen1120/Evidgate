// 09.7.1-T4 — plan 09.7.1 §2 AC1/AC3/AC4/AC5, §5, §8 S3 and §10 T4; real Chromium against the
// FIXTURES build of `apps/web`.
//
// The Policy tab is a phase-G4 product tab, so it is absent from this G1 build: only the
// dev-routes file-replacement seam mounts the real page, at
// `/dev/settings-policy/orgs/:orgId`, under the normal shell, auth and org-scope gates plus the
// `organization:read` + `policy:read` pair the product tab is gated on. Nothing else is relaxed:
// the page-local PolicyStore and the publish control's `policy:write` derivation are the real ones.
//
// Fixtures replace Angular's `HttpBackend` IN PROCESS (`core/dev-fixtures/fixture-backend.ts`), so
// Playwright never sees a `/v1/policies` request and cannot count or inspect one — the same limit
// the Secrets suite records. Every "nothing was sent" claim below is therefore made through an
// observable the app owns, and one of them is deliberately impossible to miss: the variant selected
// for `createPolicyRevision` is one NO corpus document answers (`variant=absent`), so the dev
// backend answers any such POST with 501 `fixture_not_found` and the dialog's server-refusal region
// `[data-policy-error]` appears. A local refusal that keeps that region absent therefore proves the
// request never left the app. The same device covers the read side (`getPolicyRevision=absent`):
// a fired read settles `data-policy-phase` to `error`, so an `idle` tab is the proof nothing was
// requested for a revision id that is not UUID-shaped. "One POST, never retried" is observed as one
// conflict dialog, no `[data-policy-published]`, and no second dialog after the user returns to the
// draft; the store's own spec (`policy.store.spec.ts`) pins the call count itself.
//
// The in-process backend answers by VARIANT except for `getPolicyRevision`, which is ID-KEYED
// (09.7.1-F15 note 3): a read for a revision id the corpus holds answers that document, so the
// revision-vs-revision diff is a real browser comparison of two corpus documents — the case below
// proves it row by row — and the 412 dialog's server column comes from the head read. Every other
// operation still answers one document per operation.
//
// Helm pickers expose their current revision in the combobox text and their portalled
// options' aria-selected state. Both must agree with the served revision id; the comparison
// itself is asserted through [data-policy-diff].
//
// Frozen-grant and refusal corpora are separate documents selected per operation through
// `?fixture.variant.<operationId>=<variant>`; no case infers a state by clicking twice. Every
// selector is a component/data hook, an id or a role, so the suite passes on both locale bundles;
// the only verbatim text asserted is the SERVER's own 412/422 envelope, which AC4/AC5 require.
import { expect, test, type Locator, type Page } from '@playwright/test';

import created from '../../../../fixtures/web-api/policy-revision-create/input.json';
import critical from '../../../../fixtures/web-api/policy-revision-create-critical-case-quarantined/input.json';
import preconditionFailed from '../../../../fixtures/web-api/policy-revision-create-precondition-failed/input.json';
import unchanged from '../../../../fixtures/web-api/policy-revision-create-unchanged/input.json';
import unknownField from '../../../../fixtures/web-api/policy-revision-create-unknown-field/input.json';
import head from '../../../../fixtures/web-api/policy-revision-head/input.json';
import previous from '../../../../fixtures/web-api/policy-revision-previous/input.json';
import publishedRevision from '../../../../fixtures/web-api/policy-revision-published/input.json';
import afterPublish from '../../../../fixtures/web-api/policy-revisions-list-after-publish/input.json';
import conflictHead from '../../../../fixtures/web-api/policy-revisions-list-conflict-head/input.json';
import revisions from '../../../../fixtures/web-api/policy-revisions-list/input.json';
import {
  expectNoA11yViolations,
  LOCALES,
  localePath,
  ORG_ACME,
  seedStoredTheme,
  SETTINGS_NO_TAB_GRANTS,
  THEMES,
  UNKNOWN_ORG,
  waitForDialogAnimations,
  withVariants,
  type ShellLocale,
} from './support';

const PAGE = 'app-policy-page';
const PHASE = `${PAGE}[data-policy-phase]`;
const ROWS = `${PAGE} [data-policy-revision]`;
const HEAD_ROW = `${ROWS}[data-policy-head]`;
const MODE = `${PAGE} [data-policy-mode]`;
const DIFF = `${PAGE} [data-policy-diff]`;
const DIFF_ROWS = `${DIFF} [data-diff-row]`;
const OPEN_PUBLISH = `${PAGE} [data-policy-publish-open]`;
const DIALOG = '[data-policy-publish-dialog]';
const BODY = '#policy-publish-body';
const SUBMIT = '[data-policy-publish-submit]';
const LOCAL_ERROR = '[data-policy-publish-local-error]';
const SERVER_ERROR = '[data-policy-error]';
const PUBLISHED = '[data-policy-published]';
const CONFLICT = '[role="dialog"]:has(.revision)';

/** The head revision the corpus opens by default (`?revision=…`), and the body it seeds. */
const HEAD_ID = head.body.id;
const HEAD_NO = String(head.body.revision_no);
const SEED = JSON.stringify(head.body.body, null, 2);

/**
 * The body the publish dialog seeds once the post-publish list is served: its head is revision 4,
 * the panel targets the head, and the id-keyed read answers the revision-4 document — so a review
 * edits the current head, not the body the session opened with.
 */
const PUBLISHED_SEED = JSON.stringify(publishedRevision.body.body, null, 2);

/**
 * A draft that differs from the head document in exactly two pointers: `flaky_budget` (changed)
 * and `late_window_minutes` (removed). The 412 envelope carries metadata, never a body, so the
 * conflict dialog can only fill its server column by reading the head that envelope named — these
 * two rows are the browser's proof that the head body was read and compared.
 */
const CONFLICT_DRAFT = ((): string => {
  const body: Record<string, unknown> = { ...head.body.body };
  body['flaky_budget'] = 9;
  delete body['late_window_minutes'];
  return JSON.stringify(body, null, 2);
})();

/** The revision the diff panel's base picker defaults to: the newest one below the head. */
const PREVIOUS_ID = ((): string => {
  const previous = revisions.body.items
    .filter((item) => item.revision_no < Number(HEAD_NO))
    .sort((left, right) => left.revision_no - right.revision_no)
    .at(-1);
  if (previous === undefined) throw new Error('the list corpus has no revision before the head');
  return previous.id;
})();

/** The revision another writer published while this session held `…a003`, per the 412 corpus. */
const CURRENT_HEAD = ((): string => {
  const detail = preconditionFailed.body.error.details.find((row) => row.path === 'revision_id');
  if (detail === undefined) throw new Error('the 412 document names no current revision');
  return detail.reason;
})();

/** The revision number that head carries in the list the same review selects (`=conflict-head`). */
const CURRENT_HEAD_NO = ((): string => {
  const row = conflictHead.body.items.find((item) => item.id === CURRENT_HEAD);
  if (row === undefined) throw new Error('the conflict-head list does not carry that head');
  return String(row.revision_no);
})();

/** The one `details[]` row a refusal document carries (its JSON pointer and reason, verbatim). */
function firstDetail(document: {
  readonly body: {
    readonly error: {
      readonly details: readonly { readonly path: string; readonly reason: string }[];
    };
  };
}): { path: string; reason: string } {
  const [detail] = document.body.error.details;
  if (detail === undefined) throw new Error('the refusal document carries no detail row');
  return detail;
}

const POLICY_WRITE = '?fixture.variant.listPermissions=settings-policy-write';
const POLICY_READONLY = '?fixture.variant.listPermissions=settings-policy-readonly';
const CREATE_412 = '?fixture.variant.createPolicyRevision=precondition_failed';
const CREATE_UNCHANGED = '?fixture.variant.createPolicyRevision=unchanged';
const CREATE_UNKNOWN_FIELD = '?fixture.variant.createPolicyRevision=unknown-field';
const CREATE_CRITICAL = '?fixture.variant.createPolicyRevision=critical-case-quarantined';
const LIST_CONFLICT_HEAD = '?fixture.variant.listPolicyRevisions=conflict-head';
const LIST_AFTER_PUBLISH = '?fixture.variant.listPolicyRevisions=after-publish';
/**
 * A `createPolicyRevision` variant no corpus document answers, so any POST that does leave the app
 * is answered 501 `fixture_not_found` and shows `[data-policy-error]`: the negative control that
 * makes a local refusal's "nothing was sent" observable in the browser.
 */
const UNANSWERED_CREATE = '?fixture.variant.createPolicyRevision=absent';
/** The same negative control for the revision read `open()` performs first. */
const UNANSWERED_GET = '?fixture.variant.getPolicyRevision=absent';

/** A body one byte over the contract's 65 536-byte cap: refused locally, never sent. */
const OVERSIZED_BODY = `{"a":"${'x'.repeat(65_536)}"}`;

/**
 * `/dev/settings-policy/orgs/:orgId` — the review-only address of the real Policy page, with the
 * revision the query parameter opens. `revisionId: null` leaves the parameter off entirely.
 */
function policyReviewPath(
  locale: ShellLocale,
  orgId: string = ORG_ACME,
  revisionId: string | null = HEAD_ID,
): string {
  const path = localePath(locale, `/dev/settings-policy/orgs/${orgId}`);
  return revisionId === null ? path : `${path}?revision=${revisionId}`;
}

/** What the page's own ready contract looks like: rows served, head marked, diff settled. */
async function ready(page: Page, rowCount: number): Promise<void> {
  await expect(page.locator(PHASE)).toHaveAttribute('data-policy-phase', 'ready');
  await expect(page.locator(ROWS)).toHaveCount(rowCount);
  await expect(page.locator(HEAD_ROW)).toHaveCount(1);
  await expect(page.locator(DIFF)).toBeVisible();
  // Options are portalled only while open; the placeholder is not a selectable revision.
  for (const id of ['policy-diff-base', 'policy-diff-target']) {
    const trigger = page.locator(`#${id}`);
    await expect(trigger).toHaveRole('combobox');
    await trigger.click();
    await expect(page.getByRole('listbox').getByRole('option')).toHaveCount(rowCount);
    await page.keyboard.press('Escape');
    await expect(trigger).toHaveAttribute('aria-expanded', 'false');
  }
  await expect(page.locator(DIFF)).not.toHaveAttribute('aria-busy', 'true');
  // The panel compares the two documents the id-keyed read answered, so either outcome is a
  // settled panel: differing rows, or the "no field differences" state when the two bodies agree.
  await expect(page.locator(`${DIFF} p, ${DIFF_ROWS}`).first()).toBeVisible();
}

async function visit(
  page: Page,
  locale: ShellLocale,
  variants: readonly string[] = [POLICY_WRITE],
  revisionId: string | null = HEAD_ID,
  rowCount: number = revisions.body.items.length,
): Promise<void> {
  await page.goto(withVariants(policyReviewPath(locale, ORG_ACME, revisionId), variants));
  await expect(page.locator(PAGE)).toBeVisible();
  await ready(page, rowCount);
}

/** Opens the real publish dialog and waits for its seeded body (the revision it will quote). */
async function openPublish(page: Page, seed: string = SEED): Promise<Locator> {
  await page.locator(OPEN_PUBLISH).click();
  const dialog = page.locator(DIALOG);
  await expect(dialog).toBeVisible();
  await expect(dialog.locator(BODY)).toHaveValue(seed);
  return dialog;
}

async function expectRevisionSelection(page: Page, id: string, revisionId: string): Promise<void> {
  const trigger = page.locator(`#${id}`);
  await trigger.click();
  const options = page.getByRole('listbox').getByRole('option');
  await expect(options).toHaveCount(revisions.body.items.length);
  expect(
    await options.evaluateAll((nodes) => nodes.map((node) => node.getAttribute('data-value'))),
  ).toEqual(revisions.body.items.map((revision) => revision.id));
  const selected = page.getByRole('listbox').getByRole('option', { selected: true });
  await expect(selected).toHaveCount(1);
  await expect(selected).toHaveAttribute('data-value', revisionId);
  const label = (await selected.innerText()).trim();
  await page.keyboard.press('Escape');
  await expect(trigger).toHaveAttribute('aria-expanded', 'false');
  await expect(trigger).toHaveText(label);
}

/** True while the browser's focused element is inside the selector the case is about. */
async function focusInside(page: Page, selector: string): Promise<boolean> {
  return page.evaluate((target) => {
    const element = document.querySelector(target);
    return element instanceof HTMLElement && element.contains(document.activeElement);
  }, selector);
}

for (const locale of LOCALES) {
  // AC3: the served list, its head marking, the mode chip and the diff panel's own state.
  test(`revision list, head marking, mode chip and diff state (${locale})`, async ({ page }) => {
    await visit(page, locale);
    for (const [index, revision] of revisions.body.items.entries()) {
      const row = page.locator(ROWS).nth(index);
      await expect(row).toHaveAttribute('data-revision-no', String(revision.revision_no));
      await expect(row).toHaveAttribute('data-revision-state', revision.state);
      await expect(row.locator('code')).toHaveText(revision.hash.slice(0, 12));
    }
    // Exactly one row is the head the list's ETag named, and it is the newest one.
    const headRow = page.locator(HEAD_ROW);
    await expect(headRow).toHaveCount(1);
    await expect(headRow).toHaveAttribute('data-revision-no', HEAD_NO);
    // No route reads `policy_activation` today (S83 P2): the chip says "unavailable", never a guess.
    await expect(page.locator(MODE)).toHaveAttribute('data-mode', 'unavailable');
    await expect(page.locator(MODE)).toHaveText(/\S/);
    await expect(page.locator('#policy-diff-base')).toBeEnabled();
    await expect(page.locator('#policy-diff-target')).toBeEnabled();
    await expectRevisionSelection(page, 'policy-diff-base', PREVIOUS_ID);
    await expectRevisionSelection(page, 'policy-diff-target', HEAD_ID);
    // The backend answers `getPolicyRevision` by the id in the path (09.7.1-F15 note 3), so the two
    // selections above are two DIFFERENT corpus documents: the panel lists their differences
    // instead of the no-differences state. The rows themselves are the next case's subject.
    await expect(page.locator(DIFF)).toHaveCount(1);
    await expect(page.locator(DIFF_ROWS)).not.toHaveCount(0);
    await expect(page.locator(`${DIFF} [data-policy-diff-truncated]`)).toHaveCount(0);
  });

  // 09.7.1-F15 note (3) + AC4: the revision read is id-keyed, so ONE page yields a real
  // revision-vs-revision comparison — the panel's rows between the previous revision and the head,
  // and the 412 dialog's server/local pair — with no hand-built body pair anywhere.
  test(`two revisions diff in the panel and in the 412 dialog (${locale})`, async ({ page }) => {
    // The list names the base and the head; the corpus documents must BE those two revisions.
    expect(PREVIOUS_ID).toBe(previous.body.id);
    expect(HEAD_ID).toBe(head.body.id);
    await visit(page, locale, [POLICY_WRITE, CREATE_412]);
    await expectRevisionSelection(page, 'policy-diff-base', PREVIOUS_ID);
    await expectRevisionSelection(page, 'policy-diff-target', HEAD_ID);
    // Ten differing pointers between revision 2 and revision 3 of the corpus (one of them,
    // `/required_suites`, twice: `legacy-cart` removed, `storefront-catalog` added).
    const rows = page.locator(DIFF_ROWS);
    await expect(rows).toHaveCount(10);
    const budget = page.locator(`${DIFF_ROWS}[data-diff-path="/flaky_budget"]`);
    await expect(budget).toHaveAttribute('data-diff-kind', 'changed');
    await expect(budget.locator('pre').nth(0)).toHaveText(String(previous.body.body.flaky_budget));
    await expect(budget.locator('pre').nth(1)).toHaveText(String(head.body.body.flaky_budget));
    const freshness = page.locator(`${DIFF_ROWS}[data-diff-path="/freshness_minutes"]`);
    await expect(freshness).toHaveAttribute('data-diff-kind', 'removed');
    await expect(freshness.locator('pre').nth(0)).toHaveText(
      String(previous.body.body.freshness_minutes),
    );
    const quarantine = page.locator(`${DIFF_ROWS}[data-diff-path="/quarantine_ids"]`);
    await expect(quarantine).toHaveAttribute('data-diff-kind', 'added');
    // The key is absent from the previous revision, so the whole ARRAY is the added value: set
    // element rows appear only where both bodies carry the key.
    await expect(quarantine.locator('pre').nth(1)).toHaveText(
      JSON.stringify(head.body.body.quarantine_ids),
    );
    const removedSuite = page.locator(
      `${DIFF_ROWS}[data-diff-path="/required_suites"][data-diff-kind="removed"]`,
    );
    await expect(removedSuite).toHaveCount(1);
    await expect(removedSuite.locator('pre').nth(0)).toHaveText(
      JSON.stringify(
        previous.body.body.required_suites.filter(
          (suite) => !head.body.body.required_suites.includes(suite),
        )[0],
      ),
    );

    // The same id-keyed read feeds the 412 dialog: the head the envelope names has no document of
    // its own (another writer published it), so the variant answers that read, and the two edited
    // pointers come back as a server/local pair carrying the CORPUS's values on the server side.
    const dialog = await openPublish(page);
    await dialog.locator(BODY).fill(CONFLICT_DRAFT);
    await dialog.locator(SUBMIT).click();
    const conflict = page.locator(CONFLICT);
    await expect(conflict).toBeVisible();
    await expect(conflict.locator('.revision code')).toHaveText(CURRENT_HEAD);
    await expect(conflict.locator('.field')).toHaveCount(2);
    const changed = conflict.locator('.field', { hasText: '/flaky_budget' });
    await expect(changed.locator('.server-value')).toContainText(
      String(head.body.body.flaky_budget),
    );
    await expect(changed.locator('.local-value')).toContainText('9');
    const removed = conflict.locator('.field', { hasText: '/late_window_minutes' });
    await expect(removed.locator('.server-value')).toContainText(
      String(head.body.body.late_window_minutes),
    );
    await expect(page.locator(SERVER_ERROR)).toHaveCount(0);
    await expect(page.locator(PUBLISHED)).toHaveCount(0);
  });

  // AC3 (S83 P2): a revision id that is not UUID-shaped is the "no revision selected" state, and
  // the shape check runs BEFORE any read. `getPolicyRevision=absent` would answer 501 for a fired
  // read, which settles the page to `error` — so an idle page is the browser-visible proof.
  test(`a revision id that is not a UUID requests nothing (${locale})`, async ({ page }) => {
    await page.goto(
      withVariants(policyReviewPath(locale, ORG_ACME, 'not-a-uuid'), [
        POLICY_WRITE,
        UNANSWERED_GET,
      ]),
    );
    await expect(page.locator(PAGE)).toBeVisible();
    await expect(page.locator(PHASE)).toHaveAttribute('data-policy-phase', 'idle');
    await expect(page.locator('#policy-revision-id')).toHaveValue('not-a-uuid');
    await expect(page.locator(ROWS)).toHaveCount(0);
    await expect(page.locator(DIFF)).toHaveCount(0);
    await expect(page.locator(PUBLISHED)).toHaveCount(0);
    // A read that did leave the app settles `error` (501) or `not_found`; the tab stays untouched.
    await page.waitForTimeout(600);
    await expect(page.locator(PHASE)).toHaveAttribute('data-policy-phase', 'idle');
    await expect(page.locator(ROWS)).toHaveCount(0);
  });

  // AC1/P1: the publish control is derived from `policy:write` and absent — not disabled — without
  // it, while the read side of the tab keeps working. The DIALOG is part of that control: AC1 is
  // about the DOM, so its dialog, textarea and submit button must not be rendered either.
  test(`read-only grant offers no publish control (${locale})`, async ({ page }) => {
    await visit(page, locale, [POLICY_READONLY]);
    await expect(page.locator(OPEN_PUBLISH)).toHaveCount(0);
    await expect(page.locator(DIALOG)).toHaveCount(0);
    await expect(page.locator(BODY)).toHaveCount(0);
    await expect(page.locator(SUBMIT)).toHaveCount(0);
    await expect(page.locator(`${PAGE} [data-policy-narrow-notice]`)).toHaveCount(0);
    await expect(page.locator(ROWS)).toHaveCount(revisions.body.items.length);
    await expect(page.locator(MODE)).toBeVisible();
  });

  // AC4/T4.3: the seeded body, the 201 confirmation and the reloaded head from the server's list.
  test(`publish 201: seeded body, confirmation and the reloaded head (${locale})`, async ({
    page,
  }) => {
    await visit(
      page,
      locale,
      [POLICY_WRITE, LIST_AFTER_PUBLISH],
      HEAD_ID,
      afterPublish.body.items.length,
    );
    const dialog = await openPublish(page, PUBLISHED_SEED);
    // The post-publish list's head is revision 4, so the panel compares the head document the
    // session opened (a003) against the revision-4 document — a real pair of two corpus documents.
    // Its `flaky_budget` is the published revision's own value, not the head's.
    const budgetRow = page.locator(`${DIFF_ROWS}[data-diff-path="/flaky_budget"]`);
    await expect(budgetRow).toHaveCount(1);
    await expect(budgetRow.locator('pre').nth(0)).toHaveText(String(head.body.body.flaky_budget));
    await expect(budgetRow.locator('pre').nth(1)).toHaveText(
      String(publishedRevision.body.body.flaky_budget),
    );
    await dialog.locator(SUBMIT).click();
    const published = page.locator(PUBLISHED);
    await expect(published).toBeVisible();
    await expect(published).toHaveText(/\S/);
    await expect(dialog).not.toBeVisible();
    await expect(dialog.locator(SERVER_ERROR)).toHaveCount(0);
    // The head is the server's: only the post-publish list answer moves it to the new revision.
    await expect(page.locator(ROWS)).toHaveCount(afterPublish.body.items.length);
    const headRow = page.locator(HEAD_ROW);
    await expect(headRow).toHaveCount(1);
    await expect(headRow).toHaveAttribute('data-revision-no', String(created.body.revision_no));
    await expect(page.locator(PHASE)).toHaveAttribute('data-policy-phase', 'ready');
  });

  // AC4: a `200` means the body was already stored — the confirmation must not claim a new row.
  test(`publish 200 unchanged: confirmation without a new revision (${locale})`, async ({
    page,
  }) => {
    await visit(page, locale, [POLICY_WRITE, CREATE_UNCHANGED]);
    const dialog = await openPublish(page);
    await dialog.locator(SUBMIT).click();
    await expect(page.locator(PUBLISHED)).toBeVisible();
    await expect(dialog).not.toBeVisible();
    await expect(page.locator(ROWS)).toHaveCount(revisions.body.items.length);
    const headRow = page.locator(HEAD_ROW);
    await expect(headRow).toHaveCount(1);
    await expect(headRow).toHaveAttribute('data-revision-no', String(unchanged.body.revision_no));
    const appendedRow = `${ROWS}[data-revision-no="${created.body.revision_no}"]`;
    await expect(page.locator(appendedRow)).toHaveCount(0);
  });

  // AC4: a 412 opens the shared conflict dialog with the head the server named AND the head
  // document's own values for every differing pointer, offers the reload explicitly, and never
  // resubmits on its own.
  test(`publish 412: conflict diff, explicit reload, no retry (${locale})`, async ({ page }) => {
    await visit(
      page,
      locale,
      [POLICY_WRITE, CREATE_412, LIST_CONFLICT_HEAD],
      HEAD_ID,
      conflictHead.body.items.length,
    );
    // The session's list already shows the revision another writer published, and the head the
    // ETag named moved with it — the state a stale attempt must not be silently resubmitted against.
    const headRow = page.locator(HEAD_ROW);
    await expect(headRow).toHaveCount(1);
    await expect(headRow).toHaveAttribute('data-revision-no', CURRENT_HEAD_NO);
    const dialog = await openPublish(page);
    await expect(dialog.locator(BODY)).toHaveValue(SEED);
    // The draft is edited so the two bodies differ. The 412 envelope has no body, so a filled
    // server column can only come from a completed read of the head revision it named.
    await dialog.locator(BODY).fill(CONFLICT_DRAFT);
    const draft = await dialog.locator(BODY).inputValue();
    expect(draft).toBe(CONFLICT_DRAFT);
    await dialog.locator(SUBMIT).click();
    const conflict = page.locator(CONFLICT);
    await expect(conflict).toHaveCount(1);
    await expect(conflict).toBeVisible();
    // The current revision is the server's `details[]` value, verbatim; nothing else is inferred.
    await expect(conflict.locator('.revision code')).toHaveText(CURRENT_HEAD);
    await expect(conflict.locator('.revision code')).not.toHaveText(HEAD_ID);
    // One changed and one removed pointer, each with the SERVER's value on the right.
    await expect(conflict.locator('.field')).toHaveCount(2);
    const changed = conflict.locator('.field', { hasText: '/flaky_budget' });
    await expect(changed.locator('.server-value')).toContainText(
      String(head.body.body.flaky_budget),
    );
    await expect(changed.locator('.local-value')).toContainText('9');
    const removed = conflict.locator('.field', { hasText: '/late_window_minutes' });
    await expect(removed.locator('.server-value')).toContainText(
      String(head.body.body.late_window_minutes),
    );
    await expect(page.locator(SERVER_ERROR)).toHaveCount(0);
    await expect(page.locator(PUBLISHED)).toHaveCount(0);
    await expect(dialog).not.toBeVisible();
    // Reload is a named control the user must press; it is never automatic.
    const reload = conflict.locator('button.reload');
    await expect(reload).toBeEnabled();
    await expect(reload).toHaveText(/\S/);
    // Returning to the draft keeps the text and appends no second attempt.
    await conflict.locator('footer button:not(.reload)').click();
    await expect(conflict).not.toBeVisible();
    await expect(dialog).toBeVisible();
    await expect(dialog.locator(BODY)).toHaveValue(draft);
    await page.waitForTimeout(750);
    await expect(conflict).not.toBeVisible();
    await expect(page.locator(PUBLISHED)).toHaveCount(0);
    await expect(page.locator(SERVER_ERROR)).toHaveCount(0);
  });

  // AC5: the closed schema's 422 is rendered VERBATIM (code, message, pointer and reason).
  test(`publish 422 unknown field is verbatim (${locale})`, async ({ page }) => {
    await visit(page, locale, [POLICY_WRITE, CREATE_UNKNOWN_FIELD]);
    const dialog = await openPublish(page);
    await dialog.locator(SUBMIT).click();
    const alert = dialog.locator(SERVER_ERROR);
    await expect(alert).toBeVisible();
    await expect(alert).toHaveAttribute('data-error-code', unknownField.body.error.code);
    await expect(alert.locator('code').first()).toHaveText(unknownField.body.error.code);
    await expect(alert.locator('p').first()).toHaveText(unknownField.body.error.message);
    const detail = firstDetail(unknownField);
    await expect(alert.locator('li code')).toHaveText(detail.path);
    await expect(alert.locator('li p')).toHaveText(detail.reason);
    await expect(dialog).toBeVisible();
    await expect(page.locator(PUBLISHED)).toHaveCount(0);
    await expect(dialog.locator(LOCAL_ERROR)).toHaveCount(0);
  });

  // AC5: `critical_case_quarantined` is a server refusal, never a local guess or a swallow.
  test(`publish 422 critical case is verbatim (${locale})`, async ({ page }) => {
    await visit(page, locale, [POLICY_WRITE, CREATE_CRITICAL]);
    const dialog = await openPublish(page);
    await dialog.locator(SUBMIT).click();
    const alert = dialog.locator(SERVER_ERROR);
    await expect(alert).toBeVisible();
    await expect(alert).toHaveAttribute('data-error-code', critical.body.error.code);
    await expect(alert.locator('code').first()).toHaveText(critical.body.error.code);
    await expect(alert.locator('p').first()).toHaveText(critical.body.error.message);
    const detail = firstDetail(critical);
    await expect(alert.locator('li code')).toHaveText(detail.path);
    await expect(alert.locator('li p')).toHaveText(detail.reason);
    await expect(dialog).toBeVisible();
    await expect(page.locator(PUBLISHED)).toHaveCount(0);
  });

  // AC4/P4: the two local refusals happen BEFORE any request; `createPolicyRevision=absent` would
  // answer 501 and show the server-refusal region, so its absence is the browser's proof.
  test(`local refusals send nothing and keep the editor open (${locale})`, async ({ page }) => {
    await visit(page, locale, [POLICY_WRITE, UNANSWERED_CREATE]);
    const dialog = await openPublish(page);
    const body = dialog.locator(BODY);
    const local = dialog.locator(LOCAL_ERROR);
    await body.fill('{');
    await dialog.locator(SUBMIT).click();
    await expect(local).toBeVisible();
    await expect(local).toHaveText(/\S/);
    await expect(body).toHaveAttribute('aria-invalid', 'true');
    await expect(dialog.locator(SERVER_ERROR)).toHaveCount(0);
    await expect(dialog).toBeVisible();
    // 65 536 bytes are the cap: one byte more is refused with nothing sent.
    await body.fill(OVERSIZED_BODY);
    await expect(body).not.toHaveAttribute('aria-invalid', 'true');
    await dialog.locator(SUBMIT).click();
    await expect(local).toBeVisible();
    await expect(local).toHaveText(/\S/);
    await expect(dialog.locator(SERVER_ERROR)).toHaveCount(0);
    await expect(dialog).toBeVisible();
    await expect(page.locator(PUBLISHED)).toHaveCount(0);
  });

  // AC3/P6: below 768px Settings is inspection-only, never a squeezed mutation surface.
  test(`360px is inspection-only (${locale})`, async ({ page }) => {
    await page.setViewportSize({ width: 360, height: 800 });
    await visit(page, locale);
    const notice = page.locator(`${PAGE} [data-policy-narrow-notice]`);
    await expect(notice).toBeVisible();
    await expect(notice).toHaveText(/\S/);
    // CSS-hidden, and therefore not a keyboard stop either.
    await expect(page.locator(OPEN_PUBLISH)).toBeHidden();
    await expect(page.locator(`${ROWS}:visible`)).toHaveCount(revisions.body.items.length);
    await expect(page.locator(DIFF)).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(
      true,
    );
    await expectNoA11yViolations(page, `Policy inspection-only ${locale} 360px`);
  });

  // AC3 + native dialog semantics: Enter opens, the trap holds, Escape closes to the opener.
  test(`keyboard publish: Enter opens, trap holds, Escape restores focus (${locale})`, async ({
    page,
  }) => {
    await visit(page, locale);
    const opener = page.locator(OPEN_PUBLISH);
    const dialog = page.locator(DIALOG);
    await opener.focus();
    await expect(opener).toBeFocused();
    await page.keyboard.press('Enter');
    await expect(dialog).toBeVisible();
    await expect(dialog.locator(BODY)).toHaveValue(SEED);
    await expect.poll(() => focusInside(page, DIALOG)).toBe(true);
    // Two full cycles over the dialog's three stops (body, Cancel, Publish) prove the wrapping edge.
    for (let stop = 0; stop < 6; stop++) {
      await page.keyboard.press('Tab');
      await expect.poll(() => focusInside(page, DIALOG)).toBe(true);
    }
    await page.keyboard.press('Shift+Tab');
    await expect.poll(() => focusInside(page, DIALOG)).toBe(true);
    await page.keyboard.press('Shift+Tab');
    await expect.poll(() => focusInside(page, DIALOG)).toBe(true);
    await page.keyboard.press('Escape');
    await expect(dialog).not.toBeVisible();
    await expect(opener).toBeFocused();
    await expect(page.locator(PUBLISHED)).toHaveCount(0);
  });

  // AC1: the review address omits only the phase gate — never authentication, org scope or a read
  // grant. A missing `policy:read` lands on the 403 pattern; a foreign org is the same 404 any
  // address that never existed answers, so the review address cannot confirm it exists.
  test(`review route refuses missing read grant and foreign org (${locale})`, async ({ page }) => {
    await page.goto(withVariants(policyReviewPath(locale), [SETTINGS_NO_TAB_GRANTS]));
    await expect(page.locator('app-forbidden-pattern')).toBeVisible();
    await expect(page.locator(PAGE)).toHaveCount(0);
    await page.goto(withVariants(policyReviewPath(locale, UNKNOWN_ORG), [POLICY_WRITE]));
    await expect(page.locator('app-not-found-pattern')).toBeVisible();
    await expect(page.locator(PAGE)).toHaveCount(0);
  });

  for (const theme of THEMES) {
    // AC3/T4: real-browser WCAG 2.2 AA — contrast included — on the list/diff state, the open
    // publish dialog and the conflict dialog it opens, in every locale and theme.
    test(`axe has no violations on list/diff, publish and conflict (${locale}, ${theme})`, async ({
      page,
    }) => {
      await seedStoredTheme(page, theme);
      await visit(page, locale, [POLICY_WRITE, CREATE_412]);
      await expect(page.locator('html')).toHaveAttribute('lang', locale);
      await expect(page.locator('html')).toHaveAttribute('data-theme', theme);
      await expectNoA11yViolations(page, `Policy list/diff ${locale} ${theme}`);
      for (const id of ['policy-diff-base', 'policy-diff-target']) {
        const trigger = page.locator(`#${id}`);
        await trigger.click();
        await expect(page.getByRole('listbox')).toHaveAccessibleName(
          await page.locator(`label[for="${id}"]`).innerText(),
        );
        // The portal mounts closed; its entrance animation starts only after the open state.
        await waitForDialogAnimations(page, 'hlm-select-content[data-state="open"]');
        await expectNoA11yViolations(page, `Policy ${id} options ${locale} ${theme}`);
        await page.keyboard.press('Escape');
        await expect(page.getByRole('listbox')).toHaveCount(0);
      }
      const dialog = await openPublish(page);
      await waitForDialogAnimations(page, DIALOG);
      await expectNoA11yViolations(page, `Policy publish dialog ${locale} ${theme}`);
      await dialog.locator(SUBMIT).click();
      const conflict = page.locator(CONFLICT);
      await expect(conflict).toBeVisible();
      await waitForDialogAnimations(page, CONFLICT);
      await expectNoA11yViolations(page, `Policy conflict dialog ${locale} ${theme}`);
      await expect(page.locator(SERVER_ERROR)).toHaveCount(0);
    });
  }
}
