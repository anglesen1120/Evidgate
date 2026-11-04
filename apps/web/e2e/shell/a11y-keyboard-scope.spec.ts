// Keyboard traversal follows visible controls: compact desktop scope lives in the topbar;
// narrow layouts expose those same choices in the modal navigation drawer.
import { expect, test } from '@playwright/test';
import type { Locator, Page } from '@playwright/test';
import { expectScopeValue, exploreScope, scopeControl } from './scope-controls';

import {
  activeElement,
  LIMITED_GRANTS,
  localePath,
  ORG_ACME,
  ORG_LUMEN,
  overviewPath,
  PROJECT_CHECKOUT,
  PROJECT_STOREFRONT,
  withVariant,
} from './support';

/** The project screen the trimmed corpus session reaches after committing `PROJECT_CHECKOUT`. */
const CHECKOUT_OVERVIEW = `/en/orgs/${ORG_ACME}/projects/${PROJECT_CHECKOUT}/overview`;

/**
 * Two animation frames: long enough for the router to have navigated, if it were going to. The
 * app compiles against the ES2022 lib, so this executor form is the project's resolver idiom.
 */
async function settle(page: Page): Promise<void> {
  await page.evaluate(
    () =>
      new Promise<void>((resolve) => {
        requestAnimationFrame(() => requestAnimationFrame(() => resolve()));
      }),
  );
}

/** Desktop stops in document and visual order. */
type TopbarStop = 'navigation' | 'theme' | 'organization' | 'project';

/** Walk from the document start without synthetic focus moves. */
async function walkTopbar(
  page: Page,
  stop: TopbarStop,
  layout: 'desktop' | 'compact',
): Promise<void> {
  await page.keyboard.press('Tab');
  await expectFocusVisible(page, page.locator('a.skip-link'));

  if (layout === 'desktop') {
    await page.keyboard.press('Tab');
    await expectFocusVisible(page, page.locator('a.brand'));

    const navLinks = page.locator('app-shell-nav nav a');
    for (let index = 0; index < (await navLinks.count()); index += 1) {
      await page.keyboard.press('Tab');
      await expectFocusVisible(page, navLinks.nth(index));
    }
  }

  const stops: readonly (readonly [TopbarStop, Locator])[] = [
    ['navigation', page.locator('button.nav-toggle')],
    ['organization', page.getByRole('combobox', { name: 'Organization' })],
    ['project', page.getByRole('combobox', { name: 'Project' })],
    ['theme', page.locator('button.theme-toggle')],
  ];
  const last = stops.findIndex(([name]) => name === stop);
  if (last < 0) throw new Error(`${stop} is not a topbar stop`);
  for (const [, control] of stops.slice(0, last + 1)) {
    await page.keyboard.press('Tab');
    await expectFocusVisible(page, control);
  }
}

/**
 * The focused element is the expected one AND it shows a focus ring (2.4.7): the browser reports
 * `:focus-visible` for a keyboard move, and the shell paints an outline for it. Asserting the
 * computed outline keeps "focus visible" from becoming a claim about the stylesheet's intent.
 */
async function expectFocusVisible(page: Page, locator: Locator): Promise<void> {
  await expect(locator).toBeFocused();
  const ring = await locator.evaluate((element) => {
    const style = getComputedStyle(element);
    return {
      focusVisible: element.matches(':focus-visible'),
      outlineStyle: style.outlineStyle,
      outlineWidth: Number.parseFloat(style.outlineWidth),
    };
  });
  expect(ring.focusVisible, 'the element is in the keyboard focus state').toBe(true);
  expect(ring.outlineStyle, 'a focus ring is painted, not suppressed').not.toBe('none');
  expect(ring.outlineWidth).toBeGreaterThan(0);
}

/** The element's border box, or a failure that names the element instead of `null`. */
async function boxOf(
  locator: Locator,
): Promise<{ x: number; y: number; width: number; height: number }> {
  const box = await locator.boundingBox();
  expect(box, 'the control has a box to measure').not.toBeNull();
  return box!;
}

