import { expect, test } from '@playwright/test';

// 09.1.1-T1.2 (AC2, 00 §5.6 W9): the smoke test runs the whole chain — `webServer` start,
// chromium launch, the built bundle in `dist/browser`, Angular bootstrap — so a broken build,
// missing browser install or bad config fails the gate instead of passing an empty suite.
// It asserts rendered output (`app-root` contains the heading from `src/app/app.html`), not
// just the served `index.html`: a static file server alone would satisfy a title assertion.
//
// 09.1.1-T6.1: the bundle is localized, so the suite enters through the locale routing. `/`
// redirects to `/vi/` — Vietnamese is the product's default language — and `/en/` serves the
// English bundle. `<html lang>` is the locale the builder inlined into that bundle, so it is
// what proves the redirect landed on the vi bundle and not on a page that merely renders the
// same brand text.
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
  expect(new URL(page.url()).pathname).toBe('/vi/');
  await expect(page.locator('html')).toHaveAttribute('lang', 'vi');

  // Title and `h1` both come from the app sources (`src/index.html`, `src/app/app.html`).
  await expect(page).toHaveTitle('Evidgate');
  await expect(page.locator('app-root h1')).toHaveText('Evidgate');
  await expect(page.locator('app-root')).not.toBeEmpty();

  expect(pageErrors, 'page errors during bootstrap').toEqual([]);
  expect(consoleErrors, 'console errors during bootstrap').toEqual([]);
});

test('serves the English bundle under /en', async ({ page }) => {
  const pageErrors: string[] = [];
  page.on('pageerror', (error) => {
    pageErrors.push(error.message);
  });

  const response = await page.goto('/en/');
  expect(response?.status()).toBe(200);
  expect(new URL(page.url()).pathname).toBe('/en/');
  await expect(page.locator('html')).toHaveAttribute('lang', 'en');

  await expect(page).toHaveTitle('Evidgate');
  await expect(page.locator('app-root h1')).toHaveText('Evidgate');

  expect(pageErrors, 'page errors during bootstrap').toEqual([]);
});
