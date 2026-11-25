import { expect, type Locator, type Page } from '@playwright/test';
import type { ShellLocale } from './support';

/** Both approved table presentations carry the same served row identity. */
export const RUN_RECORDS = 'app-run-list-table [data-row-id]';
export const RUN_FILTER_BUTTON = 'button[aria-controls="run-filter-panel"]';

/** The same keyboard disclosure exposes the filter form at every width. */
export async function openRunFilters(page: Page): Promise<void> {
  const button = page.locator(RUN_FILTER_BUTTON);
  await expect(button).toHaveAttribute('aria-controls', 'run-filter-panel');
  if ((await button.getAttribute('aria-expanded')) === 'false') await button.press('Enter');
  await expect(button).toHaveAttribute('aria-expanded', 'true');
  await expect(page.locator('#run-filter-panel')).toBeVisible();
}

/** The filters are Helm comboboxes now: the trigger shows the selected label, not a value. */
export const FILTER_LABEL = {
  any: { en: 'All', vi: 'Tất cả' },
  running: { en: 'Running', vi: 'Đang chạy' },
  complete: { en: 'Complete', vi: 'Hoàn tất' },
  pending: { en: 'Not evaluated', vi: 'Chưa đánh giá' },
  fail: { en: 'FAIL', vi: 'FAIL' },
  rerun: { en: 'Rerun', vi: 'Chạy lại' },
} satisfies Record<string, Record<ShellLocale, string>>;

/** A combobox commits on an option click; it exposes no form value to read back. */
export async function chooseFilter(page: Page, trigger: string, label: string): Promise<void> {
  await openRunFilters(page);
  await page.locator(trigger).click();
  await page.getByRole('option', { name: label, exact: true }).click();
}

export async function expectCenteredRunIdentity(row: Locator): Promise<void> {
  const geometry = await row.evaluate((element) => {
    const block = element.querySelector('[data-run-row]');
    const cell = block?.closest('td');
    const link = block?.querySelector('a');
    const subject = link?.querySelector('bdi, time');
    const time = block?.querySelector('time');
    const headings = element.closest('table')?.querySelectorAll('thead th');
    if (!block || !cell || !link || !subject || !time || !headings)
      throw new Error('Missing desktop run subject, time or headers');
    const wrapper = block.getBoundingClientRect();
    const bounds = cell.getBoundingClientRect();
    const range = document.createRange();
    range.selectNodeContents(subject);
    // A clipped ref paints inside its preview; its underlying text range can extend past it.
    const text = subject.getBoundingClientRect();
    return {
      centerOffset: (wrapper.top + wrapper.bottom - bounds.top - bounds.bottom) / 2,
      // Chromium reports both the original text and the ellipsis fragment at the same y.
      lines: new Set([...range.getClientRects()].map((rect) => rect.top)).size,
      fits:
        text.left >= wrapper.left - 1 && text.right <= Math.min(wrapper.right, bounds.right) + 1,
      alignment: [...element.children].map((entry, index) => {
        const body = entry.getBoundingClientRect();
        const header = headings[index]?.getBoundingClientRect();
        if (!header) return Number.POSITIVE_INFINITY;
        return Math.max(Math.abs(body.x - header.x), Math.abs(body.width - header.width));
      }),
    };
  });
  expect(Math.abs(geometry.centerOffset)).toBeLessThanOrEqual(2);
  expect(geometry.lines).toBe(1);
  expect(geometry.fits).toBe(true);
  expect(geometry.alignment).toHaveLength(6);
  for (const difference of geometry.alignment) expect(difference).toBeLessThanOrEqual(1);
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
