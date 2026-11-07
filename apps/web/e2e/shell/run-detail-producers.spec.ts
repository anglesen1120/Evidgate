// The producers section on the real page: `GET /v1/runs/{r}` serves only `declared_producers` and
// `shard_count`, so a row names one slot id and every other fact is the shared unavailable state.
// The empty and fifty-slot runs are derived catalog rows, because the corpus holds neither.
import { expect, test, type Locator, type Page } from '@playwright/test';
import readyFixture from '../../../../fixtures/web-api/run-detail/input.json';
import { activeElement, expectNoA11yViolations, LOCALES, type ShellLocale } from './support';
import { runDetailPath, type RunStateVariant } from './run-detail.support';

/** The two slots the unmodified `run-detail` document declares, in shard-index order. */
const DECLARED: readonly string[] = readyFixture.body.declared_producers;
/** The unserved fact cells of a row: identity, finalize, last sequence and the count pair. */
const FACTS = ['identity', 'finalize', 'last-sequence', 'count'] as const;

/** The producers section of one run, once the run read has landed. */
async function producersOf(
  page: Page,
  run: RunStateVariant,
  locale: ShellLocale = 'en',
): Promise<Locator> {
  await page.goto(runDetailPath({ locale, run }));
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
    .locator('tr[data-producer-slot]')
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
  test(`names one row per declared producer and guesses no other fact (${locale})`, async ({
    page,
  }) => {
    const producers = await producersOf(page, 'ready', locale);
    await expect(producers.getByRole('heading', { level: 2 })).toHaveText(/\S/);
    await expect(producers.locator('tr[data-producer-slot]')).toHaveCount(DECLARED.length);
    expect(await slotIds(producers), 'the rows left the declared order').toEqual([...DECLARED]);

    for (const slot of DECLARED) {
      const row = producers.locator(`tr[data-producer-slot="${slot}"]`);
      await expect(row.locator('td[data-fact]')).toHaveCount(FACTS.length);
      // The read serves no identity, finalize, sequence or count fact, so every cell says so.
      await expect(row.locator('td[data-fact][data-unavailable]')).toHaveCount(FACTS.length);
      await expect(
        page.locator(`tr[data-producer-slot="${slot}"] #producer-${slot}`),
        'the row carries no anchor of its own',
      ).toHaveCount(1);
    }
    // No stale-epoch or after-cancel badge: neither count is served, so neither may be claimed.
    await expect(producers.locator('[data-badge]')).toHaveCount(0);
    await expectNoA11yViolations(page, 'run detail, producers section');
  });

  test(`explains a run with no declared producer instead of reporting a failure (${locale})`, async ({
    page,
  }) => {
    const producers = await producersOf(page, 'no-producers', locale);
    await expect(producers.locator('[data-state="empty"]')).toBeVisible();
    await expect(producers.locator('tr[data-producer-slot]')).toHaveCount(0);
    await expect(producers.locator('table')).toHaveCount(0);
    // An empty declaration is a fact about the run, not a failed read: no error state, no retry.
    await expect(producers.locator('[data-state="error"]')).toHaveCount(0);
    await expect(producers.getByRole('alert')).toHaveCount(0);
    await expect(producers.locator('button')).toHaveCount(0);
    await expectNoA11yViolations(page, 'run detail, producers empty state');
  });

  test(`a fifty-slot run scrolls inside its wrapper, not the page, at 320px (${locale})`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 320, height: 800 });
    const producers = await producersOf(page, 'many-producers', locale);
    await expect(producers.locator('tr[data-producer-slot]')).toHaveCount(50);

    const slots = await slotIds(producers);
    expect(slots[0], 'the first slot is not the first shard').toBe('shard-1');
    expect(slots[49], 'the last slot is not the last shard').toBe('shard-50');
    // With both endpoints right, ascending suffix numbers pin the whole declared order.
    expect(
      slots.map((slot) => Number(slot.replace('shard-', ''))),
      'the slots are not in shard-index order',
    ).toEqual(Array.from({ length: 50 }, (_slot, index) => index + 1));

    expect(await tableWrapperOverflows(producers), 'the table wrapper does not scroll').toBe(true);
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

  test(`the table is one keyboard stop and a producer anchor lands on its row (${locale})`, async ({
    page,
  }) => {
    const producers = await producersOf(page, 'many-producers', locale);
    // The manifest panel's own copy control, named structurally so the case reads no copy.
    const copyHash = page.locator('app-manifest-panel').getByRole('button');
    await expect(copyHash).toHaveCount(1);
    await copyHash.focus();
    await expect(copyHash).toBeFocused();

    // The table's own scroll wrapper is the section's only stop, so the walk reaches it from the
    // panel above and leaves the section in one more press.
    await page.keyboard.press('Tab');
    const scroller = producers.locator('[tabindex="0"]');
    await expect(scroller).toHaveCount(1);
    await expect(scroller).toBeFocused();
    await page.keyboard.press('Tab');
    expect((await activeElement(page)).tag).toBe('body');
    await page.keyboard.press('Shift+Tab');
    await expect(scroller).toBeFocused();
    await page.keyboard.press('Shift+Tab');
    await expect(copyHash).toBeFocused();

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
}