/** True while focus is somewhere inside the topbar — used to prove the header's walk ended. */
async function focusInsideTopbar(page: Page): Promise<boolean> {
  return page.evaluate(() => document.activeElement?.closest('header.topbar') !== null);
}

/**
 * Two boxes sit on one visual row when their vertical extents overlap. Used instead of comparing y
 * directly: the toggle buttons are 44–48px tall, a scope select adds its label above it, and both
 * are centred on the row, so equal `y` is not what "same row" means here.
 */
function expectSameRow(
  a: { y: number; height: number },
  b: { y: number; height: number },
  message: string,
): void {
  const overlap = Math.min(a.y + a.height, b.y + b.height) - Math.max(a.y, b.y);
  expect(overlap, message).toBeGreaterThan(0);
}

test.describe('scope flow: keyboard order (AC2, F-SWITCH-01)', () => {
  test('desktop: Tab walks the topbar left to right, all on one row', async ({ page }) => {
    await page.goto(withVariant(overviewPath('en'), LIMITED_GRANTS));
    await expect(page.locator('#main-content h1')).toHaveText('Storefront web');
    const startPath = new URL(page.url()).pathname;

    // 1–2. A fresh document starts at the top of the tab order: the skip link, then the brand.
    await page.keyboard.press('Tab');
    const skipLink = page.locator('a.skip-link');
    await expectFocusVisible(page, skipLink);
    await expect(skipLink).toHaveAccessibleName('Skip to main content');

    await page.keyboard.press('Tab');
    const brand = page.locator('a.brand');
    await expectFocusVisible(page, brand);
    await expect(brand).toHaveAccessibleName('Evidgate workspace');
    // The desktop sidebar is not inert, so its rows really are the next stops.
    await expect(page.locator('#shell-navigation')).not.toHaveAttribute('inert', '');

    // 3. Every sidebar row, in document order: the walk neither skips one nor repeats one.
    const navLinks = page.locator('app-shell-nav nav a');
    const linkCount = await navLinks.count();
    expect(linkCount).toBe(5);
    for (let index = 0; index < linkCount; index += 1) {
      await page.keyboard.press('Tab');
      await expectFocusVisible(page, navLinks.nth(index));
    }
    await expect(page.getByRole('navigation')).toHaveAccessibleName('Primary navigation');

    // The scope controls precede the trailing theme action in both paint and Tab order.
    const navToggle = page.locator('button.nav-toggle');
    await page.keyboard.press('Tab');
    await expectFocusVisible(page, navToggle);
    await expect(navToggle).toHaveAccessibleName('Collapse navigation');

    const orgSelect = page.getByRole('combobox', { name: 'Organization' });
    await page.keyboard.press('Tab');
    await expectFocusVisible(page, orgSelect);
    await expectScopeValue(orgSelect, ORG_ACME);
    await page.keyboard.press('Enter');
    await expect(page.getByRole('option')).toHaveCount(2);
    await page.keyboard.press('Escape');

    const projectSelect = page.getByRole('combobox', { name: 'Project' });
    await page.keyboard.press('Tab');
    await expectFocusVisible(page, projectSelect);
    await expectScopeValue(projectSelect, PROJECT_STOREFRONT);
    const themeToggle = page.locator('button.theme-toggle');
    await page.keyboard.press('Tab');
    await expectFocusVisible(page, themeToggle);
    await expect(themeToggle).toHaveAttribute('aria-pressed', /^(true|false)$/);

    // Traversing the switcher is not a commitment; only activating an option navigates.
    expect(new URL(page.url()).pathname).toBe(startPath);

    // The keyboard order IS the visual order here: four stops, one row, left to right.
    const [navBox, themeBox, orgBox, projectBox] = await Promise.all([
      boxOf(navToggle),
      boxOf(themeToggle),
      boxOf(orgSelect),
      boxOf(projectSelect),
    ]);
    expect(themeBox.x, 'theme follows the project selector').toBeGreaterThan(projectBox.x);
    expect(orgBox.x, 'scope follows navigation').toBeGreaterThan(navBox.x);
    expect(
      projectBox.x,
      'the project select is painted right of the organization select',
    ).toBeGreaterThan(orgBox.x);
    for (const [label, box] of [
      ['the theme toggle', themeBox],
      ['the organization select', orgBox],
      ['the project select', projectBox],
    ] as const) {
      expectSameRow(navBox, box, `${label} shares the toggle's row`);
    }
    // The theme action is the last stop in the header.
    await page.keyboard.press('Tab');
    await expect(themeToggle).not.toBeFocused();
    expect(await focusInsideTopbar(page)).toBe(false);
  });

  test('the keyboard walk commits a project, and the shell hands focus to the page', async ({
    page,
  }) => {
    await page.goto(withVariant(overviewPath('en'), LIMITED_GRANTS));
    await expect(page.locator('#main-content h1')).toHaveText('Storefront web');
    const projectSelect = page.getByRole('combobox', { name: 'Project' });
    await expect(projectSelect).toBeEnabled();

    // Reached the way a keyboard visitor reaches it — Tab from the skip link, every stop's identity
    // asserted, no `locator.focus()` — then the step is held until Enter commits it.
    await walkTopbar(page, 'project', 'desktop');
    await exploreScope(page, projectSelect, PROJECT_CHECKOUT);
    await settle(page);
    expect(new URL(page.url()).pathname).not.toBe(CHECKOUT_OVERVIEW);

    await page.keyboard.press('Enter');
    await expect.poll(() => new URL(page.url()).pathname).toBe(CHECKOUT_OVERVIEW);
    await expect(page.locator('#main-content h1')).toHaveText('Checkout API');
    // A committed switch is a path change, so the shell moves focus to the new page's region.
    expect(await focusInsideTopbar(page)).toBe(false);
    await expect.poll(async () => (await activeElement(page)).id).toBe('main-content');
  });

  test('an organization switch that the session may not open lands on the refusal, focus on main', async ({
    page,
  }) => {
    await page.goto(withVariant(overviewPath('en'), LIMITED_GRANTS));
    await expect(page.locator('#main-content h1')).toHaveText('Storefront web');

    // The keyboard equivalent of the mouse path `scope-theme.spec.ts` documents: the same attempt,
    // the same destination, reached without a pointer — Tab from the skip link to the organization
    // select, the step held, then Enter.
    const orgSelect = page.getByRole('combobox', { name: 'Organization' });
    await walkTopbar(page, 'organization', 'desktop');
    await exploreScope(page, orgSelect, ORG_LUMEN);
    await page.keyboard.press('Enter');

    await expect.poll(() => new URL(page.url()).pathname).toBe('/en/forbidden');
    await expect(page.locator('#main-content h1')).toHaveText('Not authorized');
    // The scope did move: the organization is LUMEN and no project of it is offered.
    await expectScopeValue(orgSelect, ORG_LUMEN);
    await expect(page.getByRole('combobox', { name: 'Project' })).toBeDisabled();
    await expect.poll(async () => (await activeElement(page)).id).toBe('main-content');
  });

  test('vi bundle: the same order and the same named stops', async ({ page }) => {
    // The product default locale renders the same shell through its own bundle. Element identity is
    // asserted the way every shell spec asserts it (route, role, class), with the accessible names
    // read as "present and non-empty" because they are translated.
    await page.goto(
      withVariant(
        localePath('vi', `/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}/overview`),
        LIMITED_GRANTS,
      ),
    );
    await expect(page.locator('#main-content h1')).toHaveText('Storefront web');
    const startPath = new URL(page.url()).pathname;

    await page.keyboard.press('Tab');
    await expect(page.locator('a.skip-link')).toBeFocused();

    await page.keyboard.press('Tab');
    await expect(page.locator('a.brand')).toBeFocused();

    const navLinks = page.locator('app-shell-nav nav a');
    for (let index = 0; index < (await navLinks.count()); index += 1) {
      await page.keyboard.press('Tab');
      await expect(navLinks.nth(index)).toBeFocused();
    }

    const stops = [
      page.locator('button.nav-toggle'),
      scopeControl(page, 0),
      scopeControl(page, 1),
      page.locator('button.theme-toggle'),
    ];
    for (const stop of stops) {
      await page.keyboard.press('Tab');
      await expect(stop).toBeFocused();
      await expect(stop).toHaveAccessibleName(/\S+/);
    }

    // Tabbing through the localized switcher does not navigate either.
    expect(new URL(page.url()).pathname).toBe(startPath);
  });
});

