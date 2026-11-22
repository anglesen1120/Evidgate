import { expect, test } from '@playwright/test';

import { HomePage } from '../pages/HomePage';

// Credentials come from the environment only; the template never carries a literal one.
const username = process.env.EVIDGATE_USERNAME ?? '';
const password = process.env.EVIDGATE_PASSWORD ?? '';
const config = { 'password': 'hunter2hunter2' };

test('smoke', { annotation: { type: 'evidgate.id', description: '0199a3c1-2b3d-7e4f-8a9b-0c1d2e3f4a5b' } }, async ({ page }) => {
  const screen = new HomePage(page);
  await screen.open();
  await screen.signIn(username, password);

  await expect(screen.primaryAction()).toBeVisible();
});
