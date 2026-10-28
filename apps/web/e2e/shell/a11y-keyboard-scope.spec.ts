// 09.8.1-T1.2 (plan 09.8.1 §10 T1.2, §2 AC2; finding F-SWITCH-01 in docs/a11y): the scope flow is
// walked with the keyboard ALONE — every focus move in this file is a real `page.keyboard.press`,
// no `click`, no `force` — at the desktop width and at 360px, and every stop is asserted by element
// identity plus geometry, never by counting Tabs.
//
// The finding was a focus order that followed paint rather than the document: below 768px the
// `.topbar` wraps, the two scope selects are painted on a second row, and a `order: 3` rule lifted
// them below the theme toggle's row — while the theme toggle stayed AFTER the selects in the DOM.
// Tab therefore reached navigation toggle → organization → project → theme toggle, so the last
// keyboard stop jumped back up into the first visual row (WCAG 2.4.3 Focus Order). The fix moved
// the theme toggle before the scope group in the document and deleted the reorder, so the topbar's
// keyboard order is its visual order at both widths; this spec pins that contract on the real
// browser, where layout and sequential focus navigation exist.
//
// F-SWITCH-02 (a held step committing on blur) was judged not a defect and stays pinned by
// `scope-switcher-keyboard.spec.ts`; the commit cases below only assert the handoff a committed
// change makes, and the url/step behaviour those cases already own is not re-litigated here.
import { expect, test } from '@playwright/test';
import type { Locator, Page } from '@playwright/test';

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

/**
 * Reproduces what Chromium and Firefox on Windows and Linux do for ArrowDown on a focused, closed
 * select: a `keydown` followed by `input` and `change` with the stepped value. macOS opens the
 * picker instead, so a real key press would not step there; the Enter and Tab that follow stay real
 * key presses (same helper as `scope-switcher-keyboard.spec.ts`, which owns the held-step rules).
 */
