import { expect, type Locator, type Page } from '@playwright/test';

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

export async function expectCenteredRunIdentity(row: Locator): Promise<void> {
  const offsets = await row.locator('[data-run-row]').evaluate((block) => {
    const cell = block.closest('td');
    const label = block.querySelector('a > span');
    const time = block.querySelector('time');
    if (!cell || !label || !time) throw new Error('Missing run identity or timestamp');
    const bounds = cell.getBoundingClientRect();
    const center = (bounds.top + bounds.bottom) / 2;
    const wrapper = block.getBoundingClientRect();
    return [
      (wrapper.top + wrapper.bottom) / 2 - center,
      (label.getBoundingClientRect().top + time.getBoundingClientRect().bottom) / 2 - center,
    ];
  });
  for (const offset of offsets) expect(Math.abs(offset)).toBeLessThanOrEqual(2);
}

export async function expectSingleLineScope(page: Page): Promise<void> {
  const table = page.locator('app-run-list-table');
  const header = table.locator('th.cdk-column-scope');
  const label = table.locator('.scope-cell > span:not([data-run-rerun], [data-run-reprocess])');
  for (const item of [header, label.first(), table.locator('th.cdk-column-actions')]) {
    await expect(item).toBeVisible();
    const lines = await item.evaluate((element) => {
      const range = document.createRange();
      range.selectNodeContents(element);
      return range.getClientRects().length;
    });
    expect(lines).toBe(1);
  }
}
