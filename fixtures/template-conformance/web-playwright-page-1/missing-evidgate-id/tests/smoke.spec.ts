import { expect, test } from '@playwright/test';

import { HomePage } from '../pages/HomePage';

// Credentials come from the environment only; the template never carries a literal one.
const username = process.env.EVIDGATE_USERNAME ?? '';
const password = process.env.EVIDGATE_PASSWORD ?? '';

test('smoke', async ({ page }) => {
  const screen = new HomePage(page);
  await screen.open();
  await screen.signIn(username, password);

  await expect(screen.primaryAction()).toBeVisible();
});
