import { expect, test } from '@playwright/test';

// 01.6.1-T5.1 (AC7): the only E2E the workspace owns until 09.1.1-T2 replaces the static
// page with the Angular application. It runs the whole chain — `webServer` start, chromium
// launch, navigation — so a broken browser install or config fails the gate instead of
// passing an empty suite.
test('serves the placeholder page with its title', async ({ page }) => {
  await page.goto('/');
  await expect(page).toHaveTitle('Evidgate web (placeholder)');
});
