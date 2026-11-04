// 09.1.1-T4.4 (plan 09.1.1 §10 T4, §2 AC5; 00 §8.9, §9.2): the two session states that must never
// render a workspace — no session (`401` → `/login`) and an unreadable session (`503` →
// `/unavailable`). Both are decided by the first read, so this is the one place the corpus's error
// variants (`orgs-list-unauthenticated`, `orgs-list-unavailable`) drive the app.
import { expect, test } from '@playwright/test';

import {
  ANONYMOUS_SESSION,
  localePath,
  mainText,
  ORG_ACME,
  PROJECT_STOREFRONT,
  UNREADABLE_SESSION,
  withVariant,
} from './support';

const DEEP_LINK = `/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}/overview`;

test.describe('no usable session (AC5)', () => {
  test('a deep link becomes /login with the attempted path as returnUrl', async ({ page }) => {
    await page.goto(withVariant(localePath('en', DEEP_LINK), ANONYMOUS_SESSION));

    await expect.poll(() => new URL(page.url()).pathname).toBe('/en/login');
    const returnUrl = new URL(page.url()).searchParams.get('returnUrl');
    // The path is rebuilt from the matched segments, so it is a same-origin absolute path this app
    // produced — never a value echoed from the query string.
    expect(returnUrl).toBe(DEEP_LINK);

    // A signed-out visitor sees a page, not an empty workspace: no shell, no navigation, no scope.
    await expect(page.locator('app-shell')).toHaveCount(0);
    await expect(page.locator('app-scope-switcher')).toHaveCount(0);
    await expect(page.getByRole('navigation')).toHaveCount(0);
    await expect(page.locator('#main-content')).toHaveCount(0);
    // 09.1.1-T5.1 replaced the T4 placeholder with the real sign-in screen: the page is a login
    // card that offers the provider buttons its flow has (D-S98-Q3) and no password field.
    await expect(page.locator('main h1')).toHaveText('Sign in to Evidgate');
    await expect(page.getByRole('button', { name: 'Continue with Google' })).toBeEnabled();
    await expect(page.getByRole('button', { name: 'Continue with GitHub' })).toBeEnabled();
    await expect(page.locator('main input')).toHaveCount(0);
    // No notice: nothing happened to this tab yet.
    await expect(page.locator('.login-notice')).toHaveText('');
    // The card links nowhere: every route an anonymous visitor can reach ends back here (the `/`
    // landing redirect), so a link would loop.
    await expect(page.locator('main a')).toHaveCount(0);
  });

  test('the workspace root goes to /login without a returnUrl', async ({ page }) => {
    await page.goto(withVariant(localePath('en', '/'), ANONYMOUS_SESSION));

    await expect.poll(() => new URL(page.url()).pathname).toBe('/en/login');
    // The attempt was `/` itself; a login round trip back to `/` needs no parameter.
    expect(new URL(page.url()).search).toBe('');
    await expect(page.locator('main h1')).toHaveText('Sign in to Evidgate');
  });

  test('the redirect speaks the bundle’s language', async ({ page }) => {
    await page.goto(withVariant(localePath('vi', DEEP_LINK), ANONYMOUS_SESSION));

    await expect.poll(() => new URL(page.url()).pathname).toBe('/vi/login');
    await expect(page.locator('html')).toHaveAttribute('lang', 'vi');
    // vi: `@@auth.login.heading` ("Đăng nhập vào Evidgate"), so the redirect speaks the bundle's
    // language rather than the source language.
    await expect(page.locator('main h1')).toHaveText('Đăng nhập vào Evidgate');
  });
});

test.describe('a session that cannot be read (AC5)', () => {
  test('a 503 becomes /unavailable inside the shell, with a retry control', async ({ page }) => {
    await page.goto(withVariant(localePath('en', DEEP_LINK), UNREADABLE_SESSION));

    await expect.poll(() => new URL(page.url()).pathname).toBe('/en/unavailable');
    // The pattern page is a shell page: the visitor keeps their bearings, and the retry is the
    // only action that can succeed.
    await expect(page.locator('app-shell')).toHaveCount(1);
    await expect(page.locator('#main-content h1')).toHaveText('Workspace unavailable');
    await expect(page.locator('#main-content p').first()).toHaveText(
      'We could not load your workspace. Try again.',
    );
    const retry = page.locator('#main-content button');
    await expect(retry).toBeEnabled();
    await expect(retry).toHaveText('Try again');
    // The status region exists before it has something to say, so a failure is announced in place.
    await expect(page.locator('#main-content [role="status"]')).toHaveText('');
    await expect(page.locator('#main-content section')).toHaveAttribute('aria-busy', 'false');
    // Nothing of the workspace leaked into the failure: no scope, no navigation rows, no project.
    await expect(page.locator('app-scope-switcher [role="combobox"]').first()).toBeDisabled();
    expect(await mainText(page)).not.toContain('Storefront web');
  });

  test('the workspace root fails the same way', async ({ page }) => {
    await page.goto(withVariant(localePath('en', '/'), UNREADABLE_SESSION));
    await expect.poll(() => new URL(page.url()).pathname).toBe('/en/unavailable');
    await expect(page.locator('#main-content h1')).toHaveText('Workspace unavailable');
  });

  test('an unknown organization does not hide the failure as a 404', async ({ page }) => {
    // The session read fails before any scope can be resolved, so "unreadable" wins over
    // "not found": telling a visitor that an address does not exist would be a guess.
    await page.goto(
      withVariant(
        localePath('en', `/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}`),
        UNREADABLE_SESSION,
      ),
    );
    await expect.poll(() => new URL(page.url()).pathname).toBe('/en/unavailable');
  });
});
