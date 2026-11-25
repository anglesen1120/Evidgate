import { expect, type Locator, type Page } from '@playwright/test';

import { RUN_FILTER_BUTTON } from './run-list.support';

const SELECT_STOPS = [
  '#run-state',
  'button[aria-label="Hint: Decision"]',
  '#run-decision',
  'button[aria-controls="run-filter-hint"]',
  '#run-scope',
] as const;

/**
 * The focus ring every stop must show: the token pair the page styles its own controls with
 * (`--eg-focus-width` / `--eg-focus` on the light theme, which is this suite's default).
 */
export async function expectFocusRing(target: Locator, offset: string): Promise<void> {
  await expect(target).toHaveCSS('outline-width', '2px');
  await expect(target).toHaveCSS('outline-style', 'solid');
  await expect(target).toHaveCSS('outline-color', 'rgb(0, 107, 102)');
  await expect(target).toHaveCSS('outline-offset', offset);
}

/** The inline quick decisions and ref search precede secondary filters. */
export async function enterFilterDisclosure(page: Page): Promise<void> {
  for (const selector of [
    '[data-run-quick="all"]',
    '[data-run-quick="fail"]',
    '[data-run-quick="pending"]',
    '#run-ref',
    '[data-filter-search]',
  ]) {
    await page.keyboard.press('Tab');
    await expect(page.locator(selector)).toBeFocused();
    await expectFocusRing(page.locator(selector), '2px');
  }
  await page.keyboard.press('Tab');
  const button = page.locator(RUN_FILTER_BUTTON);
  await expect(button).toBeFocused();
  await expectFocusRing(button, '2px');
  if ((await button.getAttribute('aria-expanded')) === 'false') await page.keyboard.press('Enter');
  await expect(button).toHaveAttribute('aria-expanded', 'true');
  for (const [index, selector] of [...SELECT_STOPS, '[data-filter-apply]'].entries()) {
    if (index > 0) await page.keyboard.press('Tab');
    await expect(page.locator(selector)).toBeFocused();
    await expectFocusRing(page.locator(selector), '2px');
  }
  await page.keyboard.press('Escape');
  await expect(button).toHaveAttribute('aria-expanded', 'false');
  await expect(button).toBeFocused();
}

/** Open a collapsed native disclosure in sequence, without moving focus programmatically. */
export async function enterRunDetails(page: Page, details: Locator): Promise<void> {
  const summary = details.locator(':scope > summary');
  await page.keyboard.press('Tab');
  await expect(summary).toBeFocused();
  await expect(summary).toHaveAccessibleName(/\S/);
  await expect(details).not.toHaveAttribute('open');
  await page.keyboard.press('Enter');
  await expect(details).toHaveAttribute('open', '');
  await expect(summary).toBeFocused();
}

/** Only an actually overflowing shared table adds a named scroll-region stop. */
export async function tabThroughTableScroller(page: Page, table: Locator): Promise<void> {
  const scroller = table.locator('.scroll');
  await expect(scroller).toHaveCount(1);
  const scrolls = await scroller.evaluate(
    (element) =>
      !element.classList.contains('eg-table-narrow') &&
      (element.scrollWidth > element.clientWidth || element.scrollHeight > element.clientHeight),
  );
  if (scrolls) {
    await expect(scroller).toHaveAttribute('tabindex', '0');
    await expect(scroller).toHaveAttribute('role', 'region');
    await expect(scroller).toHaveAccessibleName(/\S/);
    await page.keyboard.press('Tab');
    await expect(scroller).toBeFocused();
  } else {
    await expect(scroller).not.toHaveAttribute('tabindex', '0');
  }
}

/**
 * Keep an exact navigation/copy budget. A measured clipped value may additionally expose
 * its one full-value trigger; anonymous controls and extra actions are not permitted.
 */
