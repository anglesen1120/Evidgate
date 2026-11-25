// The producers section on the real page: one row per declared slot, with the facts the run read
// serves for it. The empty and fifty-slot runs are derived catalog rows, because the corpus holds neither.
import { expect, test, type Locator, type Page } from '@playwright/test';
import readyFixture from '../../../../fixtures/web-api/run-detail/input.json';
import { expectNoA11yViolations } from '../a11y/axe-scan';
import { LOCALES, type ShellLocale } from './support';
import { runDetailPath, type RunStateVariant } from './run-detail.support';

/** The two slots the unmodified `run-detail` document declares, in shard-index order. */
const DECLARED: readonly string[] = readyFixture.body.declared_producers;
/** The fact cells of a row: identity, finalize, last sequence and the count pair. */
const FACTS = ['identity', 'finalize', 'last-sequence', 'count'] as const;
const text = async (cell: Locator): Promise<string> =>
  ((await cell.textContent()) ?? '').replace(/\s+/g, ' ').trim();

/** The producers section of one run, once the run read has landed. */
async function producersOf(
  page: Page,
  run: RunStateVariant,
  locale: ShellLocale = 'en',
): Promise<Locator> {
  await page.goto(runDetailPath({ locale, run, tab: 'evidence' }));
  await expect(page.locator('app-run-detail-page')).toHaveAttribute(
    'data-run-detail-status',
    'ready',
  );
  const producers = page.locator('#producers');
  await expect(producers).toBeVisible();
  return producers;
}

/** The slot id of every rendered row, in document order. */
async function slotIds(producers: Locator): Promise<string[]> {
  const slots = await producers
    .locator('[data-producer-slot]')
    .evaluateAll((rows) => rows.map((row) => row.getAttribute('data-producer-slot')));
  return slots.map((slot) => {
    if (slot === null) throw new Error('a producer row carries no slot id');
    return slot;
  });
}

/** Whether the wrapper the table sits in has to scroll the table sideways. */
async function tableWrapperOverflows(producers: Locator): Promise<boolean> {
  return producers.evaluate((root) => {
    let node: HTMLElement | null = root.querySelector('table')?.parentElement ?? null;
    while (node !== null && node !== root) {
      const { overflowX } = getComputedStyle(node);
      if (overflowX === 'auto' || overflowX === 'scroll') {
        return node.scrollWidth > node.clientWidth;
      }
      node = node.parentElement;
    }
    return false;
  });
}

