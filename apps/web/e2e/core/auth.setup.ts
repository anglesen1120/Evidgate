// Stores the seeded fixture tenant's browser state once per shard. The fixture backend answers the
// session in-process, so the durable part of "signed in" is what the app keeps in the browser (the
// visitor's theme choice); the setup proves the seeded session reaches the shell, then saves that
// state for every core spec to start from.
import { expect, test as setup } from '@playwright/test';

import { overviewPath, seedStoredTheme } from '../shell/support';

setup('seeded fixture tenant reaches the shell', async ({ page }) => {
  await seedStoredTheme(page, 'light');
  await page.goto(overviewPath('en'));
  await expect(page.locator('app-shell-nav')).toBeVisible();
  await page.context().storageState({ path: 'test-results/core-auth-state.json' });
});
