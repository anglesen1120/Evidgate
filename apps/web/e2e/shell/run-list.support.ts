import { expect, type Locator, type Page } from '@playwright/test';

/** Both approved table presentations carry the same served row identity. */
export const RUN_RECORDS = 'app-run-list-table [data-row-id]';
export const RUN_FILTER_BUTTON = 'button[data-run-filter-disclosure]';

/** The same keyboard disclosure exposes the filter form at every width. */
export async function openRunFilters(page: Page): Promise<void> {
  const button = page.locator(RUN_FILTER_BUTTON);
  await expect(button).toHaveAttribute('aria-controls', 'run-filter-panel');
  if ((await button.getAttribute('aria-expanded')) === 'false') await button.press('Enter');
  await expect(button).toHaveAttribute('aria-expanded', 'true');
  await expect(page.locator('#run-filter-panel')).toBeVisible();
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