export async function tabThroughRunControls(
  page: Page,
  root: Locator,
  expectedCopies: number,
  isListRow = false,
): Promise<void> {
  const copies = root.locator('app-copy-value-button button');
  await expect(copies).toHaveCount(expectedCopies);
  const rowActionCount = isListRow ? 1 : 0;
  await expect(root.locator('a[href]')).toHaveCount(rowActionCount);
  if (isListRow) await expect(root.locator('button[aria-haspopup="dialog"]')).toHaveCount(1);
  const disclosures = root.locator('app-data-table-value .value[role="button"][tabindex="0"]');
  const badges = root.locator('[hlmBadge].spartan-badge--clipped[tabindex="0"]');
  const controls = root.locator('a[href], button, [tabindex="0"]:not([role="region"])');
  await expect(controls).toHaveCount(
    expectedCopies + 2 * rowActionCount + (await disclosures.count()) + (await badges.count()),
  );
  for (const control of await controls.all()) {
    await page.keyboard.press('Tab');
    await expect(control)
      .toBeFocused()
      .catch(async (error: unknown) => {
        const actual = await page.evaluate(() => document.activeElement?.outerHTML.slice(0, 500));
        throw new Error(`${String(error)}\nActual active element: ${actual}`);
      });
    const badgeDisclosure = await control.evaluate((element) =>
      element.matches('[hlmBadge].spartan-badge--clipped'),
    );
    if (badgeDisclosure) {
      await expect(control).toContainText(/\S/);
      const fullValue = control.locator('.spartan-badge__full-value');
      await expect(fullValue).toBeVisible();
      await expect(fullValue).toHaveText(/\S/);
      await page.keyboard.press('Escape');
      await expect(fullValue).not.toBeVisible();
      await expect(control).toBeFocused();
    } else {
      await expect(control).toHaveAccessibleName(/\S/);
    }
    if ((await control.getAttribute('role')) === 'button') {
      await expect(control).toHaveAttribute('aria-expanded', 'true');
      await page.keyboard.press('Escape');
      await expect(control).toHaveAttribute('aria-expanded', 'false');
      await expect(control).toBeFocused();
    }
  }
  for (const copy of await copies.all()) {
    await expect(copy).toHaveAccessibleName(/^(Copy|Sao chép) \S/);
  }
}

/** Record every real focus transition without 400 per-control browser round trips. */
export async function tabThroughNarrowRunRecords(
  page: Page,
  records: Locator,
  count: number,
): Promise<void> {
  await expect(records.locator('li[data-row-id]')).toHaveCount(count);
  await expect(records.locator('a[href]')).toHaveCount(count);
  await expect(records.locator('button[aria-haspopup="dialog"]')).toHaveCount(count);
  await expect(records.locator('app-copy-value-button button')).toHaveCount(count);
  await expect(records.locator('[role="button"]')).toHaveCount(0);
  const walk = await records.evaluateHandle((root) => {
    const expected = [...root.querySelectorAll('a[href], button, [tabindex="0"]')];
    const actual: Element[] = [];
    const record = (event: FocusEvent) => {
      if (event.target instanceof Element) actual.push(event.target);
    };
    document.addEventListener('focusin', record);
    return { expected, actual, record };
  });
  try {
    const total = await walk.evaluate((state) => state.expected.length);
    expect(total).toBe(count * 3);
    for (let step = 0; step < total; step++) await page.keyboard.press('Tab');
    const result = await walk.evaluate((state) => ({
      count: state.actual.length,
      mismatches: state.expected.flatMap((expected, index) =>
        state.actual[index] === expected
          ? []
          : [
              {
                index,
                expected:
                  expected.getAttribute('href') ??
                  expected.getAttribute('aria-label') ??
                  expected.tagName,
                actual:
                  state.actual[index]?.getAttribute('href') ??
                  state.actual[index]?.getAttribute('aria-label') ??
                  state.actual[index]?.tagName,
              },
            ],
      ),
    }));
    expect(result.count).toBe(total);
    expect(result.mismatches).toEqual([]);
  } finally {
    await walk.evaluate((state) => document.removeEventListener('focusin', state.record));
    await walk.dispose();
  }
}

/** Disclosure-only delta: existing action stops are present in both before/after totals. */
export async function auditRunTabStops(root: Locator, walk: string): Promise<void> {
  const counts = await root.evaluate((element) => {
    const selector =
      'a[href], button, input:not([type="hidden"]), select, textarea, summary, [tabindex="0"]';
    const candidates = [...element.querySelectorAll<HTMLElement>(selector)];
    if (element.matches(selector)) candidates.unshift(element as HTMLElement);
    const stops = candidates.filter(
      (candidate) =>
        candidate.getClientRects().length > 0 &&
        !candidate.matches(':disabled, [tabindex="-1"]') &&
        !candidate.closest('[hidden], [inert]'),
    );
    const badgeStops = stops.filter((stop) =>
      stop.matches('[hlmBadge].spartan-badge--clipped[tabindex="0"]'),
    ).length;
    return {
      beforeBadgeDisclosures: stops.length - badgeStops,
      afterBadgeDisclosures: stops.length,
      badgeDisclosures: badgeStops,
      valueDisclosures: stops.filter((stop) =>
        stop.matches('app-data-table-value .value[role="button"]'),
      ).length,
      copyActions: stops.filter((stop) => stop.closest('app-copy-value-button')).length,
    };
  });
  console.info(`RUNS_KEYBOARD_STOPS ${JSON.stringify({ walk, ...counts })}`);
}
