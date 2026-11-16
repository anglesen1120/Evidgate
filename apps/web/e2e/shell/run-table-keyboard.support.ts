import { expect, type Locator, type Page } from '@playwright/test';

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
  expectedLinks = 0,
): Promise<void> {
  const copies = root.locator('app-copy-value-button button');
  await expect(copies).toHaveCount(expectedCopies);
  await expect(root.locator('a[href]')).toHaveCount(expectedLinks);
  const disclosures = root.locator('app-data-table-value .value[role="button"][tabindex="0"]');
  const badges = root.locator('[hlmBadge].spartan-badge--clipped[tabindex="0"]');
  const controls = root.locator('a[href], button, [tabindex="0"]:not([role="region"])');
  await expect(controls).toHaveCount(
    expectedCopies + expectedLinks + (await disclosures.count()) + (await badges.count()),
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
  const copies = records.locator('app-copy-value-button button');
  await expect(copies).toHaveCount(count);
  const names = await copies.evaluateAll((buttons) =>
    buttons.map((button) => button.getAttribute('aria-label')),
  );
  expect(names.every((name) => name !== null && /^(Copy|Sao chép) \S/.test(name))).toBe(true);
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
    expect(total).toBe(count * 2);
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
