import { expect, test, type Locator, type Page } from '@playwright/test';
import { expectNoA11yViolations } from '../a11y/axe-scan';
import {
  FIXTURE_REQUEST_LOG,
  type FixtureCallRecord,
} from '../../src/app/core/dev-fixtures/fixture-request-log';
import {
  localePath,
  LOCALES,
  ORG_ACME,
  PROJECT_STOREFRONT,
  seedStoredTheme,
  THEMES,
  withVariant,
} from './support';

const base = `/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}/pending-changes`;
const HOSTILE = 'fixture.variant.listAuthoringChanges=hostile';
const INJECTION = '<img src=x onerror="globalThis.pwned=1"> <script>globalThis.pwned=1</script>';
const LONG_TOKEN = 'A'.repeat(140);
/** The `ok` corpus merge and split rows: the store selects every row on load but blocks apply while
 * a structural row stays selected, so the desktop flow deselects them first. */
const STRUCTURAL = ['018f0000-0000-7000-8000-00000000cd03', '018f0000-0000-7000-8000-00000000cd04'];

async function calls(page: Page): Promise<FixtureCallRecord[]> {
  return page.evaluate(
    (key) =>
      (window[key] ?? []).filter((record) =>
        new URL(record.url, location.origin).pathname.endsWith('/authoring/changes:apply'),
      ),
    FIXTURE_REQUEST_LOG,
  );
}

async function keyboardTo(page: Page, target: Locator): Promise<void> {
  for (let step = 0; step < 160; step++) {
    if (await target.evaluate((element) => element === document.activeElement)) return;
    await page.keyboard.press('Tab');
  }
  await expect(target).toBeFocused();
}

/** Leave only the applyable rows selected, as the desktop consent flow requires. */
async function deselectStructural(page: Page): Promise<void> {
  for (const id of STRUCTURAL) {
    const box = page.getByRole('checkbox', { name: new RegExp(id) });
    await keyboardTo(page, box);
    await page.keyboard.press('Space');
    await expect(box).not.toBeChecked();
  }
  await expect(page.locator('[data-pending-apply]')).toBeEnabled();
}

/** True while focus stays inside the open modal, i.e. the trap never let Tab escape. */
async function focusInsideDialog(page: Page): Promise<boolean> {
  return page.evaluate(() => {
    const dialog = document.querySelector('[role="alertdialog"], [role="dialog"]');
    return dialog !== null && dialog.contains(document.activeElement);
  });
}

async function openHostilePreview(page: Page): Promise<void> {
  await expect(page.locator('[data-pending-preview]')).toHaveCount(4);
  const preview = page.locator('[data-pending-preview]').first();
  await keyboardTo(page, preview);
  await page.keyboard.press('Enter');
  await expect(page.locator('[data-pending-preview-content]')).toContainText(INJECTION);
}

test.describe('pending changes hostile text renders inert as text', () => {
  test('the queue and preview show markup as text and inject no element', async ({ page }) => {
    const errors: Error[] = [];
    page.on('pageerror', (error) => errors.push(error));
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto(withVariant(localePath('en', base), HOSTILE));
    await expect(page.locator('#main-content h1')).toBeVisible();
    await openHostilePreview(page);
    const content = page.locator('[data-pending-preview-content]');
    await expect(content).toContainText('\u202eevil\u202c');
    await expect(content).toContainText(LONG_TOKEN);
    await expect(page.locator('#main-content img')).toHaveCount(0);
    await expect(page.locator('#main-content script')).toHaveCount(0);
    await expect(page.locator('#main-content [onerror]')).toHaveCount(0);
    expect(await page.evaluate(() => Object.hasOwn(globalThis, 'pwned'))).toBe(false);
    await expect(page.getByRole('alertdialog')).toHaveCount(0);
    expect(await calls(page)).toEqual([]);
    expect(errors).toEqual([]);
    await expectNoA11yViolations(page, 'pending hostile en light 1280');
  });
});

