// The shell's scope switcher is the one control that rewrites the address wholesale, so this
// follows it end to end: the URL must name what was chosen (or the refusal), and the remaining
// scope controls must agree with the new session rather than keep offering the old one.
import { expect, test } from '@playwright/test';

import {
  LIMITED_GRANTS,
  ORG_ACME,
  ORG_LUMEN,
  overviewPath,
  PROJECT_CHECKOUT,
  withVariant,
} from '../shell/support';
import { ScopeSwitcher } from './pages/scope-switcher.page';

const CHECKOUT_OVERVIEW = `/orgs/${ORG_ACME}/projects/${PROJECT_CHECKOUT}/overview`;

test.describe('the scope switcher moves the shell', () => {
  test('choosing a project rewrites the project segment and keeps the screen', async ({ page }) => {
    // The seeded tenant has no `project:read`, so the project screen is reached through the
    // catalogue variant that grants it; the switcher itself is the subject here.
    await page.goto(withVariant(overviewPath('en'), LIMITED_GRANTS));
    await expect(page.locator('#main-content h1')).toHaveText('Storefront web');

    const scope = new ScopeSwitcher(page);
    await expect(scope.control(1)).toBeEnabled();
    await scope.choose(1, PROJECT_CHECKOUT);

    await expect.poll(() => new URL(page.url()).pathname).toBe(`/en${CHECKOUT_OVERVIEW}`);
    // The screen after the project id is preserved, not reset to the entry route.
    await expect(page.locator('#main-content h1')).toHaveText('Checkout API');
  });

  test('the vi bundle keeps its locale prefix across a project switch', async ({ page }) => {
    await page.goto(withVariant(overviewPath('vi'), LIMITED_GRANTS));
    await expect(page.locator('#main-content h1')).toHaveText('Storefront web');

    const scope = new ScopeSwitcher(page);
    await expect(scope.control(1)).toBeEnabled();
    await scope.choose(1, PROJECT_CHECKOUT);

    await expect.poll(() => new URL(page.url()).pathname).toBe(`/vi${CHECKOUT_OVERVIEW}`);
    await expect(page.locator('html')).toHaveAttribute('lang', 'vi');
    await expect(page.locator('#main-content h1')).toBeVisible();
    // The first project is still the one the current organization names.
    await expect(scope.control(0)).toBeEnabled();
  });

  test('choosing the suspended organization lands on the refusal and drops project scope', async ({
    page,
  }) => {
    await page.goto(withVariant(overviewPath('en'), LIMITED_GRANTS));
    await expect(page.locator('#main-content h1')).toHaveText('Storefront web');

    const scope = new ScopeSwitcher(page);
    await expect(scope.control(0)).toBeEnabled();
    await scope.choose(0, ORG_LUMEN);

    // The address stops naming the old organization and ends on the documented refusal; no
    // project of the suspended organization is offered afterwards.
    await expect.poll(() => new URL(page.url()).pathname).toBe('/en/forbidden');
    expect(new URL(page.url()).pathname).not.toContain(ORG_ACME);
    await expect(page.locator('#main-content h1')).toBeVisible();
    await expect(scope.control(1)).toBeDisabled();
  });
});
