import { expect, test, type Page } from '@playwright/test';

import { expectNoA11yViolations } from '../a11y/axe-scan';
import { openRunFilters } from './run-list.support';
import {
  LOCALES,
  ORG_ACME,
  PROJECT_STOREFRONT,
  THEMES,
  localePath,
  seedStoredTheme,
  waitForDialogAnimations,
  withVariants,
  type ShellLocale,
} from './support';

const PROJECT = `/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}`;
const AUTHORING = `${PROJECT}/authoring`;
const RUNS_VARIANT = '?fixture.variant.listRuns=runs';
const RUN_READ_GRANT = '?fixture.variant.listPermissions=settings-policy-readonly';
const RUN_ID = '018f0000-0000-7000-8000-000000000071';

function trigger(page: Page) {
  return page.locator('app-shell-search').getByRole('button');
}

function palette(page: Page) {
  const dialog = page.getByRole('dialog');
  return {
    dialog,
    field: dialog.getByRole('combobox'),
    options: dialog.getByRole('option'),
    status: dialog.getByRole('status'),
  };
}

async function visit(page: Page, locale: ShellLocale = 'en') {
  await page.goto(localePath(locale, AUTHORING));
  await expect(trigger(page)).toBeVisible();
  await expect(page.locator('#main-content')).toBeVisible();
}

async function openFromTrigger(page: Page) {
  await trigger(page).click();
  const picker = palette(page);
  await expect(picker.field).toBeFocused();
  return picker;
}

test('Ctrl+K opens from the page; the trigger advertises the shortcut and regains focus after Escape', async ({
  page,
}) => {
  await visit(page);
  const open = trigger(page);
  const isApple = await page.evaluate(() => /Mac|iPhone|iPad|iPod/.test(navigator.userAgent));
  const hint = isApple ? '⌘K' : 'Ctrl K';
  await expect(open).toHaveAccessibleName(`Command palette (${hint})`);
  await expect(open).toHaveAttribute('aria-keyshortcuts', 'Control+K Meta+K');

  await page.locator('#main-content').focus();
  await page.keyboard.press('Control+k');
  const { dialog, field } = palette(page);
  await expect(dialog).toBeVisible();
  await expect(dialog).toHaveAccessibleName('Command palette');
  await expect(field).toHaveAccessibleName('Find a command');
  await expect(field).toBeFocused();
  await field.press('Escape');
  await expect(dialog).toHaveCount(0);

  await openFromTrigger(page);
  await field.press('Escape');
  await expect(dialog).toHaveCount(0);
  await expect(open).toBeFocused();
});

test('Ctrl+K does not open a palette or consume typing in a page input', async ({ page }) => {
  await page.goto(
    withVariants(localePath('en', `/dev/run-list${PROJECT}`), [RUNS_VARIANT, RUN_READ_GRANT]),
  );
  await openRunFilters(page);
  const ref = page.locator('#run-ref');
  await ref.fill('feature/');
  await ref.press('End');
  await ref.press('Control+k');
  await page.keyboard.type('checkout');
  await expect(ref).toHaveValue('feature/checkout');
  await expect(ref).toBeFocused();
  await expect(palette(page).dialog).toHaveCount(0);
});

test('the live count follows the filter, and arrow keys select the destination that Enter opens', async ({
  page,
}) => {
  await visit(page);
  const { dialog, field, options, status } = await openFromTrigger(page);
  await expect(status).toHaveAttribute('aria-live', 'polite');
  await field.fill('no command matches this query');
  await expect(status).toHaveText('No results');
  await expect(options).toHaveCount(0);

  await field.fill('feature tree');
  await expect(status).toHaveText('1 result');
  await expect(options).toHaveText(['Feature tree']);

  await field.fill('navigate');
  await expect(status).toHaveText(/^\s*\d+ results\s*$/);
  const labels = (await options.allTextContents()).map((text) => text.trim());
  expect(labels).toContain('Feature tree');
  expect(labels).toContain('Pending changes');
  const pendingIndex = labels.indexOf('Pending changes');

  const selected = dialog.getByRole('option', { selected: true });
  for (let step = 0; step <= pendingIndex; step += 1) {
    await field.press('ArrowDown');
  }
  await expect(selected).toHaveText('Pending changes');
  await field.press('ArrowDown');
  const next = labels[pendingIndex + 1];
  expect(next).toBeDefined();
  await expect(selected).toHaveText(next ?? '');
  await field.press('ArrowUp');
  await expect(selected).toHaveText('Pending changes');
  await expect(field).toHaveAttribute(
    'aria-activedescendant',
    (await selected.getAttribute('id'))!,
  );

  await field.press('Enter');
  await expect(page).toHaveURL(
    (url) => url.pathname === localePath('en', `${PROJECT}/pending-changes`),
  );
  await expect(dialog).toHaveCount(0);
});

