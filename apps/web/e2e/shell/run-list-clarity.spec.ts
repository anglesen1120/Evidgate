import { expect, test } from '@playwright/test';
import { openRunFilters, RUN_RECORDS } from './run-list.support';
import {
  localePath,
  LOCALES,
  ORG_ACME,
  PROJECT_STOREFRONT,
  seedStoredTheme,
  THEMES,
  withVariants,
} from './support';

const COPY = {
  en: {
    hint: 'Run scope filters this page only. State, decision and ref name filter all project runs.',
    scope: 'Run scope (this page only)',
    state: 'State',
    decision: 'Decision',
    ref: 'Ref name (exact match)',
    order: 'Run (oldest first)',
  },
  vi: {
    hint: 'Phạm vi lượt chạy chỉ lọc trang này. Trạng thái, quyết định và tên tham chiếu lọc tất cả lượt chạy của dự án.',
    scope: 'Phạm vi lượt chạy (chỉ trang này)',
    state: 'Trạng thái',
    decision: 'Quyết định',
    ref: 'Tên tham chiếu (khớp chính xác)',
    order: 'Lượt chạy (cũ nhất trước)',
  },
} as const;

for (const locale of LOCALES) {
  const path = localePath(locale, `/dev/run-list/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}`);
  for (const theme of THEMES) {
    test(`filter scope and fixed creation order stay visible (${locale}, ${theme})`, async ({
      page,
    }) => {
      const copy = COPY[locale];
      await seedStoredTheme(page, theme);
      await page.emulateMedia({ reducedMotion: 'reduce' });
      await page.setViewportSize({ width: 1440, height: 900 });
      await page.goto(
        withVariants(path, [
          '?fixture.variant.listPermissions=settings-policy-readonly',
          '?fixture.variant.listRuns=runs',
        ]),
      );
      const hint = page.getByText(copy.hint, { exact: true });
      for (const width of [360, 390, 900, 1440]) {
        await page.setViewportSize({ width, height: 900 });
        if (width === 360) {
          const all = page.locator('[data-run-quick="all"]');
          const pending = page.locator('[data-run-quick="pending"]');
          const pressedRing = /(?:2px inset|inset [^,]*2px)/;
          await expect(all).toHaveCSS('box-shadow', pressedRing);
          await expect(pending).not.toHaveCSS('box-shadow', pressedRing);
          await pending.click();
          await expect(page).toHaveURL(/decision=pending/);
          await expect(pending).toHaveCSS('box-shadow', pressedRing);
          await expect(all).not.toHaveCSS('box-shadow', pressedRing);
          expect(
            await page.evaluate(() => document.documentElement.scrollWidth),
          ).toBeLessThanOrEqual(width);
          await page.goBack();
          await expect(all).toHaveCSS('box-shadow', pressedRing);
          await expect(pending).not.toHaveCSS('box-shadow', pressedRing);
        }
        await expect(page.locator('#run-scope')).toBeHidden();
        await openRunFilters(page);
        const scopeHint = page.locator('button[aria-controls="run-filter-hint"]');
        await expect(hint).toBeHidden();
        await scopeHint.click();
        await expect(hint).toBeVisible();
        await scopeHint.press('Escape');
        await expect(scopeHint).toBeFocused();
        for (const name of [copy.scope, copy.state, copy.decision]) {
          await expect(page.getByRole('combobox', { name, exact: true })).toBeVisible();
        }
        await expect(page.getByRole('textbox', { name: copy.ref, exact: true })).toBeVisible();
        await expect(page.locator('label[for="run-scope"]')).toHaveText(copy.scope);
        await page.keyboard.press('Escape');
        await expect(page.locator('#run-scope')).toBeHidden();
        // The available width and translated headers determine which presentation fits.
        const heading = page
          .getByRole('columnheader', { name: copy.order, exact: true })
          .or(page.locator(RUN_RECORDS).first().getByText(copy.order, { exact: true }));
        await expect(heading).toBeVisible();
        const overflow = await heading.evaluate((el) => el.scrollWidth - el.clientWidth);
        expect(overflow).toBeLessThanOrEqual(1);
        expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(
          width,
        );
      }
    });

    test(`rerun scope is readable without hovering at 1440px (${locale}, ${theme})`, async ({
      page,
    }) => {
      await seedStoredTheme(page, theme);
      await page.setViewportSize({ width: 1440, height: 900 });
      await page.goto(
        withVariants(path, [
          '?fixture.variant.listPermissions=settings-policy-readonly',
          '?fixture.variant.listRuns=ok',
        ]),
      );
      const label = page.locator('td [data-run-rerun] app-data-table-value');
      await expect(label).toHaveAttribute('title', 'failed_subset');
      const text = label.locator('.value');
      await expect(text).toHaveText('failed_subset');
      await expect(text).toBeVisible();
      await page.evaluate(() => document.fonts.ready);
      const overflow = await text.evaluate((element) => element.scrollWidth - element.clientWidth);
      expect(overflow).toBeLessThanOrEqual(1);
    });
  }
}
