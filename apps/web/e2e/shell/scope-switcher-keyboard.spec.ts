// 09.1.1-T4.2 (plan 09.1.1 §10 T4, §2 AC4/AC6; 00 §5.6 W9): the scope switcher commits a keyboard
// step instead of navigating on it. A closed native `<select>` fires `change` on every arrow,
// Home, End and Page key in Chromium and Firefox, so a switcher that listens to `change` alone
// moves the visitor to another project (and triggers a full scope resolve) on each keypress —
// WCAG 3.2.2 On Input. jsdom dispatches no `change` for a key press, so only a real browser can
// reproduce the behaviour this spec pins.
import { expect, test } from '@playwright/test';
import type { Page } from '@playwright/test';

import {
  LIMITED_GRANTS,
  ORG_ACME,
  overviewPath,
  PROJECT_CHECKOUT,
  PROJECT_STOREFRONT,
  withVariant,
} from './support';

/** The project screen the default corpus session reaches with the trimmed grant set. */
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

test.describe('scope switcher keyboard commit (AC4)', () => {
  test('an arrow-key step does not navigate; Enter commits the stepped project', async ({
    page,
  }) => {
    await page.goto(withVariant(overviewPath('en'), LIMITED_GRANTS));
    await expect(page.locator('#main-content h1')).toHaveText('Storefront web');
    const projectSelect = page.locator('app-scope-switcher select').nth(1);
    await expect(projectSelect).toBeEnabled();
    await expect(projectSelect).toHaveValue(PROJECT_STOREFRONT);
    const before = new URL(page.url()).pathname;

    await projectSelect.focus();
    await page.keyboard.press('ArrowDown');
    // The browser applied the step to the control itself ...
    await expect(projectSelect).toHaveValue(PROJECT_CHECKOUT);
    // ... but the shell held it: the address must still name the previous project.
    await settle(page);
    expect(new URL(page.url()).pathname).toBe(before);

    await page.keyboard.press('Enter');
    await expect.poll(() => new URL(page.url()).pathname).toBe(CHECKOUT_OVERVIEW);
    await expect(page.locator('#main-content h1')).toHaveText('Checkout API');
  });

  test('leaving the select after a step commits it', async ({ page }) => {
    await page.goto(withVariant(overviewPath('en'), LIMITED_GRANTS));
    await expect(page.locator('#main-content h1')).toHaveText('Storefront web');
    const projectSelect = page.locator('app-scope-switcher select').nth(1);
    await expect(projectSelect).toBeEnabled();
    const before = new URL(page.url()).pathname;

    await projectSelect.focus();
    await page.keyboard.press('ArrowDown');
    await expect(projectSelect).toHaveValue(PROJECT_CHECKOUT);
    await settle(page);
    expect(new URL(page.url()).pathname).toBe(before);

    // Tab away: the control loses focus, which is the other way to commit a held step.
    await page.keyboard.press('Tab');
    await expect.poll(() => new URL(page.url()).pathname).toBe(CHECKOUT_OVERVIEW);
    // A committed change is a path change, so the shell hands focus to the new page's region.
    await expect(page.locator('#main-content')).toBeFocused();
  });
});
