import { chromium, expect, test } from '@playwright/test';
import type { Page } from '@playwright/test';
import {
  LIMITED_GRANTS,
  LOCALES,
  fixturesBaseUrl,
  USAGE_READ_GRANTS,
  localePath,
  ORG_ACME,
  overviewPath,
  withVariant,
} from './support';

// Shell geometry stays consistent across translated bundles and permission-filtered navigation.
for (const locale of LOCALES) {
  for (const [width, colorScheme] of [
    [768, 'light'],
    [900, 'light'],
    [900, 'dark'],
    [1024, 'light'],
    [1279, 'light'],
    [1280, 'light'],
    [1440, 'light'],
    [1440, 'dark'],
  ] as const) {
    test(`toolbar fits at ${width}px in ${locale} (${colorScheme})`, async ({ page }) => {
      await page.emulateMedia({ colorScheme });
      await page.setViewportSize({ width, height: 900 });
      await page.goto(withVariant(overviewPath(locale), LIMITED_GRANTS));
      const header = page.locator('header.topbar');
      const breadcrumb = page.locator('main > app-shell-breadcrumbs nav');
      const scopes = header.getByRole('combobox');
      await expect(breadcrumb).toBeVisible();
      await expect(header.locator('app-shell-breadcrumbs')).toHaveCount(0);
      await expect(scopes).toHaveCount(2);
      for (const scope of await scopes.all()) {
        const selected = (await scope.innerText()).trim();
        expect(selected).not.toBe('');
        await expect(header.getByText(selected, { exact: true })).toHaveCount(1);
        await expect(breadcrumb.getByText(selected, { exact: true })).toHaveCount(1);
      }
      await expect(header.locator('.theme-toggle')).toHaveAttribute(
        'aria-pressed',
        String(colorScheme === 'dark'),
      );
      await expect(header.locator('.nav-toggle')).toHaveAttribute(
        'aria-expanded',
        String(width >= 1280),
      );
      await expect(page.locator('#shell-navigation')).toHaveCSS(
        'width',
        width >= 1280 ? '224px' : '68px',
      );
      if (width >= 1280) await expectSidebarLabelsToFit(page);
      await page.evaluate(() => document.fonts.ready.then(() => undefined));
      const controls = [
        header.locator('.nav-toggle'),
        scopes.nth(0),
        scopes.nth(1),
        header.locator('.screen-search'),
        header.locator('.theme-toggle'),
        header.locator('app-shell-account button'),
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
      const rows = new Set(boxes.map((box) => Math.round(box.y)));
      expect(rows.size).toBeLessThanOrEqual(width < 1024 ? 2 : 1);
      for (const [index, box] of boxes.entries()) {
        expect(box.x).toBeGreaterThanOrEqual(headerBox.x);
        expect(box.x + box.width).toBeLessThanOrEqual(headerBox.x + headerBox.width);
        if (index > 0 && Math.abs(box.y - boxes[index - 1]!.y) <= 1) {
          expect(box.x).toBeGreaterThan(boxes[index - 1]!.x);
        }
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
    test(`Settings rail label fits the expanded rail at ${width}px in ${locale}`, async ({
      page,
    }) => {
      await page.setViewportSize({ width, height: 1000 });
      const variant = '?fixture.variant.listPermissions=settings-secrets-write';
      await page.goto(
        withVariant(localePath(locale, `/dev/settings-secrets/orgs/${ORG_ACME}`), variant),
      );
      await expectSettingsRailLabel(page, locale, false);
      // Active navigation uses a heavier weight; it must fit the same budget without smaller type.
      await page.goto(
        withVariant(localePath(locale, `/orgs/${ORG_ACME}/usage`), USAGE_READ_GRANTS),
      );
      await expect
        .poll(() => new URL(page.url()).pathname)
        .toBe(localePath(locale, `/orgs/${ORG_ACME}/usage`));
      await expect(page.locator('app-quota-panel')).toBeVisible();
      await expectSettingsRailLabel(page, locale, true);
    });
  }
}

async function expectSettingsRailLabel(
  page: Page,
  locale: (typeof LOCALES)[number],
  active: boolean,
) {
  // The rail carries one bottom Settings row, lit on every administration URL including /usage.
  const link = page.locator('app-shell-nav a').last();
  const fullName = locale === 'vi' ? 'Cài đặt' : 'Settings';
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
    const peer = Array.from(element.closest('nav')!.querySelectorAll('a'))
      .find((other) => other !== element)!
      .querySelector('span')!;
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
    // The expanded rail shows no tooltip unless the label is shortened; "Cài đặt" fits in full.
    await link.focus();
    await expect(link).toBeFocused();
    await expect(link).toHaveAccessibleName(fullName);
    await expect(page.getByRole('tooltip')).toHaveCount(0);
  }
}

test('centers the 1600px workspace between the expanded sidebar and the window edge', async ({
  page,
}) => {
  await page.setViewportSize({ width: 2048, height: 1000 });
  await page.goto(withVariant(overviewPath('en'), LIMITED_GRANTS));
  const main = page.getByRole('main');
  await expect(main).toHaveCSS('width', '1600px');
  const margins = await main.evaluate((element) => {
    const outer = element.parentElement!.getBoundingClientRect();
    const inner = element.getBoundingClientRect();
    return [inner.left - outer.left, outer.right - inner.right];
  });
  expect(margins[0]).toBeGreaterThan(0);
  expect(margins[0]).toBeCloseTo(margins[1]!, 0);
});

test('rail navigation exposes the focused destination and keeps focus on Escape', async ({
  page,
}) => {
  await page.setViewportSize({ width: 768, height: 900 });
  await page.goto(withVariant(overviewPath('en'), LIMITED_GRANTS));
  const link = page.locator('app-shell-nav a[aria-current="page"]');
  await expect(link).toHaveAccessibleName(/\S/);
  const marker = await link.evaluate(
    (element) => getComputedStyle(element, '::before').borderInlineStartWidth,
  );
  expect(marker).toBe('3px');
  const name = await link.getAttribute('aria-label');
  if (!name) throw new Error('The current rail destination has no name');
  await link.focus();
  const tooltip = page.getByRole('tooltip');
  await expect(tooltip).toHaveText(name);
  await page.keyboard.press('Escape');
  await expect(tooltip).toHaveCount(0);
  await expect(link).toBeFocused();
});

test('mobile search and account overlays return focus without changing the route', async ({
  page,
}) => {
  await page.setViewportSize({ width: 390, height: 900 });
  await page.goto(withVariant(overviewPath('en'), LIMITED_GRANTS));
  const start = page.url();
  const topbar = page.locator('header.topbar');
  const search = topbar.getByRole('button', { name: /^Command palette \((?:⌘K|Ctrl K)\)$/ });
  await search.press('Enter');
  const picker = page.getByRole('dialog', { name: 'Command palette', exact: true });
  await expect(picker.getByRole('combobox')).toBeFocused();
  await page.keyboard.press('Escape');
  await expect(picker).toHaveCount(0);
  await expect(search).toBeFocused();
  const account = topbar.getByRole('button', { name: 'Account and preferences' });
  await account.press('Enter');
  const language = page.getByRole('group', { name: 'Language' });
  await expect(language.getByRole('menuitemradio', { name: 'EN' })).toBeFocused();
  await page.keyboard.press('ArrowDown');
  await expect(language.getByRole('menuitemradio', { name: 'VI' })).toBeFocused();
  const theme = page.getByRole('group', { name: 'Theme' });
  await expect(theme.getByRole('menuitemradio', { name: 'System' })).toHaveAttribute(
    'aria-checked',
    'true',
  );
  await page.keyboard.press('ArrowDown');
  await expect(theme.getByRole('menuitemradio', { name: 'Light' })).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(theme.getByRole('menuitemradio', { name: 'Light' })).toHaveAttribute(
    'aria-checked',
    'true',
  );
  await expect(language.getByRole('menuitemradio', { name: 'EN' })).toHaveAttribute(
    'aria-checked',
    'true',
  );
  await expect(page.getByRole('menuitem', { name: 'Sign out of this tab' })).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(page.getByRole('menu')).toHaveCount(0);
  await expect(account).toBeFocused();
  await expect(page).toHaveURL(start);
});

async function expectSidebarLabelsToFit(page: Page) {
  const links = page.locator('app-shell-nav a');
  await expect(links.first()).toBeVisible();
  await page.evaluate(() => document.fonts.ready.then(() => undefined));
  const clipped = await links.evaluateAll((items) =>
    items.flatMap((link) => {
      const label = link.querySelector(':scope > span');
      if (!label?.textContent?.trim()) return [link.getAttribute('aria-label')];
      const range = document.createRange();
      range.selectNodeContents(label);
      const bounds = link.getBoundingClientRect();
      const lines = Array.from(range.getClientRects());
      const fits =
        lines.length > 0 &&
        lines.every(
          (line) =>
            line.left >= bounds.left - 1 &&
            line.right <= bounds.right + 1 &&
            line.top >= bounds.top - 1 &&
            line.bottom <= bounds.bottom + 1,
        );
      return fits && label.scrollWidth <= label.clientWidth + 1 ? [] : [label.textContent];
    }),
  );
  expect(clipped).toEqual([]);
}

test('expanded navigation reflows at 125% and 150% browser zoom on a 1920px display', async () => {
  // Zoom is a profile preference. A disposable regular profile isolates it from other suites.
  const context = await chromium.launchPersistentContext('', {
    channel: 'chrome',
    baseURL: fixturesBaseUrl(),
    viewport: null,
  });
  try {
    const page = await context.newPage();
    const cdp = await context.newCDPSession(page);
    const { windowId } = await cdp.send('Browser.getWindowForTarget');
    await cdp.send('Browser.setContentsSize', { windowId, width: 1920, height: 1080 });
    await expect.poll(() => page.evaluate(() => innerWidth)).toBe(1920);
    const settings = await context.newPage();
    await settings.goto('chrome://settings/appearance');
    for (const zoom of [1.25, 1.5]) {
      await settings.locator('#zoomLevel').selectOption({ label: `${zoom * 100}%` });
      for (const locale of LOCALES) {
        await page.goto(overviewPath(locale));
        await expect.poll(() => page.evaluate(() => innerWidth)).toBe(1920 / zoom);
        await expect(page.locator('#shell-navigation')).toHaveCSS('width', '224px');
        await expectSidebarLabelsToFit(page);
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(
          true,
        );
      }
    }
  } finally {
    await context.close();
  }
});
