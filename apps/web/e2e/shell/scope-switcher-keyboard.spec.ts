// Scope exploration never changes the URL until the visitor activates an option.
import { expect, test } from '@playwright/test';
import type { Page } from '@playwright/test';
import { expectScopeValue, exploreScope, scopeControl } from './scope-controls';

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
    const projectSelect = scopeControl(page, 1);
    await expect(projectSelect).toBeEnabled();
    await expectScopeValue(projectSelect, PROJECT_STOREFRONT);
    const before = new URL(page.url()).pathname;

    await projectSelect.focus();
    await exploreScope(page, projectSelect, PROJECT_CHECKOUT);
    // The shell holds the step: the address must still name the previous project.
    await settle(page);
    expect(new URL(page.url()).pathname).toBe(before);

    await page.keyboard.press('Enter');
    await expect.poll(() => new URL(page.url()).pathname).toBe(CHECKOUT_OVERVIEW);
    await expect(page.locator('#main-content h1')).toHaveText('Checkout API');
  });

  test('Tab dismisses an explored option without changing scope', async ({ page }) => {
    await page.goto(withVariant(overviewPath('en'), LIMITED_GRANTS));
    await expect(page.locator('#main-content h1')).toHaveText('Storefront web');
    const projectSelect = scopeControl(page, 1);
    await expect(projectSelect).toBeEnabled();
    const before = new URL(page.url()).pathname;

    await projectSelect.focus();
    await exploreScope(page, projectSelect, PROJECT_CHECKOUT);
    await settle(page);
    expect(new URL(page.url()).pathname).toBe(before);

    await page.keyboard.press('Tab');
    await expect(projectSelect).toHaveAttribute('aria-expanded', 'false');
    expect(new URL(page.url()).pathname).toBe(before);
    await expectScopeValue(projectSelect, PROJECT_STOREFRONT);
    const localeToggle = page.locator('header.topbar button.locale-toggle');
    await expect(localeToggle).toBeFocused();
    await expect(localeToggle).toHaveAccessibleName(
      'EN / VI: Switch language between English and Vietnamese',
    );
    await page.keyboard.press('Tab');
    await expect(page.locator('button.theme-toggle')).toBeFocused();
    expect(new URL(page.url()).pathname).toBe(before);
  });
});
