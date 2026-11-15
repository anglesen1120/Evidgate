import { expect, test } from '@playwright/test';
import type { Page } from '@playwright/test';
import {
  LIMITED_GRANTS,
  LOCALES,
  localePath,
  ORG_ACME,
  overviewPath,
  withVariant,
} from './support';

// LYT-11/12: real shell geometry, with translated bundles and the normal navigation permissions.
for (const locale of LOCALES) {
  for (const width of [768, 1024]) {
    test(`toolbar utilities share one row at ${width}px in ${locale}`, async ({ page }) => {
      await page.setViewportSize({ width, height: 900 });
      await page.goto(withVariant(overviewPath(locale), LIMITED_GRANTS));
      const header = page.locator('header.topbar');
      const scopes = header.getByRole('combobox');
      await expect(scopes).toHaveCount(2);
      await page.evaluate(() => document.fonts.ready.then(() => undefined));
      const controls = [
        header.locator('.nav-toggle'),
        scopes.nth(0),
        scopes.nth(1),
        header.locator('.locale-toggle'),
        header.locator('.theme-toggle'),
      ];
      const boxes = [];
      for (const control of controls) {
        await expect(control).toBeVisible();
        const box = await control.boundingBox();
        if (box === null) throw new Error('A toolbar control has no rendered box');
        expect(box.width).toBeGreaterThanOrEqual(44);
        expect(box.height).toBeGreaterThanOrEqual(44);
        boxes.push(box);
      }
      const headerBox = await header.boundingBox();
      if (headerBox === null) throw new Error('The toolbar has no rendered box');
      expect(headerBox.height).toBeCloseTo(64, 0);
      for (const [index, box] of boxes.entries()) {
        expect(Math.abs(box.y - boxes[0]!.y)).toBeLessThanOrEqual(1);
        expect(box.x).toBeGreaterThanOrEqual(headerBox.x);
        expect(box.x + box.width).toBeLessThanOrEqual(headerBox.x + headerBox.width);
        if (index > 0) expect(box.x).toBeGreaterThan(boxes[index - 1]!.x);
      }
      const scopeBox = await header.locator('app-scope-switcher').boundingBox();
      if (scopeBox === null) throw new Error('The loaded scope pair has no rendered box');
      expect(scopeBox.width).toBeLessThanOrEqual(480);
      if (width === 768) expect(scopeBox.width).toBeLessThan(480);
      expect(
        await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth),
      ).toBe(true);
    });
  }

  for (const width of [1280, 1440]) {
    test(`usage label fits the expanded rail at ${width}px in ${locale}`, async ({ page }) => {
      await page.setViewportSize({ width, height: 1000 });
      const variant = '?fixture.variant.listPermissions=settings-secrets-write';
      await page.goto(
        withVariant(localePath(locale, `/dev/settings-secrets/orgs/${ORG_ACME}`), variant),
      );
      await expectUsageLabel(page, locale, false);
      // Active navigation uses a heavier weight; it must fit the same budget without smaller type.
      await page.goto(withVariant(localePath(locale, `/orgs/${ORG_ACME}/usage`), variant));
      await expectUsageLabel(page, locale, true);
    });
  }
}

async function expectUsageLabel(page: Page, locale: (typeof LOCALES)[number], active: boolean) {
  const link = page.locator('app-shell-nav a[href$="/usage"]');
  const fullName = locale === 'vi' ? 'Mức sử dụng và tín dụng AI' : 'Usage & credits';
  await expect(link).toBeVisible();
  await expect(link).toHaveAccessibleName(fullName);
  if (active) await expect(link).toHaveAttribute('aria-current', 'page');
  else await expect(link).not.toHaveAttribute('aria-current', 'page');
  await page.evaluate(() => document.fonts.ready.then(() => undefined));
  const geometry = await link.evaluate((element) => {
    const label = element.querySelector('span')!;
    const icon = element.querySelector('app-shell-icon')!;
    const style = getComputedStyle(element);
    const labelStyle = getComputedStyle(label);
    const peer = element.closest('nav')!.querySelector('a:not([href$="/usage"]) span')!;
    const gap = Number.parseFloat(style.columnGap);
    const available =
      element.clientWidth -
      Number.parseFloat(style.paddingLeft) -
      Number.parseFloat(style.paddingRight) -
      icon.getBoundingClientRect().width -
      gap;
    return {
      railWidth: document.getElementById('shell-navigation')!.getBoundingClientRect().width,
      labelHeight: label.getBoundingClientRect().height,
      lineHeight: Number.parseFloat(labelStyle.lineHeight),
      margin: available - label.getBoundingClientRect().width,
      requiredMargin: gap,
      overflows: label.scrollWidth > label.clientWidth + 1,
      textOverflow: labelStyle.textOverflow,
      fontSize: labelStyle.fontSize,
      peerFontSize: getComputedStyle(peer).fontSize,
    };
  });
  expect(geometry.railWidth).toBe(224);
  expect(geometry.labelHeight).toBeLessThanOrEqual(geometry.lineHeight + 1);
  expect(geometry.margin).toBeGreaterThanOrEqual(geometry.requiredMargin);
  expect(geometry.overflows).toBe(false);
  expect(geometry.textOverflow).not.toBe('ellipsis');
  expect(geometry.fontSize).toBe(geometry.peerFontSize);
  if (locale === 'vi') {
    await link.focus();
    await expect(page.getByRole('tooltip')).toHaveText(fullName);
    await page.keyboard.press('Escape');
    await expect(page.getByRole('tooltip')).toHaveCount(0);
    await expect(link).toBeFocused();
  }
}
