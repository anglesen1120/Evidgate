import { expect, test } from '@playwright/test';
import { LOCALES, ORG_ACME, PROJECT_STOREFRONT, localePath } from './support';

const screens = [
  { route: 'agent-pools', subject: 'browser-ci-east', fact: 'pool_01HX6D3K9M' },
  { route: 'webhooks', subject: 'wh-19', fact: 'https://ci.example.test/hooks/evidgate' },
  { route: 'support-access', subject: 'sg-41', fact: 'req-sg-41' },
] as const;

for (const { route, subject, fact } of screens) {
  for (const locale of LOCALES) {
    test(`${route}: inspect a loaded record with the keyboard (${locale})`, async ({ page }) => {
      await page.goto(
        localePath(locale, `/dev/${route}/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}`),
      );
      const row = page.getByRole('row').filter({ hasText: subject }).first();
      const trigger = row.locator('button[aria-haspopup="dialog"]');
      const dialog = page.getByRole('dialog');
      await expect(dialog).toHaveCount(0);
      await trigger.focus();
      await page.keyboard.press('Enter');
      await expect(dialog).toContainText(fact);
      await page.keyboard.press('Escape');
      await expect(dialog).toHaveCount(0);
      await expect(trigger).toBeFocused();
    });
  }
}