async function stepWithArrow(select: Locator, value: string): Promise<void> {
  await select.evaluate((element: HTMLSelectElement, next: string) => {
    element.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }));
    element.value = next;
    element.dispatchEvent(new Event('input', { bubbles: true }));
    element.dispatchEvent(new Event('change', { bubbles: true }));
  }, value);
  await expect(select).toHaveValue(value);
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

    // 4–7. The topbar: navigation toggle → theme toggle → organization → project. The theme toggle
    // used to be the LAST stop of this row while being painted first (F-SWITCH-01).
    const navToggle = page.locator('button.nav-toggle');
    await page.keyboard.press('Tab');
    await expectFocusVisible(page, navToggle);
    await expect(navToggle).toHaveAccessibleName('Collapse navigation');

    const themeToggle = page.locator('button.theme-toggle');
    await page.keyboard.press('Tab');
    await expectFocusVisible(page, themeToggle);
    await expect(themeToggle).toHaveAttribute('aria-pressed', /^(true|false)$/);

    const orgSelect = page.getByRole('combobox', { name: 'Organization' });
    await page.keyboard.press('Tab');
    await expectFocusVisible(page, orgSelect);
    await expect(orgSelect).toHaveValue(ORG_ACME);
    // The disabled placeholder is one of the three rows: a blank scope is never a choice.
    expect(await orgSelect.locator('option').count()).toBe(3);

    const projectSelect = page.getByRole('combobox', { name: 'Project' });
    await page.keyboard.press('Tab');
    await expectFocusVisible(page, projectSelect);
    await expect(projectSelect).toHaveValue(PROJECT_STOREFRONT);

    // Traversing the switcher is not a commitment: only a step plus Enter, or a blur, commits, and
    // no step was taken (WCAG 3.2.2 — no change on focus or on tabbing through a control).
    expect(new URL(page.url()).pathname).toBe(startPath);

    // The keyboard order IS the visual order here: four stops, one row, left to right.
    const [navBox, themeBox, orgBox, projectBox] = await Promise.all([
      boxOf(navToggle),
      boxOf(themeToggle),
      boxOf(orgSelect),
      boxOf(projectSelect),
    ]);
    expect(
      themeBox.x,
      'the theme toggle is painted right of the navigation toggle',
    ).toBeGreaterThan(navBox.x);
    expect(
      orgBox.x,
      'the organization select is painted right of the theme toggle',
    ).toBeGreaterThan(themeBox.x);
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
    // The toggle joins the header's leading icon cluster: the auto margin that right-aligns it
    // below 768px gets no free space at this width, because the scope group grows into all of it.
    expect(
      themeBox.x,
      'the theme toggle starts within one topbar gap of the navigation toggle',
    ).toBeLessThan(navBox.x + navBox.width + 24);

    // 8. The project select is the header's last stop, so the next Tab leaves the topbar instead of
    // walking back up to a control painted above it.
    await page.keyboard.press('Tab');
    await expect(projectSelect).not.toBeFocused();
    expect(await focusInsideTopbar(page)).toBe(false);
  });

  test('the keyboard walk commits a project, and the shell hands focus to the page', async ({
    page,
  }) => {
    await page.goto(withVariant(overviewPath('en'), LIMITED_GRANTS));
    await expect(page.locator('#main-content h1')).toHaveText('Storefront web');
    const projectSelect = page.getByRole('combobox', { name: 'Project' });
    await expect(projectSelect).toBeEnabled();

    // Reached the way the walk reaches it, not by a click: focus from a real key press, then the
    // step is held until Enter commits it.
    await projectSelect.focus();
    await stepWithArrow(projectSelect, PROJECT_CHECKOUT);
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
    // the same destination, reached without a pointer.
    const orgSelect = page.getByRole('combobox', { name: 'Organization' });
    await orgSelect.focus();
    await stepWithArrow(orgSelect, ORG_LUMEN);
    await page.keyboard.press('Enter');

    await expect.poll(() => new URL(page.url()).pathname).toBe('/en/forbidden');
    await expect(page.locator('#main-content h1')).toHaveText('Not authorized');
    // The scope did move: the organization is LUMEN and no project of it is offered.
    await expect(orgSelect).toHaveValue(ORG_LUMEN);
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
      page.locator('button.theme-toggle'),
      page.locator('app-scope-switcher select').nth(0),
      page.locator('app-scope-switcher select').nth(1),
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

test.describe('scope flow at 360px: keyboard order (AC2, F-SWITCH-01)', () => {
  // The width the finding was reported at: the selects are a second (and, at ≤420px, third) row.
  test.use({ viewport: { width: 360, height: 900 } });

  test('mobile: Tab follows the two rows — toggles first, then organization, then project', async ({
    page,
  }) => {
    await page.goto(withVariant(overviewPath('en'), LIMITED_GRANTS));
    await expect(page.locator('#main-content h1')).toHaveText('Storefront web');
    const startPath = new URL(page.url()).pathname;
    // Below 768px the sidebar is a closed, inert drawer: it must not take any of these stops.
    await expect(page.locator('#shell-navigation')).toHaveAttribute('inert', '');

    await page.keyboard.press('Tab');
    const skipLink = page.locator('a.skip-link');
    await expectFocusVisible(page, skipLink);

    await page.keyboard.press('Tab');
    const navToggle = page.locator('button.nav-toggle');
    await expectFocusVisible(page, navToggle);
    await expect(navToggle).toHaveAccessibleName('Open navigation');
    await expect(navToggle).toHaveAttribute('aria-expanded', 'false');

    // The theme toggle is the SECOND stop, in the first visual row beside the navigation toggle.
    // Before the fix it was the fourth stop, painted in that same first row: Tab reached the two
    // selects in the second row and then jumped back up (F-SWITCH-01).
    await page.keyboard.press('Tab');
    const themeToggle = page.locator('button.theme-toggle');
    await expectFocusVisible(page, themeToggle);

    await page.keyboard.press('Tab');
    const orgSelect = page.getByRole('combobox', { name: 'Organization' });
    await expectFocusVisible(page, orgSelect);
    await expect(orgSelect).toHaveValue(ORG_ACME);

    await page.keyboard.press('Tab');
    const projectSelect = page.getByRole('combobox', { name: 'Project' });
    await expectFocusVisible(page, projectSelect);
    await expect(projectSelect).toHaveValue(PROJECT_STOREFRONT);
    expect(new URL(page.url()).pathname).toBe(startPath);

    // The geometry of the walk, in the same sequence: toggles on row 1 left to right, the selects
    // below them, the project select below the organization select at this width.
    const [navBox, themeBox, orgBox, projectBox] = await Promise.all([
      boxOf(navToggle),
      boxOf(themeToggle),
      boxOf(orgSelect),
      boxOf(projectSelect),
    ]);
    expectSameRow(navBox, themeBox, 'both toggles share the first row');
    expect(themeBox.x, 'the theme toggle ends the first row').toBeGreaterThan(navBox.x);
    expect(
      orgBox.y,
      'the organization select is painted below the first row',
    ).toBeGreaterThanOrEqual(themeBox.y + themeBox.height);
    expect(
      projectBox.y,
      'the project select is painted below the organization select',
    ).toBeGreaterThan(orgBox.y);
    // The theme toggle closes the first row on the same inline-end edge as the rows below it: the
    // auto margin puts it there, not in a beginning-edge cluster as on wider screens.
    expect(
      Math.abs(themeBox.x + themeBox.width - (projectBox.x + projectBox.width)),
      'the first row ends flush with the scope rows',
    ).toBeLessThanOrEqual(2);

    // And the header's last stop is the project select: focus moves on into the page.
    await page.keyboard.press('Tab');
    await expect(projectSelect).not.toBeFocused();
    expect(await focusInsideTopbar(page)).toBe(false);
  });

  test('mobile: the drawer still traps focus, closes on Escape and returns it in the fixed order', async ({
    page,
  }) => {
    await page.goto(withVariant(overviewPath('en'), LIMITED_GRANTS));
    await expect(page.locator('#main-content h1')).toHaveText('Storefront web');

    const navToggle = page.locator('button.nav-toggle');
    const themeToggle = page.locator('button.theme-toggle');
    const orgSelect = page.getByRole('combobox', { name: 'Organization' });

    // Walk to the organization select by keyboard, then Shift+Tab back out of it: the theme toggle
    // and the navigation toggle are the two stops above it in the fixed order.
    await navToggle.focus();
    await page.keyboard.press('Tab');
    await expect(themeToggle).toBeFocused();
    await page.keyboard.press('Tab');
    await expect(orgSelect).toBeFocused();
    await page.keyboard.press('Shift+Tab');
    await expect(themeToggle).toBeFocused();
    await page.keyboard.press('Shift+Tab');
    await expect(navToggle).toBeFocused();

    // Enter opens the drawer; it is modal, focus lands on its close button and the page behind is
    // inert (the trap is a property of the DOM, not only a key handler).
    const aside = page.locator('#shell-navigation');
    await page.keyboard.press('Enter');
    await expect(aside).toHaveAttribute('role', 'dialog');
    await expect(page.locator('.main-stack')).toHaveAttribute('inert', '');
    await expect(page.locator('#shell-navigation button.close-button')).toBeFocused();

    // Escape closes it and gives focus back to the control that opened it.
    await page.keyboard.press('Escape');
    await expect(aside).not.toHaveAttribute('role', 'dialog');
    await expect(aside).toHaveAttribute('inert', '');
    await expect(navToggle).toBeFocused();

    // The order resumes exactly where it left off: theme toggle, then organization, then project.
    await page.keyboard.press('Tab');
    await expect(themeToggle).toBeFocused();
    await page.keyboard.press('Tab');
    await expect(orgSelect).toBeFocused();
    await page.keyboard.press('Tab');
    await expect(page.getByRole('combobox', { name: 'Project' })).toBeFocused();
  });
});
