import { expect, test, type Locator, type Page } from '@playwright/test';
import { LOCALES, ORG_ACME, PROJECT_STOREFRONT, localePath } from './support';

const screens = [
  {
    route: 'agent-pools',
    subject: 'browser-ci-east',
    fact: 'pool_01HX6D3K9M',
    param: 'pool',
    linked: 'mobile-lab',
  },
  {
    route: 'webhooks',
    subject: 'wh-19',
    fact: 'https://ci.example.test/hooks/evidgate',
    param: 'endpoint',
    linked: 'wh-20',
  },
  {
    route: 'support-access',
    subject: 'sg-41',
    fact: 'req-sg-41',
    param: 'object',
    linked: 'sg-40',
  },
] as const;

async function expectViewOpen(dialog: Locator, fact: string) {
  await expect(dialog).toContainText(fact);
  await expect(dialog).toHaveAccessibleName(/\S/);
  await expect(dialog.getByRole('heading').first()).toBeFocused();
}

async function closeToRow(page: Page, dialog: Locator, trigger: Locator) {
  await page.keyboard.press('Tab');
  await expect(dialog.locator(':focus')).toHaveCount(1);
  await page.keyboard.press('Escape');
  await expect(dialog).toHaveCount(0);
  await expect(trigger).toBeFocused();
}

for (const { route, subject, fact, param, linked } of screens) {
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
      await expectViewOpen(dialog, fact);
      await expect(dialog.locator('hlm-dialog-content')).toHaveCSS('width', '760px');
      await closeToRow(page, dialog, trigger);
    });

    test(`${route}: closing a deep link focuses the matching row at 360px (${locale})`, async ({
      page,
    }) => {
      await page.setViewportSize({ width: 360, height: 800 });
      await page.goto(
        localePath(
          locale,
          `/dev/${route}/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}?${param}=${linked}`,
        ),
      );
      const dialog = page.getByRole('dialog');
      await expectViewOpen(dialog, linked);
      const content = dialog.locator('hlm-dialog-content');
      await expect(content).toHaveCSS('width', '360px');
      await expect(content).toHaveCSS('height', '800px');
      await expect.poll(async () => (await content.boundingBox())?.x).toBeCloseTo(0);
      // The modal removes background rows from the accessibility tree until it closes.
      const trigger = page
        .getByRole('row')
        .filter({ hasText: linked })
        .locator('[data-view-record]');
      await closeToRow(page, dialog, trigger);
    });
  }
}

test('support approval uses the large confirmation dialog', async ({ page }) => {
  await page.goto(
    localePath(
      'en',
      `/dev/support-access/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}?section=admin-preview`,
    ),
  );
  const trigger = page.locator('[data-grant-row="sg-41"] [data-grant-action]');
  await trigger.click();
  const dialog = page.getByRole('dialog');
  await expect(dialog).toContainText('sg-41');
  await expect(dialog.locator('hlm-dialog-content')).toHaveCSS('width', '760px');
  await expect(dialog.locator('#grant-cancel')).toBeFocused();
  await page.keyboard.press('Escape');
  await expect(dialog).toHaveCount(0);
  await expect(trigger).toBeFocused();
});

test('webhook creation uses the large confirmation inside the endpoint view', async ({ page }) => {
  await page.goto(
    localePath(
      'en',
      `/dev/webhooks/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}?endpoint=wh-19`,
    ),
  );
  const view = page.getByRole('dialog');
  await expectViewOpen(view, 'wh-19');
  await view.locator('footer button[aria-expanded]').click();
  await view.getByRole('button', { name: 'Create webhook endpoint' }).click();
  const form = page.getByRole('dialog', { name: 'Create webhook endpoint' });
  await expect(form.locator('hlm-dialog-content')).toHaveCSS('width', '760px');
  await expect(form.locator('[data-preview-cancel]')).toBeFocused();
  await page.keyboard.press('Escape');
  await expect(form).toHaveCount(0);
  await expect(view).toContainText('wh-19');
});
