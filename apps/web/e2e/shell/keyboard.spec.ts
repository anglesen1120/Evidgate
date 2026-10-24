// 09.1.1-T4.4 (plan 09.1.1 §10 T4, §2 AC6; 00 §5.6 W9): the keyboard path through the shell. A
// keyboard or screen-reader visitor must be able to skip the sidebar, must land in the route's own
// region after every client-side navigation, and must never be trapped in the mobile drawer for
// longer than it is open. All three are DOM-order and focus properties: only a real browser has them.
import { expect, test } from '@playwright/test';

import { activeElement, currentTheme, localePath, ORG_ACME, PROJECT_STOREFRONT } from './support';

/** `/orgs/:orgId/projects/:projectId/authoring` — the default corpus session reaches it. */
const AUTHORING = localePath('en', `/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}/authoring`);

test.describe('skip link and focus handoff (AC6)', () => {
  test('the first Tab stop is the skip link, and it moves focus into the main region', async ({
    page,
  }) => {
    await page.goto(AUTHORING);
    await expect(page.locator('#main-content h1')).toBeVisible();

    // No click first: the browser starts every fresh document at the top of its tab order, so the
    // very first Tab must reach the skip link — if the shell put anything focusable before it, a
    // keyboard visitor would have to walk the whole sidebar on every page.
    await page.keyboard.press('Tab');
    // `toBeFocused` retries across the app's own history updates; a bare `document.activeElement`
    // read can lose a race with the router's replaceState on the first frame.
    await expect(page.locator('a.skip-link')).toBeFocused();

    await page.keyboard.press('Enter');
    await expect(page.locator('#main-content')).toBeFocused();
    // The skip link really jumped: its target is the focused element's id.
    expect((await activeElement(page)).tag).toBe('main');
    // The main region is programmatically focusable (`tabindex="-1"`) but never a Tab stop of its
    // own: the fragment target is what makes it reachable.
    await expect(page.locator('#main-content')).toHaveAttribute('tabindex', '-1');
  });

  test('a client-side navigation moves focus to the main region', async ({ page }) => {
    await page.goto(AUTHORING);
    await expect(page.locator('#main-content h1')).toBeVisible();

    await page.locator('app-shell-nav nav a[href$="/generation"]').click();
    await expect
      .poll(() => new URL(page.url()).pathname)
      .toBe(`/en/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}/generation`);
    await expect(page.locator('#main-content h1')).toBeVisible();

    // Without this, a keyboard visitor's focus would stay on the link they just used, which no
    // longer describes the page they are on.
    await expect(page.locator('#main-content')).toBeFocused();
  });

  test('the sidebar is one named navigation landmark of named groups', async ({ page }) => {
    await page.goto(AUTHORING);
    await expect(page.locator('app-shell-nav nav a').first()).toBeVisible();

    const navigation = page.getByRole('navigation');
    await expect(navigation).toHaveCount(1);
    await expect(navigation).toHaveAccessibleName('Primary navigation');

    // Every rendered group is a labelled `role="group"` (not a `<section>`, which would add a
    // `region` landmark per group, review L5), so a screen reader can announce "Design, group"
    // instead of one undifferentiated list.
    const groups = await page.locator('app-shell-nav nav [role="group"]').evaluateAll((sections) =>
      sections.map((section) => {
        const labelledBy = section.getAttribute('aria-labelledby') ?? '';
        const heading = labelledBy === '' ? null : section.querySelector('h2');
        return {
          headingId: heading?.id ?? '',
          labelledBy,
          level: heading?.tagName ?? '',
        };
      }),
    );
    expect(groups.length).toBeGreaterThan(0);
    for (const group of groups) {
      expect(group.level).toBe('H2');
      expect(group.headingId).not.toBe('');
      expect(group.labelledBy).toBe(group.headingId);
    }

    // The current page is marked for assistive technology, not by colour alone.
    await expect(
      page.locator(`app-shell-nav nav a[href$="/projects/${PROJECT_STOREFRONT}/authoring"]`),
    ).toHaveAttribute('aria-current', 'page');
  });
});