test('Vietnamese destination labels match a typed unaccented query', async ({ page }) => {
  await visit(page, 'vi');
  const { field, options } = await openFromTrigger(page);
  await field.pressSequentially('cay chuc nang');
  await expect(options.filter({ hasText: 'Cây chức năng' })).toHaveCount(1);
});

test('pasting a run UUID offers Open run and Enter opens that scoped run URL', async ({
  context,
  page,
}) => {
  await context.grantPermissions(['clipboard-read', 'clipboard-write']);
  await visit(page);
  const { dialog, field, options } = await openFromTrigger(page);
  await page.evaluate((id) => navigator.clipboard.writeText(id), RUN_ID);
  await field.press('ControlOrMeta+v');
  await expect(field).toHaveValue(RUN_ID);
  await expect(options).toHaveText([`Open run ${RUN_ID}`]);
  await field.press('Enter');
  await expect(page).toHaveURL(
    (url) => url.pathname === localePath('en', `${PROJECT}/runs/${RUN_ID}`),
  );
  await expect(dialog).toHaveCount(0);
});

test('theme and locale actions change the page without losing the project route', async ({
  page,
}) => {
  await visit(page);
  const { dialog, field, options } = await openFromTrigger(page);
  await field.fill('switch to dark theme');
  await dialog.getByRole('option', { name: 'Switch to dark theme', exact: true }).click();
  await expect(dialog).toHaveCount(0);
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');

  await openFromTrigger(page);
  await field.fill('switch to Vietnamese');
  await expect(options).toHaveText(['Switch to Vietnamese']);
  await field.press('Enter');
  await expect(page).toHaveURL((url) => url.pathname.startsWith('/vi/'));
  await expect(page.locator('html')).toHaveAttribute('lang', 'vi');
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
});

for (const locale of LOCALES) {
  for (const theme of THEMES) {
    test(`the open command palette passes axe (${locale}, ${theme})`, async ({ page }) => {
      await seedStoredTheme(page, theme);
      await visit(page, locale);
      await expect(page.locator('html')).toHaveAttribute('data-theme', theme);
      const { options } = await openFromTrigger(page);
      await expect(options.first()).toBeVisible();
      await waitForDialogAnimations(page, '[role="dialog"]');
      await expectNoA11yViolations(page, `open command palette (${locale}, ${theme})`);
    });
  }
}

test('at 360px every option has a 44px hit row and the page has no horizontal overflow', async ({
  page,
}) => {
  await page.setViewportSize({ width: 360, height: 800 });
  await visit(page, 'vi');
  const { dialog, options } = await openFromTrigger(page);
  await expect(dialog.getByRole('option', { name: 'Cây chức năng', exact: true })).toBeVisible();
  await waitForDialogAnimations(page, '[role="dialog"]');
  const heights = await options.evaluateAll((rows) =>
    rows.map((row) => row.getBoundingClientRect().height),
  );
  expect(heights.length).toBeGreaterThan(1);
  for (const height of heights) expect(height).toBeGreaterThanOrEqual(44);
  const overflow = await page.evaluate(
    () => document.documentElement.scrollWidth - document.documentElement.clientWidth,
  );
  expect(overflow).toBeLessThanOrEqual(0);
});
