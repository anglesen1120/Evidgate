// The core flow starts from the seeded tenant's stored browser state, so the anonymous cases must
// start from none: the 401 below comes from the fixture corpus (`listMyOrganizations` answers
// `unauthenticated`), never from the app.
import { expect, test } from '@playwright/test';

import {
  ANONYMOUS_SESSION,
  localePath,
  ORG_ACME,
  PROJECT_STOREFRONT,
  withVariant,
} from '../shell/support';
import { LoginPage } from './pages/login.page';

const DEEP_LINK = `/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}/overview`;

test.use({ storageState: { cookies: [], origins: [] } });

test.describe('an anonymous visitor is sent to sign-in', () => {
  test('a deep link becomes /login and keeps the attempted path as returnUrl', async ({ page }) => {
    await page.goto(withVariant(localePath('en', DEEP_LINK), ANONYMOUS_SESSION));

    await expect.poll(() => new URL(page.url()).pathname).toBe('/en/login');
    const login = new LoginPage(page);
    // The return path is rebuilt from matched route segments, so it is this app's own path and
    // never a value echoed from the query string.
    expect(login.returnUrl()).toBe(DEEP_LINK);
    await login.expectReady();
    await expect(login.notice).toHaveText('');

    // The English labels are the subject here; other cases key on roles and ids only.
    await expect(login.providerButtons.first()).toHaveText('Continue with Google');
    await expect(login.providerButtons.nth(1)).toHaveText('Continue with GitHub');

    // A refused visitor sees the sign-in page, not a workspace with empty panels.
    await expect(page.locator('app-shell')).toHaveCount(0);
    await expect(page.locator('app-scope-switcher')).toHaveCount(0);
    await expect(page.locator('#main-content')).toHaveCount(0);
  });

  test('the vi bundle keeps the deep link and speaks Vietnamese', async ({ page }) => {
    await page.goto(withVariant(localePath('vi', DEEP_LINK), ANONYMOUS_SESSION));

    await expect.poll(() => new URL(page.url()).pathname).toBe('/vi/login');
    await expect(page.locator('html')).toHaveAttribute('lang', 'vi');
    const login = new LoginPage(page);
    expect(login.returnUrl()).toBe(DEEP_LINK);
    await login.expectReady();
  });

  test('the workspace root becomes /login without a returnUrl', async ({ page }) => {
    await page.goto(withVariant(localePath('en', '/'), ANONYMOUS_SESSION));

    await expect.poll(() => new URL(page.url()).pathname).toBe('/en/login');
    const login = new LoginPage(page);
    // The refused attempt was `/` itself: sending it back there needs no parameter.
    expect(login.returnUrl()).toBeNull();
    await login.expectReady();
  });
});
