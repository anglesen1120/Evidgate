// Real Policy page against the in-process fixture backend, under normal shell and grant gates.
// Unanswered fixture variants prove local validation makes no request; revision reads are ID-keyed.
import { expect, test } from '@playwright/test';

import created from '../../../../fixtures/web-api/policy-revision-create/input.json';
import critical from '../../../../fixtures/web-api/policy-revision-create-critical-case-quarantined/input.json';
import unchanged from '../../../../fixtures/web-api/policy-revision-create-unchanged/input.json';
import unknownField from '../../../../fixtures/web-api/policy-revision-create-unknown-field/input.json';
import activationRead from '../../../../fixtures/web-api/policy-activation-read-ok/input.json';
import head from '../../../../fixtures/web-api/policy-revision-head/input.json';
import publishedRevision from '../../../../fixtures/web-api/policy-revision-published/input.json';
import previous from '../../../../fixtures/web-api/policy-revision-previous/input.json';
import afterPublish from '../../../../fixtures/web-api/policy-revisions-list-after-publish/input.json';
import conflictHead from '../../../../fixtures/web-api/policy-revisions-list-conflict-head/input.json';
import revisions from '../../../../fixtures/web-api/policy-revisions-list/input.json';
import {
  expectNoA11yViolations,
  LOCALES,
  ORG_ACME,
  seedStoredTheme,
  SETTINGS_NO_TAB_GRANTS,
  THEMES,
  UNKNOWN_ORG,
  waitForDialogAnimations,
  withVariants,
  type ShellLocale,
} from './support';

import {
  PAGE,
  PHASE,
  ROWS,
  HEAD_ROW,
  MODE,
  SUMMARY,
  ACTIVATION,
  ACTIVATION_REVISION,
  HEAD_READ,
  HEAD_READ_NO,
  READS_RETRY,
  DIFF,
  DIFF_ROWS,
  OPEN_PUBLISH,
  DIALOG,
  BODY,
  SUBMIT,
  LOCAL_ERROR,
  SERVER_ERROR,
  PUBLISHED,
  CONFLICT,
  HEAD_ID,
  HEAD_NO,
  SEED,
  PUBLISHED_SEED,
  CONFLICT_DRAFT,
  PREVIOUS_ID,
  CURRENT_HEAD,
  CURRENT_HEAD_NO,
  firstDetail,
  POLICY_WRITE,
  POLICY_READONLY,
  ACTIVATION_OK,
  ACTIVATION_ENFORCING,
  ACTIVATION_NOT_FOUND,
  ACTIVATION_FORBIDDEN,
  ACTIVATION_RATE_LIMITED,
  HEAD_NOT_FOUND,
  CREATE_412,
  CREATE_UNCHANGED,
  CREATE_UNKNOWN_FIELD,
  CREATE_CRITICAL,
  LIST_CONFLICT_HEAD,
  LIST_AFTER_PUBLISH,
  UNANSWERED_CREATE,
  UNANSWERED_GET,
  OVERSIZED_BODY,
  policyReviewPath,
  visit,
  openPublish,
  expectRevisionSelection,
  focusInside,
} from './settings-policy.support';

/**
 * The summary's own labels, per bundle: the one thing these cases assert by text, because the point
 * of the pair is that the same data renders in both product languages.
 */
const LABELS: Record<ShellLocale, { summary: string; activation: string; head: string }> = {
  en: { summary: 'Project policy', activation: 'Activation', head: 'Head revision' },
  vi: { summary: 'Chính sách của dự án', activation: 'Kích hoạt', head: 'Phiên bản mới nhất' },
};

