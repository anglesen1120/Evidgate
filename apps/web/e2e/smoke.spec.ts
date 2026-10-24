import { expect, test } from '@playwright/test';

// 09.1.1-T1.2 (AC2, 00 §5.6 W9): the smoke test runs the whole chain — `webServer` start,
// chromium launch, the built bundle in `dist/browser`, Angular bootstrap — so a broken build,
// missing browser install or bad config fails the gate instead of passing an empty suite.
// It asserts rendered output, not just the served `index.html`: a static file server alone would
// satisfy a title assertion.
//
// 09.1.1-T6.1: the bundle is localized, so the suite enters through the locale routing. `/`
// redirects to `/vi/` — Vietnamese is the product's default language — and `/en/` serves the
// English bundle. `<html lang>` is the locale the builder inlined into that bundle, so it is
// what proves the redirect landed on the vi bundle and not on a page that merely renders the
// same brand text.
//
// 09.1.1-T4.1: the app boots into the router (`src/app/app.html` is a bare `<router-outlet />`),
// and this bundle has NO API behind it: `e2e/static-server.mjs` serves files only, so the session
// probe's `/v1/orgs` request is answered by the SPA fallback with `index.html` and cannot become a
// session. The shell therefore ends on one of its own pattern pages (the unavailable page, or the
// sign-in placeholder for an anonymous answer) inside `app-shell`. The assertions below pin what is
// true either way — a rendered `h1`, the locale of that bundle, the `/vi/` prefix and a clean
// console — and deliberately not which pattern page the failed probe selected, because that is
// `app.routes.spec.ts`'s subject, not the boot chain's.
test('boots the built Angular bundle at the default locale (vi)', async ({ page }) => {
  const consoleErrors: string[] = [];
  const pageErrors: string[] = [];
  page.on('console', (message) => {
    if (message.type() === 'error') {
      consoleErrors.push(message.text());
    }
  });
  page.on('pageerror', (error) => {
    pageErrors.push(error.message);
  });

  const response = await page.goto('/');
  expect(response?.status()).toBe(200);
  expect(new URL(page.url()).pathname.startsWith('/vi/')).toBe(true);
  await expect(page.locator('html')).toHaveAttribute('lang', 'vi');

  // `src/index.html` carries `Evidgate`; the shell prefixes the routed page heading (`<page> · Evidgate`).
  await expect(page).toHaveTitle(/Evidgate$/);
  await expect(page.locator('app-root app-shell')).toBeVisible();
  const heading = page.locator('app-root h1').first();
  await expect(heading).toBeVisible();
  expect((await heading.textContent())?.trim()).not.toBe('');

  expect(pageErrors, 'page errors during bootstrap').toEqual([]);
  expect(consoleErrors, 'console errors during bootstrap').toEqual([]);
});

test('serves the English bundle under /en', async ({ page }) => {
  const consoleErrors: string[] = [];
  const pageErrors: string[] = [];
  page.on('console', (message) => {
    if (message.type() === 'error') {
      consoleErrors.push(message.text());
    }
  });
  page.on('pageerror', (error) => {
    pageErrors.push(error.message);
  });

  const response = await page.goto('/en/');
  expect(response?.status()).toBe(200);
  expect(new URL(page.url()).pathname.startsWith('/en/')).toBe(true);
  await expect(page.locator('html')).toHaveAttribute('lang', 'en');

  await expect(page).toHaveTitle(/Evidgate$/);
  await expect(page.locator('app-root app-shell')).toBeVisible();
  const heading = page.locator('app-root h1').first();
  await expect(heading).toBeVisible();
  expect((await heading.textContent())?.trim()).not.toBe('');

  expect(pageErrors, 'page errors during bootstrap').toEqual([]);
  expect(consoleErrors, 'console errors during bootstrap').toEqual([]);
});
