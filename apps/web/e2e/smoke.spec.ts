import { expect, test } from '@playwright/test';

// 09.1.1-T1.2 (AC2, 00 §5.6 W9): the smoke test runs the whole chain — `webServer` start,
// chromium launch, the built bundle in `dist/browser`, Angular bootstrap — so a broken build,
// missing browser install or bad config fails the gate instead of passing an empty suite.
// It asserts rendered output (`app-root` contains the heading from `src/app/app.html`), not
// just the served `index.html`: a static file server alone would satisfy a title assertion.
test('boots the built Angular bundle without console or page errors', async ({ page }) => {
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

  // Title and `h1` both come from the app sources (`src/index.html`, `src/app/app.html`).
  await expect(page).toHaveTitle('Evidgate');
  await expect(page.locator('app-root h1')).toHaveText('Evidgate');
  await expect(page.locator('app-root')).not.toBeEmpty();

  expect(pageErrors, 'page errors during bootstrap').toEqual([]);
  expect(consoleErrors, 'console errors during bootstrap').toEqual([]);
});