for (const locale of LOCALES) {
  test(`revision form keeps its field beside the action on desktop (${locale})`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await visit(page, locale);
    const field = await page.locator('#policy-revision-id').boundingBox();
    const action = await page.locator('[data-policy-open]').boundingBox();
    if (field === null || action === null)
      throw new Error('policy revision field or action has no box');
    expect(field.y + field.height).toBeCloseTo(action.y + action.height, 0);
    expect(field.x + field.width).toBeLessThan(action.x);
    expect(field.width).toBeGreaterThan(action.width);
  });
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
    // The activation read serves `advisory` for the review project, so the chip shows that mode
    // verbatim — never a mode inferred from a revision's `state`.
    await expect(page.locator(MODE)).toHaveAttribute('data-mode', activationRead.body.mode);
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

  // The activation and head reads (both project-scoped): the summary renders what the server said,
  // with the same hooks in both bundles and each read's own state.
  test(`policy summary renders the mode and both revisions (${locale})`, async ({ page }) => {
    await visit(page, locale, [POLICY_WRITE, ACTIVATION_OK]);
    const copy = LABELS[locale];
    await expect(page.locator(`${SUMMARY} h3`)).toHaveText(copy.summary);
    await expect(page.locator(`${ACTIVATION} dt`)).toHaveText(copy.activation);
    await expect(page.locator(`${HEAD_READ} dt`)).toHaveText(copy.head);
    await expect(page.locator(MODE)).toHaveAttribute('data-mode', activationRead.body.mode);
    // The revision in force is the activation's own id, never a row's `state`.
    await expect(page.locator(ACTIVATION_REVISION)).toHaveText(
      activationRead.body.policy_revision_id,
    );
    // The head read names the newest revision, the same one the list marks as its head.
    await expect(page.locator(HEAD_READ_NO)).toHaveAttribute('data-revision-no', HEAD_NO);
    await expect(page.locator(`${HEAD_READ} code`)).toHaveText(head.body.id);
    await expectNoA11yViolations(page, `policy summary (${locale})`);
  });

  test(`an enforcing activation renders the gating chip (${locale})`, async ({ page }) => {
    await visit(page, locale, [POLICY_WRITE, ACTIVATION_ENFORCING]);
    await expect(page.locator(MODE)).toHaveAttribute('data-mode', 'enforcing');
  });

  // A project without an activation is a normal project: absence is not an error, and the rows the
  // list served stay on screen.
  test(`no activation shows the empty state and keeps the list (${locale})`, async ({ page }) => {
    await visit(page, locale, [POLICY_WRITE, ACTIVATION_NOT_FOUND]);
    await expect(page.locator(ACTIVATION)).toHaveAttribute('data-policy-activation-state', 'empty');
    await expect(page.locator(`${ACTIVATION} [data-policy-activation-empty]`)).toHaveText(/\S/);
    await expect(page.locator(MODE)).toHaveCount(0);
    await expect(page.locator(ROWS)).toHaveCount(revisions.body.items.length);
    await expect(page.locator(PHASE)).toHaveAttribute('data-policy-phase', 'ready');
  });

  test(`an activation the caller may not read is forbidden, not retryable (${locale})`, async ({
    page,
  }) => {
    await visit(page, locale, [POLICY_WRITE, ACTIVATION_FORBIDDEN]);
    await expect(page.locator(ACTIVATION)).toHaveAttribute(
      'data-policy-activation-state',
      'forbidden',
    );
    await expect(page.locator(READS_RETRY)).toHaveCount(0);
    await expect(page.locator(ROWS)).not.toHaveCount(0);
  });

  test(`a throttled activation read offers a retry that re-reads it (${locale})`, async ({
    page,
  }) => {
    await visit(page, locale, [POLICY_WRITE, ACTIVATION_RATE_LIMITED]);
    await expect(page.locator(ACTIVATION)).toHaveAttribute('data-policy-activation-state', 'error');
    await expect(page.locator(`${SUMMARY} [data-policy-reads-error]`)).toContainText(
      'rate_limited',
    );
    // The fixture backend answers in-process, so no network request shows the re-read: record the
    // state attribute instead and require the retry to pass through `loading` before it settles.
    await page.locator(ACTIVATION).evaluate((element) => {
      const seen: string[] = [];
      Object.assign(window, { __activationStates: seen });
      new MutationObserver(() => {
        seen.push(element.getAttribute('data-policy-activation-state') ?? '');
      }).observe(element, { attributes: true, attributeFilter: ['data-policy-activation-state'] });
    });
    await page.locator(READS_RETRY).click();
    await expect
      .poll(() => page.evaluate(() => Reflect.get(window, '__activationStates') as string[]))
      .toContain('loading');
    // The variant still refuses, so the error stands: the retry re-issued the read, it did not
    // clear the state optimistically.
    await expect(page.locator(ACTIVATION)).toHaveAttribute('data-policy-activation-state', 'error');
  });

  // The head read's own absence, with the list still served: the two reads are independent.
  test(`a project with no head revision shows the empty head state (${locale})`, async ({
    page,
  }) => {
    await visit(page, locale, [POLICY_WRITE, HEAD_NOT_FOUND]);
    await expect(page.locator(HEAD_READ)).toHaveAttribute('data-policy-head-state', 'empty');
    await expect(page.locator(HEAD_READ_NO)).toHaveCount(0);
    await expect(page.locator(ROWS)).not.toHaveCount(0);
    await expect(page.locator(ACTIVATION)).toHaveAttribute('data-policy-activation-state', 'ready');
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
