import { expect, type Locator, type Page } from '@playwright/test';
import preconditionFailed from '../../../../fixtures/web-api/policy-revision-create-precondition-failed/input.json';
import head from '../../../../fixtures/web-api/policy-revision-head/input.json';
import publishedRevision from '../../../../fixtures/web-api/policy-revision-published/input.json';
import conflictHead from '../../../../fixtures/web-api/policy-revisions-list-conflict-head/input.json';
import revisions from '../../../../fixtures/web-api/policy-revisions-list/input.json';
import { localePath, ORG_ACME, withVariants, type ShellLocale } from './support';

export const PAGE = 'app-policy-page';
export const PHASE = `${PAGE}[data-policy-phase]`;
export const ROWS = `${PAGE} [data-policy-revision]`;
export const HEAD_ROW = `${ROWS}[data-policy-head]`;
export const MODE = `${PAGE} [data-policy-mode]`;
export const DIFF = `${PAGE} [data-policy-diff]`;
export const DIFF_ROWS = `${DIFF} [data-diff-row]`;
export const OPEN_PUBLISH = `${PAGE} [data-policy-publish-open]`;
export const DIALOG = '[data-policy-publish-dialog]';
export const BODY = '#policy-publish-body';
export const SUBMIT = '[data-policy-publish-submit]';
export const LOCAL_ERROR = '[data-policy-publish-local-error]';
export const SERVER_ERROR = '[data-policy-error]';
export const PUBLISHED = '[data-policy-published]';
export const CONFLICT = '[role="dialog"]:has(.revision)';

/** The head revision the corpus opens by default (`?revision=…`), and the body it seeds. */
export const HEAD_ID = head.body.id;
export const HEAD_NO = String(head.body.revision_no);
export const SEED = JSON.stringify(head.body.body, null, 2);

/**
 * The body the publish dialog seeds once the post-publish list is served: its head is revision 4,
 * the panel targets the head, and the id-keyed read answers the revision-4 document — so a review
 * edits the current head, not the body the session opened with.
 */
export const PUBLISHED_SEED = JSON.stringify(publishedRevision.body.body, null, 2);

/**
 * A draft that differs from the head document in exactly two pointers: `flaky_budget` (changed)
 * and `late_window_minutes` (removed). The 412 envelope carries metadata, never a body, so the
 * conflict dialog can only fill its server column by reading the head that envelope named — these
 * two rows are the browser's proof that the head body was read and compared.
 */
export const CONFLICT_DRAFT = ((): string => {
  const body: Record<string, unknown> = { ...head.body.body };
  body['flaky_budget'] = 9;
  delete body['late_window_minutes'];
  return JSON.stringify(body, null, 2);
})();

/** The revision the diff panel's base picker defaults to: the newest one below the head. */
export const PREVIOUS_ID = ((): string => {
  const previous = revisions.body.items
    .filter((item) => item.revision_no < Number(HEAD_NO))
    .sort((left, right) => left.revision_no - right.revision_no)
    .at(-1);
  if (previous === undefined) throw new Error('the list corpus has no revision before the head');
  return previous.id;
})();

/** The revision another writer published while this session held `…a003`, per the 412 corpus. */
export const CURRENT_HEAD = ((): string => {
  const detail = preconditionFailed.body.error.details.find((row) => row.path === 'revision_id');
  if (detail === undefined) throw new Error('the 412 document names no current revision');
  return detail.reason;
})();

/** The revision number that head carries in the list the same review selects (`=conflict-head`). */
export const CURRENT_HEAD_NO = ((): string => {
  const row = conflictHead.body.items.find((item) => item.id === CURRENT_HEAD);
  if (row === undefined) throw new Error('the conflict-head list does not carry that head');
  return String(row.revision_no);
})();

/** The one `details[]` row a refusal document carries (its JSON pointer and reason, verbatim). */
export function firstDetail(document: {
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

export const POLICY_WRITE = '?fixture.variant.listPermissions=settings-policy-write';
export const POLICY_READONLY = '?fixture.variant.listPermissions=settings-policy-readonly';
export const CREATE_412 = '?fixture.variant.createPolicyRevision=precondition_failed';
export const CREATE_UNCHANGED = '?fixture.variant.createPolicyRevision=unchanged';
export const CREATE_UNKNOWN_FIELD = '?fixture.variant.createPolicyRevision=unknown-field';
export const CREATE_CRITICAL = '?fixture.variant.createPolicyRevision=critical-case-quarantined';
export const LIST_CONFLICT_HEAD = '?fixture.variant.listPolicyRevisions=conflict-head';
export const LIST_AFTER_PUBLISH = '?fixture.variant.listPolicyRevisions=after-publish';
/**
 * A `createPolicyRevision` variant no corpus document answers, so any POST that does leave the app
 * is answered 501 `fixture_not_found` and shows `[data-policy-error]`: the negative control that
 * makes a local refusal's "nothing was sent" observable in the browser.
 */
export const UNANSWERED_CREATE = '?fixture.variant.createPolicyRevision=absent';
/** The same negative control for the revision read `open()` performs first. */
export const UNANSWERED_GET = '?fixture.variant.getPolicyRevision=absent';

/** A body one byte over the contract's 65 536-byte cap: refused locally, never sent. */
export const OVERSIZED_BODY = `{"a":"${'x'.repeat(65_536)}"}`;

/**
 * `/dev/settings-policy/orgs/:orgId` — the review-only address of the real Policy page, with the
 * revision the query parameter opens. `revisionId: null` leaves the parameter off entirely.
 */
export function policyReviewPath(
  locale: ShellLocale,
  orgId: string = ORG_ACME,
  revisionId: string | null = HEAD_ID,
): string {
  const path = localePath(locale, `/dev/settings-policy/orgs/${orgId}`);
  return revisionId === null ? path : `${path}?revision=${revisionId}`;
}

/** What the page's own ready contract looks like: rows served, head marked, diff settled. */
export async function ready(page: Page, rowCount: number): Promise<void> {
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
    await expect(page.getByRole('listbox', { includeHidden: true })).toHaveCount(0);
  }
  await expect(page.locator(DIFF)).not.toHaveAttribute('aria-busy', 'true');
  // The panel compares the two documents the id-keyed read answered, so either outcome is a
  // settled panel: differing rows, or the "no field differences" state when the two bodies agree.
  await expect(page.locator(`${DIFF} p, ${DIFF_ROWS}`).first()).toBeVisible();
}

export async function visit(
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
export async function openPublish(page: Page, seed: string = SEED): Promise<Locator> {
  await page.locator(OPEN_PUBLISH).click();
  const dialog = page.locator(DIALOG);
  await expect(dialog).toBeVisible();
  await expect(dialog.locator(BODY)).toHaveValue(seed);
  return dialog;
}

export async function expectRevisionSelection(
  page: Page,
  id: string,
  revisionId: string,
): Promise<void> {
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
export async function focusInside(page: Page, selector: string): Promise<boolean> {
  return page.evaluate((target) => {
    const element = document.querySelector(target);
    return element instanceof HTMLElement && element.contains(document.activeElement);
  }, selector);
}
