// 09.1.1-T4.4 (plan 09.1.1 §10 T4, §2 AC6; 00 §5.6 W9): axe-core in the real browser, with
// `color-contrast` ENABLED, over the shell and over the review-only UI gallery in both themes and
// both bundles. jsdom cannot compute contrast or layout, so this file is the only place the
// product's WCAG 2.2 AA claim is measured rather than asserted.
//
// The gallery is the shell's shared-UI review surface (T7): scanning it here means the components
// the shell composes are checked in the same Chromium the app runs in.
import { expect, test, type Page } from '@playwright/test';

import {
  axeScan,
  axeViolations,
  currentTheme,
  expectNoA11yViolations,
  LIMITED_GRANTS,
  localePath,
  LOCALES,
  ORG_ACME,
  ORG_LUMEN,
  PROJECT_STOREFRONT,
  seedStoredTheme,
  THEMES,
  withVariant,
} from './support';

/** The one project screen the shell ships; `project:read` comes from the trimmed grant set. */
const OVERVIEW_PATH = `/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}/overview`;

test.describe('the shell is WCAG 2.2 AA clean (AC6)', () => {
  for (const locale of LOCALES) {
    for (const theme of THEMES) {
      test(`project screen, ${locale} bundle, ${theme} tokens`, async ({ page }) => {
        await seedStoredTheme(page, theme);
        await page.goto(withVariant(localePath(locale, OVERVIEW_PATH), LIMITED_GRANTS));
        await expect(page.locator('#main-content h1')).toBeVisible();
        await expect(page.locator('html')).toHaveAttribute('lang', locale);
        // The theme the labels the case: a scan of the wrong token set would be a false pass.
        expect(await currentTheme(page)).toBe(theme);

        await expectNoA11yViolations(page, `overview ${locale}/${theme}`);
      });
    }
  }

  // The refusal and the landing patterns are shell pages too; they get one case each rather than the
  // full matrix, because they share the shell's chrome and differ only in their own copy.
  test('the 403 pattern is clean in the dark, non-source locale', async ({ page }) => {
    await seedStoredTheme(page, 'dark');
    await page.goto(localePath('vi', `/orgs/${ORG_ACME}/settings`));
    await expect.poll(() => new URL(page.url()).pathname).toBe('/vi/forbidden');
    expect(await currentTheme(page)).toBe('dark');
    await expectNoA11yViolations(page, 'forbidden vi/dark');
  });

  test('the not-found pattern is clean', async ({ page }) => {
    await page.goto(localePath('en', `/orgs/${ORG_LUMEN}/not-a-page`));
    await expect(page.locator('#main-content h1')).toHaveText('Page not found');
    await expectNoA11yViolations(page, 'not-found en/light');
  });
});

test.describe('the UI gallery is WCAG 2.2 AA clean (AC6, T7 hand-over)', () => {
  /**
   * `/dev/ui-gallery` is a review-only route with no shell around it, so there is no `ThemeService`
   * to click: the token set is chosen the way a stylesheet sees it — the attribute on `<html>`.
   */
  async function openGallery(
    page: Page,
    locale: (typeof LOCALES)[number],
    theme: (typeof THEMES)[number],
  ): Promise<void> {
    await page.goto(localePath(locale, '/dev/ui-gallery'));
    await expect(page.locator('h1').first()).toBeVisible();
    await page.evaluate((choice) => {
      if (choice === 'light') document.documentElement.removeAttribute('data-theme');
      else document.documentElement.setAttribute('data-theme', choice);
    }, theme);
  }

  /**
   * The one violation the gallery currently has, and the only one these cases tolerate. It is a
   * `shared/ui` defect, not a shell one, so it is pinned here instead of being fixed (the T7
   * hand-over owns `data-table`):
   *
   *   - rule: `aria-required-children` (impact: critical)
   *   - target: `div[aria-rowcount="251"]` — the `role="table"` wrapper of the VIRTUAL table
   *     (`src/app/shared/ui/data-table/data-table.component.html:12`), whose second child is
   *     `<cdk-virtual-scroll-viewport>` (`:45`). Role `table` may only own `row`/`rowgroup`
   *     children; CDK's viewport is a plain element that wraps the body rows, so the accessibility
   *     tree is invalid for every virtual table in the product.
   *   - repro: open `/dev/ui-gallery`, run axe; the violation appears in both themes and locales.
   *   - exit criterion: when `shared/ui` wraps the viewport as a `rowgroup` (or drops the outer
   *     `role="table"`), this case must be switched back to `expectNoA11yViolations` — the
   *     assertion below fails if the list changes in EITHER direction.
   */
  const KNOWN_GALLERY_VIOLATIONS = [
    { id: 'aria-required-children', target: 'div[aria-rowcount="251"]' },
  ];

  for (const locale of LOCALES) {
    for (const theme of THEMES) {
      test(`gallery, ${locale} bundle, ${theme} tokens`, async ({ page }) => {
        await openGallery(page, locale, theme);
        expect(await currentTheme(page)).toBe(theme === 'light' ? null : theme);

        const results = await axeScan(page);
        const contrast = [...results.passes, ...results.incomplete].filter(
          (rule) => rule.id === 'color-contrast',
        );
        expect(
          contrast.length,
          `axe evaluated color-contrast on ui-gallery ${locale}/${theme}`,
        ).toBeGreaterThan(0);

        // axe's first selector is an array (it may cross frames); the leaf is what names the node.
        const found = axeViolations(results).map((violation) => {
          const first = violation.targets[0];
          const leaf = Array.isArray(first) ? (first as readonly unknown[])[0] : first;
          return {
            id: violation.id,
            target: typeof leaf === 'string' ? leaf : JSON.stringify(leaf),
          };
        });
        expect(found, `axe violations on ui-gallery ${locale}/${theme}`).toEqual(
          KNOWN_GALLERY_VIOLATIONS,
        );
      });
    }
  }
});
