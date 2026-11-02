import { randomUUID } from 'node:crypto';
import { expect, test, type Page } from '@playwright/test';

const copy = {
  en: {
    heading: 'Dev sign-in',
    secret: 'Dev-login secret',
    wrong: 'The dev-login secret is incorrect. Try again.',
    unavailable: 'Dev sign-in is not enabled.',
  },
  vi: {
    heading: 'Đăng nhập phát triển',
    secret: 'Khóa bí mật đăng nhập phát triển',
    wrong: 'Khóa bí mật đăng nhập phát triển không đúng. Hãy thử lại.',
    unavailable: 'Đăng nhập phát triển chưa được bật.',
  },
};

async function api(page: Page, loginStatus: number) {
  const value = randomUUID();
  const probes = { organizations: 0, permissions: 0 };
  let authenticated = false;
  await page.route('**/v1/orgs', async (route) => {
    probes.organizations += 1;
    await route.fulfill({
      status: authenticated ? 200 : 401,
      json: authenticated
        ? { items: [] }
        : {
            error: { code: 'token_invalid', message: 'Unauthenticated', request_id: randomUUID() },
          },
    });
  });
  await page.route('**/v1/permissions**', async (route) => {
    probes.permissions += 1;
    await route.fulfill({
      status: authenticated ? 200 : 401,
      json: authenticated
        ? { items: [], next_cursor: null }
        : {
            error: { code: 'token_invalid', message: 'Unauthenticated', request_id: randomUUID() },
          },
    });
  });
  await page.route('**/v1/dev/session', async (route) => {
    expect(route.request().method()).toBe('POST');
    expect(route.request().postDataJSON()).toEqual({ secret: value });
    authenticated = loginStatus === 204;
    await route.fulfill({ status: loginStatus, body: '' });
  });
  return { value, probes };
}

for (const locale of ['en', 'vi'] as const) {
  for (const theme of ['light', 'dark'] as const) {
    test(`${locale} ${theme}: keyboard sign-in re-probes the real session seam`, async ({
      page,
    }) => {
      await page.setViewportSize({ width: 360, height: 800 });
      await page.emulateMedia({ colorScheme: theme });
      const stub = await api(page, 204);
      await page.goto(`/${locale}/login?returnUrl=${encodeURIComponent('/dev/ui-gallery')}`);
      const disclosure = page.locator('summary', { hasText: copy[locale].heading });
      await disclosure.focus();
      await page.keyboard.press('Enter');
      const input = page.getByLabel(copy[locale].secret, { exact: true });
      await expect(input).toBeVisible();
      await expect(input).toHaveAttribute('type', 'password');
      await input.fill(stub.value);
      await input.press('Enter');
      await expect(page).toHaveURL(new RegExp(`/${locale}/dev/ui-gallery$`));
      expect(stub.probes.organizations).toBeGreaterThanOrEqual(2);
      expect(stub.probes.permissions).toBeGreaterThanOrEqual(2);
      expect(await page.evaluate(() => Object.values(localStorage).join(''))).not.toContain(
        stub.value,
      );
      expect(await page.evaluate(() => Object.values(sessionStorage).join(''))).not.toContain(
        stub.value,
      );
    });
  }

  for (const status of [401, 404, 503]) {
    test(`${locale}: status ${status} is announced inline without leaving login`, async ({
      page,
    }) => {
      const stub = await api(page, status);
      await page.goto(`/${locale}/login`);
      await page.locator('summary', { hasText: copy[locale].heading }).click();
      const input = page.getByLabel(copy[locale].secret, { exact: true });
      await input.fill(stub.value);
      await input.press('Enter');
      await expect(page.getByRole('alert')).toContainText(
        status === 401 ? copy[locale].wrong : copy[locale].unavailable,
      );
      await expect(input).toHaveValue('');
      await expect(page).toHaveURL(new RegExp(`/${locale}/login$`));
      await expect(
        page.getByRole('button', { name: copy[locale].heading, exact: true }),
      ).toBeEnabled();
      expect(stub.probes.organizations).toBe(1);
      expect(stub.probes.permissions).toBe(1);
    });
  }
}