for (const locale of LOCALES) {
  const range = (span: string, total: number) =>
    locale === 'vi' ? `Hiển thị ${span} trên ${total}` : `Showing ${span} of ${total}`;
  test(`names one row per declared producer with its served facts (${locale})`, async ({
    page,
  }) => {
    const producers = await producersOf(page, 'ready', locale);
    await expect(producers.getByRole('heading', { level: 2 })).toHaveText(/\S/);
    await expect(producers.locator('[data-row-id]')).toHaveCount(DECLARED.length);
    expect(await slotIds(producers), 'the rows left the declared order').toEqual([...DECLARED]);

    for (const slot of DECLARED) {
      const row = producers.locator(`[data-row-id]:has([data-producer-slot="${slot}"])`);
      await expect(row.locator('[data-fact]')).toHaveCount(FACTS.length);
      await expect(row.locator('[data-fact][data-unavailable]')).toHaveCount(0);
      await expect(
        row.locator(`#producer-${slot}`),
        'the row carries no anchor of its own',
      ).toHaveCount(1);
    }
    const served = readyFixture.body.producers[0];
    if (served === undefined) throw new Error('the run-detail fixture serves no producer');
    const first = producers.locator(
      `[data-row-id]:has([data-producer-slot="${served.producer_id}"])`,
    );
    expect(await text(first.locator('[data-fact="last-sequence"]'))).toBe(
      String(served.last_sequence),
    );
    expect(await text(first.locator('[data-fact="count"]'))).toBe(
      `${served.count} / ${served.expected}`,
    );
    await expect(producers.locator('[data-badge]')).toHaveCount(0);
    await expectNoA11yViolations(page, 'run detail, producers section');
  });

  test(`shows a finalized, a count-mismatched and a stale-epoch slot (${locale})`, async ({
    page,
  }) => {
    const producers = await producersOf(page, 'slot-states', locale);
    expect(await slotIds(producers)).toEqual(['shard-1', 'shard-2', 'shard-3']);
    const row = (slot: string) =>
      producers.locator(`[data-row-id]:has([data-producer-slot="${slot}"])`);

    expect(await text(row('shard-1').locator('[data-fact="count"]'))).toBe('1 / 1');
    await expect(row('shard-1').locator('[data-count-mismatch], [data-badge]')).toHaveCount(0);

    expect(await text(row('shard-2').locator('[data-fact="count"]'))).toContain('7 / 9');
    await expect(row('shard-2').locator('[data-count-mismatch]')).toBeVisible();
    await expect(row('shard-2').locator('[data-badge]')).toHaveCount(0);

    expect(await text(row('shard-3').locator('[data-fact="count"]'))).toBe('5 / 5');
    await expect(row('shard-3').locator('[data-badge="stale-epoch"]')).toBeVisible();
    await expect(row('shard-3').locator('[data-count-mismatch]')).toHaveCount(0);

    await expect(producers.locator('[data-fact][data-unavailable]')).toHaveCount(0);
    // The badge and the mismatch are informational: no completeness or decision claim appears.
    await expect(producers.locator('[data-completeness]')).toHaveCount(0);
    await expectNoA11yViolations(page, 'run detail, producer slot states');
  });

  test(`explains a run with no declared producer instead of reporting a failure (${locale})`, async ({
    page,
  }) => {
    const producers = await producersOf(page, 'no-producers', locale);
    await expect(producers.locator('[data-state="empty"]')).toBeVisible();
    await expect(producers.locator('[data-row-id]')).toHaveCount(0);
    await expect(producers.locator('table')).toHaveCount(0);
    // An empty declaration is a fact about the run, not a failed read: no error state, no retry.
    await expect(producers.locator('[data-state="error"]')).toHaveCount(0);
    await expect(producers.getByRole('alert')).toHaveCount(0);
    await expect(producers.locator('app-data-table-pager button')).toHaveCount(0);
    await expect(producers.locator('app-data-table-pager .page-range')).toHaveText(range('0–0', 0));
    await expectNoA11yViolations(page, 'run detail, producers empty state');
  });

  test(`a fifty-slot run retains all six labelled facts without sideways scrolling at 320px (${locale})`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 320, height: 800 });
    const producers = await producersOf(page, 'many-producers', locale);
    await producers.locator('app-data-table-pager hlm-select-trigger button').click();
    await page.getByRole('option', { name: '50', exact: true }).click();
    await expect(producers.locator('li[data-row-id]')).toHaveCount(50);

    const slots = await slotIds(producers);
    expect(slots[0], 'the first slot is not the first shard').toBe('shard-1');
    expect(slots[49], 'the last slot is not the last shard').toBe('shard-50');
    // With both endpoints right, ascending suffix numbers pin the whole declared order.
    expect(
      slots.map((slot) => Number(slot.replace('shard-', ''))),
      'the slots are not in shard-index order',
    ).toEqual(Array.from({ length: 50 }, (_slot, index) => index + 1));

    await expect(producers.locator('li[data-row-id] dt')).toHaveCount(300);
    await expect(producers.locator('li[data-row-id] dd')).toHaveCount(300);
    expect(await tableWrapperOverflows(producers), 'stacked facts need no sideways scroller').toBe(
      false,
    );
    expect(
      await producers.evaluate((section) => section.scrollWidth > section.clientWidth),
      'the producers section overflows its own column at 320px',
    ).toBe(false);
    expect(
      await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth),
      'the page scrolls sideways at 320px',
    ).toBe(true);
    await expectNoA11yViolations(page, 'run detail, producers table at 320px');
  });

  test(`page-flow copy controls remain reachable and a producer anchor focuses its row (${locale})`, async ({
    page,
  }) => {
    const producers = await producersOf(page, 'many-producers', locale);
    await page.locator('[data-run-tab="context"]').click();
    // The manifest is a collapsed disclosure; its copy controls are reachable only once open.
    const manifest = page.locator('app-manifest-panel details').first();
    await manifest.locator('summary').click();
    await expect(manifest).toHaveJSProperty('open', true);
    const manifestCopies = page.locator('app-manifest-panel app-copy-value-button button');
    await expect(manifestCopies).toHaveCount(3);
    await manifestCopies.first().focus();
    await expect(manifestCopies.first()).toBeFocused();
    for (const copy of (await manifestCopies.all()).slice(1)) {
      await page.keyboard.press('Tab');
      await expect(copy).toBeFocused();
      await expect(copy).toHaveAccessibleName(/^(Copy|Sao chép) /);
    }
    await page.locator('[data-run-tab="evidence"]').click();
    await producers.locator('app-data-table-pager hlm-select-trigger button').click();
    await page.getByRole('option', { name: '50', exact: true }).click();
    const scroller = producers.locator('.scroll');
    await expect(scroller).toHaveCount(1);
    await expect(scroller).not.toHaveAttribute('tabindex', '0');
    expect(
      await scroller.evaluate((element) => element.scrollHeight <= element.clientHeight + 1),
    ).toBe(true);
    // All producer controls remain in page flow after the evidence panel.
    const producerCopies = producers.locator('app-copy-value-button button');
    await expect(producerCopies).toHaveCount(50);
    await producerCopies.first().focus();
    for (const [index, copy] of (await producerCopies.all()).entries()) {
      if (index > 0) await page.keyboard.press('Tab');
      await expect(copy).toBeFocused();
      await expect(copy).toHaveAccessibleName(/^(Copy|Sao chép) /);
    }
    await page.keyboard.press('Tab');
    const pageSize = producers.locator('app-data-table-pager hlm-select-trigger button');
    await expect(pageSize).toBeFocused();
    const producerPager = producers.locator('app-data-table-pager');
    const producerPrevious = producerPager.locator('button[data-page="previous"]');
    const producerNext = producerPager.locator('button[data-page="next"]');
    // Unavailable shared-pager actions remain keyboard reachable via aria-disabled.
    for (const action of [producerPrevious, producerNext]) {
      await expect(action).toHaveAttribute('aria-disabled', 'true');
      await page.keyboard.press('Tab');
      await expect(action).toBeFocused();
    }
    await page.keyboard.press('Tab');
    expect(await page.evaluate(() => document.activeElement?.tagName)).toBe('BODY');
    for (const control of [producerNext, producerPrevious, pageSize]) {
      await page.keyboard.press('Shift+Tab');
      await expect(control).toBeFocused();
    }
    await page.keyboard.press('Shift+Tab');
    for (const copy of (await producerCopies.all()).reverse()) {
      await expect(copy).toBeFocused();
      await page.keyboard.press('Shift+Tab');
    }
    await expect(scroller).not.toBeFocused();
    await expect(page.locator('[data-evidence-revision-select]')).toBeFocused();
    await page.locator('[data-run-tab="attempts"]').click();
    const attempts = page.locator('app-attempts-table');
    await expect(attempts.locator('app-data-table-pager button')).toHaveCount(0);
    const lastCopy = attempts.locator('app-copy-value-button button').last();
    await lastCopy.focus();
    await expect(lastCopy).toBeFocused();

    // `#producer-<slot>` is the documented anchor: a deep link lands on, and focuses, that row.
    await page.goto(`${runDetailPath({ locale, run: 'many-producers' })}#producer-shard-42`);
    await expect(page.locator('app-run-detail-page')).toHaveAttribute(
      'data-run-detail-status',
      'ready',
    );
    const anchor = page.locator('#producer-shard-42');
    await expect(anchor).toBeVisible();
    await expect(anchor).toBeInViewport();
    await expect(anchor).toBeFocused();
    await expect(anchor).toContainText('shard-42');
  });

  test(`producer paging shows every slot exactly once and resets on size changes (${locale})`, async ({
    page,
  }) => {
    const producers = await producersOf(page, 'many-producers', locale);
    const pager = producers.locator('app-data-table-pager');
    const summary = pager.locator('.page-range');
    await expect(producers.locator('[data-row-id]')).toHaveCount(20);
    await expect(summary).toHaveText(range('1–20', 50));
    await expect(pager.locator('button[data-page="previous"]')).toBeDisabled();
    const seen = await slotIds(producers);
    await pager.locator('button[data-page="next"]').click();
    await expect(summary).toHaveText(range('21–40', 50));
    seen.push(...(await slotIds(producers)));
    await pager.locator('button[data-page="next"]').click();
    await expect(summary).toHaveText(range('41–50', 50));
    await expect(producers.locator('[data-row-id]')).toHaveCount(10);
    seen.push(...(await slotIds(producers)));
    expect(seen).toEqual(Array.from({ length: 50 }, (_, index) => `shard-${index + 1}`));
    await expect(pager.locator('button[data-page="next"]')).toBeDisabled();
    await pager.locator('hlm-select-trigger button').click();
    await page.getByRole('option', { name: '50', exact: true }).click();
    await expect(summary).toHaveText(range('1–50', 50));
    await expect(producers.locator('[data-row-id]')).toHaveCount(50);
    await expect(pager.locator('button[data-page="previous"]')).toBeDisabled();
    await expect(pager.locator('button[data-page="next"]')).toBeDisabled();
  });
}