test.describe('mobile drawer (AC6)', () => {
  // Below the shell's 768px breakpoint the sidebar becomes a modal drawer.
  test.use({ viewport: { width: 375, height: 900 } });

  test('the drawer traps Tab, closes on Escape and returns focus to its opener', async ({
    page,
  }) => {
    await page.goto(AUTHORING);
    await expect(page.locator('#main-content h1')).toBeVisible();

    const aside = page.locator('#shell-navigation');
    const toggle = page.locator('button.nav-toggle');
    // Closed: the sidebar is inert, so its links are not Tab stops and not in the accessibility
    // tree, and the page behind it is the only thing that can take focus.
    await expect(aside).toHaveAttribute('inert', '');
    await expect(page.locator('.main-stack')).not.toHaveAttribute('inert', '');
    await expect(toggle).toHaveAttribute('aria-expanded', 'false');

    await toggle.click();
    await expect(aside).toHaveAttribute('role', 'dialog');
    await expect(aside).toHaveAttribute('aria-modal', 'true');
    await expect(aside).not.toHaveAttribute('inert', '');
    // The page behind the drawer is inert while it is open — the "trap" is not only a key handler.
    await expect(page.locator('.main-stack')).toHaveAttribute('inert', '');
    await expect(toggle).toHaveAttribute('aria-expanded', 'true');

    // Opening moves focus into the drawer, onto its first control (the close button).
    const closeButton = page.locator('#shell-navigation button.close-button');
    await expect(closeButton).toBeFocused();

    // Shift+Tab off the first control wraps to the last, and Tab off the last wraps back.
    await page.keyboard.press('Shift+Tab');
    const last = await activeElement(page);
    expect(last.tag).toBe('a');
    expect(last.className).not.toContain('close-button');
    expect(
      await page.evaluate(() => document.activeElement?.closest('#shell-navigation') !== null),
      'the wrap target is inside the drawer',
    ).toBe(true);
    await page.keyboard.press('Tab');
    await expect(closeButton).toBeFocused();

    // Escape closes the drawer and gives focus back to the control that opened it — never to the
    // document body, which would restart the whole tab order.
    await page.keyboard.press('Escape');
    await expect(aside).not.toHaveAttribute('role', 'dialog');
    await expect(toggle).toHaveAttribute('aria-expanded', 'false');
    await expect(toggle).toBeFocused();
    await expect(aside).toHaveAttribute('inert', '');

    // And with the drawer closed the next Tab does not walk into the (inert) sidebar.
    await page.keyboard.press('Tab');
    expect(
      await page.evaluate(() => document.activeElement?.closest('#shell-navigation') === null),
      'Tab skips the closed drawer',
    ).toBe(true);
  });

  test('choosing an entry closes the drawer and hands focus to the new page', async ({ page }) => {
    await page.goto(AUTHORING);
    await expect(page.locator('#main-content h1')).toBeVisible();

    await page.locator('button.nav-toggle').click();
    const aside = page.locator('#shell-navigation');
    await expect(aside).toHaveAttribute('role', 'dialog');

    await page.locator('app-shell-nav nav a[href$="/generation"]').click();
    await expect(aside).not.toHaveAttribute('role', 'dialog');
    await expect
      .poll(() => new URL(page.url()).pathname)
      .toBe(`/en/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}/generation`);
    await expect.poll(async () => (await activeElement(page)).id).toBe('main-content');
    // The drawer scrim is gone with it, so nothing invisible is left covering the page.
    await expect(page.locator('button.scrim')).toHaveCount(0);
  });

  test('the theme toggle is reachable and operable from the keyboard', async ({ page }) => {
    await page.goto(AUTHORING);
    const toggle = page.locator('button.theme-toggle');
    await toggle.focus();
    await expect(toggle).toBeFocused();

    // Enter chooses a theme (the corpus page may have no stored choice, so the first activation
    // either pins light or pins dark — both are explicit choices).
    await page.keyboard.press('Enter');
    await expect.poll(async () => currentTheme(page)).toMatch(/^(light|dark)$/);
    const chosen = await currentTheme(page);
    await expect(toggle).toHaveAttribute('aria-pressed', String(chosen === 'dark'));

    // Space is the other native activation key for a button, and it flips the same state.
    await page.keyboard.press('Space');
    await expect.poll(async () => currentTheme(page)).not.toBe(chosen);
    await expect(toggle).toHaveAttribute('aria-pressed', String(chosen !== 'dark'));
  });
});