test.describe('pending consent dialog keeps keyboard focus inside', () => {
  test.use({ viewport: { width: 1280, height: 900 } });

  test('Tab wraps inside, Escape closes and restores the Apply opener', async ({ page }) => {
    await seedStoredTheme(page, 'light');
    await page.goto(localePath('en', base));
    await expect(page.locator('[data-pending-preview]')).toHaveCount(4);
    await deselectStructural(page);
    const apply = page.locator('[data-pending-apply]');
    await keyboardTo(page, apply);
    await page.keyboard.press('Enter');
    await expect(page.locator('[data-pending-cancel]')).toBeFocused();
    const dialog = page.getByRole('alertdialog');
    await expect(dialog).toHaveAttribute('aria-modal', 'true');
    for (let step = 0; step < 8; step++) {
      await page.keyboard.press('Tab');
      expect(await focusInsideDialog(page), `Tab ${step} left the dialog`).toBe(true);
    }
    for (let step = 0; step < 8; step++) {
      await page.keyboard.press('Shift+Tab');
      expect(await focusInsideDialog(page), `Shift+Tab ${step} left`).toBe(true);
    }
    await page.keyboard.press('Escape');
    await expect(dialog).toHaveCount(0);
    await expect(apply).toBeFocused();
    expect(await calls(page)).toEqual([]);
  });

  test('a successful apply that empties the selection moves focus to the preview region', async ({
    page,
  }) => {
    await page.goto(localePath('en', base));
    await expect(page.locator('[data-pending-preview]')).toHaveCount(4);
    await deselectStructural(page);
    const apply = page.locator('[data-pending-apply]');
    await keyboardTo(page, apply);
    await page.keyboard.press('Enter');
    await expect(page.locator('[data-pending-cancel]')).toBeFocused();
    const dialog = page.getByRole('alertdialog');
    // Only the comment and delete rows are selectable; the structural pair stays queued.
    await page.keyboard.press('Tab');
    await page.keyboard.press('Enter');
    await expect(dialog).toHaveCount(0);
    await expect(page.locator('[data-pending-receipt]')).toBeVisible();
    // Every applied row leaves the selection, so the opener is disabled and must not hold focus.
    await expect(apply).toBeDisabled();
    await expect(page.locator('[data-pending-preview-content]')).toBeFocused();
    expect(await calls(page)).toHaveLength(1);
  });

  test('discard that removes its opener moves focus to the preview region', async ({ page }) => {
    await page.goto(localePath('en', base));
    await page.locator('[data-pending-preview]').first().click();
    const discard = page.locator('[data-pending-discard]');
    await keyboardTo(page, discard);
    await page.keyboard.press('Enter');
    await expect(page.locator('[data-pending-cancel]')).toBeFocused();
    const dialog = page.getByRole('alertdialog');
    await page.keyboard.press('Tab');
    await page.keyboard.press('Enter');
    await expect(dialog).toHaveCount(0);
    await expect(page.locator('[data-pending-preview-content]')).toBeFocused();
  });
});

test.describe('pending changes stay readable with hostile vi strings at 360px', () => {
  for (const locale of LOCALES) {
    for (const theme of THEMES) {
      test(`the hostile queue never overflows (${locale}, ${theme}, 360)`, async ({ page }) => {
        await page.setViewportSize({ width: 360, height: 900 });
        await seedStoredTheme(page, theme);
        await page.goto(withVariant(localePath(locale, base), HOSTILE));
        await expect(page.locator('#main-content h1')).toBeVisible();
        await openHostilePreview(page);
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(
          true,
        );
        expect(
          await page
            .locator('[data-pending-preview-content]')
            .evaluate((element) => element.scrollWidth <= element.clientWidth + 1),
        ).toBe(true);
      });
    }
  }
});

test.describe('the hostile pending queue is WCAG 2.2 AA clean', () => {
  for (const locale of LOCALES) {
    for (const theme of THEMES) {
      for (const width of [360, 1280]) {
        test(`axe clean (${locale}, ${theme}, ${width})`, async ({ page }) => {
          await page.setViewportSize({ width, height: 900 });
          await seedStoredTheme(page, theme);
          await page.goto(withVariant(localePath(locale, base), HOSTILE));
          await expect(page.locator('#main-content h1')).toBeVisible();
          await openHostilePreview(page);
          await expectNoA11yViolations(page, `pending hostile ${locale} ${theme} ${width}`);
        });
      }
    }
  }
});
