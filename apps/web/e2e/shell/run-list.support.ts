import { expect, type Page } from '@playwright/test';

/** Both approved table presentations carry the same served row identity. */
export const RUN_RECORDS = 'app-run-list-table [data-row-id]';
export const RUN_FILTER_SUMMARY = '[data-run-filter-disclosure] > summary';

/** A narrow filter disclosure is opened through its native keyboard interaction. */
export async function openRunFilters(page: Page): Promise<void> {
  const disclosure = page.locator('[data-run-filter-disclosure]');
  const summary = page.locator(RUN_FILTER_SUMMARY);
  await summary.focus();
  if ((await disclosure.getAttribute('open')) === null) {
    await page.keyboard.press('Enter');
  }
  await expect(disclosure).toHaveAttribute('open', '');
}
