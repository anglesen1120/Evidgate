// The two project-scoped reads (activation, head): a refusal is a summary state, not an error.
import { expect, test } from '@playwright/test';

import activationRead from '../../../../fixtures/web-api/policy-activation-read-ok/input.json';
import head from '../../../../fixtures/web-api/policy-revision-head/input.json';
import revisions from '../../../../fixtures/web-api/policy-revisions-list/input.json';
import { expectNoA11yViolations, LOCALES, type ShellLocale } from './support';

import {
  PHASE,
  ROWS,
  MODE,
  SUMMARY,
  ACTIVATION,
  ACTIVATION_REVISION,
  HEAD_READ,
  HEAD_READ_NO,
  READS_RETRY,
  HEAD_NO,
  POLICY_WRITE,
  ACTIVATION_OK,
  ACTIVATION_ENFORCING,
  ACTIVATION_NOT_FOUND,
  ACTIVATION_FORBIDDEN,
  ACTIVATION_RATE_LIMITED,
  HEAD_NOT_FOUND,
  visit,
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

  test(`a throttled activation read keeps its retry closed until the wait ends (${locale})`, async ({
    page,
  }) => {
    await visit(page, locale, [POLICY_WRITE, ACTIVATION_RATE_LIMITED]);
    await expect(page.locator(ACTIVATION)).toHaveAttribute('data-policy-activation-state', 'error');
    await expect(page.locator(`${SUMMARY} [data-policy-reads-error]`)).toContainText(
      'rate_limited',
    );
    // The fixture answers 429 with `Retry-After: 30`, so the control is on screen, focusable and
    // inert: an early press must spend itself, not fire a second read.
    const retry = page.locator(READS_RETRY);
    await expect(retry).toHaveAttribute('aria-disabled', 'true');
    await expect(retry).not.toHaveAttribute('disabled', '');
    await expect(page.locator(`${SUMMARY} [data-policy-reads-waiting]`)).toHaveText(/\S/);

    await retry.focus();
    await retry.press('Enter');
    await expect(retry).toHaveAttribute('aria-disabled', 'true');
    await expect(page.locator(ACTIVATION)).toHaveAttribute('data-policy-activation-state', 'error');
    // A press that could not run must not hand the focus back to `body`.
    expect(await page.evaluate(() => document.activeElement?.tagName)).not.toBe('BODY');
    expect(
      await page.evaluate(() => {
        const active = document.activeElement;
        return active instanceof HTMLElement && active.hasAttribute('data-policy-reads-retry');
      }),
    ).toBe(true);
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
}