for (const width of [360, 390]) {
  test.describe(`scope flow at ${width}px`, () => {
    test.use({ viewport: { width, height: 900 } });

    test('keeps one compact topbar row and leaves scope in the closed drawer', async ({ page }) => {
      await page.goto(withVariant(overviewPath('en'), LIMITED_GRANTS));
      await expect(page.locator('#main-content h1')).toHaveText('Storefront web');
      await expect(page.locator('#shell-navigation')).toHaveAttribute('inert', '');
      await expect(page.locator('header.topbar [role="combobox"]')).toHaveCount(0);
      await expect(page.locator('#shell-navigation [role="combobox"]')).toHaveCount(2);
      await walkTopbar(page, 'navigation', 'compact');
      const navToggle = page.locator('button.nav-toggle');
      const themeToggle = page.locator('button.theme-toggle');
      await page.keyboard.press('Tab');
      await expectFocusVisible(page, themeToggle);
      const [navBox, themeBox, topbarBox] = await Promise.all([
        boxOf(navToggle),
        boxOf(themeToggle),
        boxOf(page.locator('header.topbar')),
      ]);
      expectSameRow(navBox, themeBox, 'both controls share a single row');
      expect(themeBox.x).toBeGreaterThan(navBox.x);
      expect(topbarBox.height).toBeGreaterThanOrEqual(64);
      expect(topbarBox.height).toBeLessThan(80);
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(
        true,
      );
      await page.keyboard.press('Tab');
      expect(await focusInsideTopbar(page)).toBe(false);
    });

    test('opens scope in drawer order, traps focus and closes with Escape', async ({ page }) => {
      await page.goto(withVariant(overviewPath('en'), LIMITED_GRANTS));
      await expect(page.locator('#main-content h1')).toHaveText('Storefront web');
      await walkTopbar(page, 'navigation', 'compact');
      const navToggle = page.locator('button.nav-toggle');
      const aside = page.locator('#shell-navigation');
      const close = aside.locator('button.close-button');
      await page.keyboard.press('Enter');
      await expect(aside).toHaveAttribute('role', 'dialog');
      await expect(page.locator('.main-stack')).toHaveAttribute('inert', '');
      await expectFocusVisible(page, close);
      await page.keyboard.press('Tab');
      await expectFocusVisible(page, aside.locator('a.brand'));
      await page.keyboard.press('Tab');
      await expectFocusVisible(page, aside.getByRole('combobox', { name: 'Organization' }));
      await page.keyboard.press('Enter');
      await expect(page.getByRole('listbox', { name: 'Organization' })).toBeVisible();
      await page.keyboard.press('Escape');
      await expect(aside.getByRole('combobox', { name: 'Organization' })).toHaveAttribute(
        'aria-expanded',
        'false',
      );
      await expect(aside).toHaveAttribute('aria-modal', 'true');
      await expectFocusVisible(page, aside.getByRole('combobox', { name: 'Organization' }));
      await page.keyboard.press('Tab');
      await expectFocusVisible(page, aside.getByRole('combobox', { name: 'Project' }));
      const links = aside.locator('app-shell-nav nav a');
      for (let index = 0; index < (await links.count()); index += 1) {
        await page.keyboard.press('Tab');
        await expectFocusVisible(page, links.nth(index));
      }
      await page.keyboard.press('Tab');
      await expectFocusVisible(page, close);
      await page.keyboard.press('Shift+Tab');
      await expectFocusVisible(page, links.last());
      await page.keyboard.press('Escape');
      await expect(aside).toHaveAttribute('inert', '');
      await expect(navToggle).toBeFocused();
      await page.keyboard.press('Tab');
      await expect(page.locator('button.theme-toggle')).toBeFocused();
    });
  });
}
