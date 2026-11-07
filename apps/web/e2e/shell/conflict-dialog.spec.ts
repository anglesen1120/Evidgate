// Real-browser coverage for Brain's modal overlay, focus trap, dismissal and restoration.
import { expect, test, type Locator, type Page } from '@playwright/test';

import head from '../../../../fixtures/web-api/policy-revision-head/input.json';
import versionPointer from '../../../../fixtures/web-api/policy-revision-create-version-pointer/input.json';

import {
  CONFLICT,
  CONFLICT_DRAFT,
  CREATE_412,
  CREATE_VERSION_POINTER,
  BODY as PUBLISH_BODY,
  POLICY_WRITE,
  SUBMIT as PUBLISH_SUBMIT,
  openPublish,
  visit,
} from './settings-policy.support';
import { axeScan, axeViolations } from '../a11y/axe-scan';
import { localePath, LOCALES, THEMES, waitForDialogAnimations } from './support';

const OPENER = '[data-demo="conflict-opener"]';
const DIALOG = '[role="dialog"]:has(hlm-dialog-content)';

/** The `details[].reason` of the served document's `/version` row: what the dialog must display. */
const POINTER_REVISION = ((): string => {
  const detail = versionPointer.body.error.details.find((row) => row.path === '/version');
  if (detail === undefined) throw new Error('the pointer-form 412 document names no /version');
  return detail.reason;
})();

/**
 * The local/server pair of every compared field, in the order the dialog renders them: the browser
 * proof that the server column was filled from a body the envelope's own identifier named.
 */
async function comparedFields(scope: Locator): Promise<readonly string[]> {
  return scope
    .locator('.field')
    .evaluateAll((fields) =>
      fields.map((field) =>
        [...field.querySelectorAll('.local-value, .server-value')]
          .map((value) => value.textContent?.trim() ?? '')
          .join(' → '),
      ),
    );
}

/**
 * The gallery is review-only and has no shell (`/dev/ui-gallery`), so the theme is only the
 * attribute `ThemeService` would otherwise own.
 */
async function openGallery(
  page: Page,
  locale: (typeof LOCALES)[number],
  theme: (typeof THEMES)[number],
): Promise<void> {
  await page.goto(localePath(locale, '/dev/ui-gallery'));
  await expect(page.locator('h1').first()).toBeVisible();
  await expect(page.locator(OPENER)).toBeVisible();
  await page.evaluate((choice) => {
    if (choice === 'light') document.documentElement.removeAttribute('data-theme');
    else document.documentElement.setAttribute('data-theme', choice);
  }, theme);
}

/** Opens through the gallery's own button, exactly as a user does: the opener must have focus. */
async function openDialog(page: Page): Promise<void> {
  await page.locator(OPENER).click();
  await expect(page.locator(DIALOG)).toBeVisible();
  await expect(page.locator(DIALOG)).toHaveAttribute('aria-modal', 'true');
}

/**
 * Where focus is, relative to the dialog, after one Tab or Shift+Tab: `inside:<text>`,
 * `outside:<text>` for a page control, or `body` when nothing in the document is focused (the
 * stop Chromium puts between a modal dialog's last and first control unless the dialog wraps).
 */
async function focusAfterKey(page: Page, key: 'Tab' | 'Shift+Tab'): Promise<string> {
  await page.keyboard.press(key);
  return page.evaluate((selector) => {
    const dialog = document.querySelector(selector) as HTMLElement;
    const active = document.activeElement;
    if (!(active instanceof HTMLElement) || active === document.body) return 'body';
    const text = (active.textContent ?? '').trim().replace(/\s+/g, ' ').slice(0, 40);
    return dialog.contains(active) ? `inside:${text}` : `outside:${text}`;
  }, DIALOG);
}

for (const locale of LOCALES) {
  test.describe(`412 conflict dialog, ${locale} bundle (T8 hand-over)`, () => {
    test('Tab and Shift+Tab stay inside the modal', async ({ page }) => {
      await openGallery(page, locale, 'light');
      await openDialog(page);

      // Walks well past the three focusable controls (Close, Keep editing, Reload current) in both
      // directions, so each direction crosses the wrap at least once.
      const forward: string[] = [];
      for (let index = 0; index < 5; index += 1) forward.push(await focusAfterKey(page, 'Tab'));
      const backward: string[] = [];
      for (let index = 0; index < 5; index += 1)
        backward.push(await focusAfterKey(page, 'Shift+Tab'));

      const inside = (steps: string[]) => steps.filter((step) => step.startsWith('inside'));
      const outside = (steps: string[]) => steps.filter((step) => step.startsWith('outside'));
      const bodyStops = (steps: string[]) => steps.filter((step) => step === 'body');

      // CDK's focus-trap sentinels wrap both boundaries without a stop on the page or body.
      expect(outside(forward), forward.join(' | ')).toEqual([]);
      expect(outside(backward), backward.join(' | ')).toEqual([]);
      expect(bodyStops(forward), forward.join(' | ')).toEqual([]);
      expect(bodyStops(backward), backward.join(' | ')).toEqual([]);
      // Containment is not the same as a stuck focus ring: the cycle really visits the dialog's
      // controls, and it visits the same set in both directions.
      expect(new Set(inside(forward)).size, forward.join(' | ')).toBeGreaterThan(1);
      expect(new Set(inside(backward))).toEqual(new Set(inside(forward)));
    });

    test('Escape dismisses the overlay and returns focus to the opener', async ({ page }) => {
      await openGallery(page, locale, 'light');
      await openDialog(page);

      await expect(page.locator(DIALOG)).toHaveAttribute('aria-modal', 'true');

      await page.keyboard.press('Escape');

      await expect(page.locator(DIALOG)).toBeHidden();
      await expect(page.locator(DIALOG)).toHaveCount(0);
      await expect(page.locator(OPENER)).toBeFocused();
    });

    test('the backdrop dismisses, a click inside does not', async ({ page }) => {
      await openGallery(page, locale, 'light');
      await openDialog(page);

      // A click on the dialog's own body area stays inside its box → the component ignores it.
      const inside = await page.locator(`${DIALOG} .body`).boundingBox();
      expect(inside).not.toBeNull();
      await page.mouse.click(
        (inside?.x ?? 0) + (inside?.width ?? 0) / 2,
        (inside?.y ?? 0) + (inside?.height ?? 0) / 2,
      );
      await expect(page.locator(DIALOG)).toBeVisible();

      // The CDK backdrop fills the viewport outside the content.
      await page.mouse.click(4, 4);
      await expect(page.locator(DIALOG)).toBeHidden();
      await expect(page.locator(OPENER)).toBeFocused();
    });

    test('the modal blocks background pointer access and hides background content from assistive technology', async ({
      page,
    }) => {
      await openGallery(page, locale, 'light');
      await openDialog(page);

      const opener = page.locator(OPENER);
      const box = await opener.boundingBox();
      expect(box).not.toBeNull();
      const hit = await page.evaluate(
        ({ selector, x, y }) => {
          const element = document.elementFromPoint(x, y);
          const dialog = document.querySelector(selector);
          return {
            blocked:
              element === dialog ||
              dialog?.contains(element) ||
              element?.classList.contains('cdk-overlay-backdrop'),
            tag: element?.tagName.toLowerCase() ?? 'nothing',
          };
        },
        {
          selector: DIALOG,
          x: (box?.x ?? 0) + (box?.width ?? 0) / 2,
          y: (box?.y ?? 0) + (box?.height ?? 0) / 2,
        },
      );
      expect(hit.blocked, `elementFromPoint over the opener returned ${hit.tag}`).toBe(true);
      await expect(
        page
          .locator('app-ui-gallery')
          .locator('xpath=ancestor-or-self::*[@aria-hidden="true"]')
          .first(),
      ).toBeAttached();

      // The accessibility snapshot exposes the modal rather than the background gallery.
      const snapshot = await page.locator('body').ariaSnapshot();
      test.info().annotations.push({
        type: 'dialog-open-aria-snapshot',
        description: snapshot.slice(0, 1200),
      });
      expect(snapshot).toContain('dialog');
    });

    for (const theme of THEMES) {
      test(`axe with the dialog open, ${theme}`, async ({ page }) => {
        await openGallery(page, locale, theme);
        await openDialog(page);
        await waitForDialogAnimations(page, DIALOG);

        const results = await axeScan(page);
        const contrast = [...results.passes, ...results.incomplete].filter(
          (rule) => rule.id === 'color-contrast',
        );
        expect(
          contrast.length,
          `axe evaluated color-contrast with the dialog open`,
        ).toBeGreaterThan(0);

        // CDK hides the background from assistive technology while this modal is open.
        const found = axeViolations(results).map((violation) => ({
          id: violation.id,
          targets: violation.targets.map((target) => JSON.stringify(target)),
        }));
        expect(
          found.map((violation) => violation.id),
          JSON.stringify(found),
        ).toEqual([]);

        // And the dialog itself is clean in both themes, contrast included.
        const dialogOnly = await axeScan(page, DIALOG);
        expect(
          axeViolations(dialogOnly).map((violation) => violation.id),
          'violations inside the dialog',
        ).toEqual([]);
      });
    }

    test('the loading, empty, error, forbidden and not-found specimens are inside 360px', async ({
      page,
    }) => {
      // `ui-gallery.spec.ts` already asserts `documentElement.scrollWidth <= innerWidth` for the
      // WHOLE document at 360px in en/vi × light/dark, which covers these five sections (they are
      // part of the same page, always rendered). What that cannot show is whether a section was
      // actually laid out: a section collapsed to zero box would pass that check vacuously. So this
      // case only proves each specimen has a real box inside the narrow viewport.
      await page.setViewportSize({ width: 360, height: 800 });
      await openGallery(page, locale, 'light');

      const specimens = ['loading', 'empty', 'error', 'forbidden', 'not-found'] as const;
      const boxes = await page.evaluate((names) => {
        const limit = window.innerWidth;
        return names.map((name) => {
          const element = document.querySelector(`[data-demo="${name}"]`) as HTMLElement;
          const rect = element.getBoundingClientRect();
          return {
            name,
            width: Math.round(rect.width),
            left: Math.round(rect.left),
            right: Math.round(rect.right),
            limit,
          };
        });
      }, specimens);

      for (const box of boxes) {
        expect(box.width, `${box.name} has a laid-out box`).toBeGreaterThan(0);
        expect(box.left, `${box.name} starts inside the viewport`).toBeGreaterThanOrEqual(-0.5);
        expect(box.right, `${box.name} ends inside the viewport`).toBeLessThanOrEqual(box.limit);
      }
    });
  });
}

for (const locale of LOCALES) {
  test.describe(`412 conflict dialog over a served 412, ${locale} bundle`, () => {
    // The publish dialog is the one web screen that opens the shared dialog, and the corpus serves
    // it a 412 whose detail names the concurrency token as `/version` — the pointer spelling the
    // authoring routes emit — instead of the bare `revision_id`. Both documents name the same head,
    // so the two runs must end in the same comparison: a matcher that dropped the pointer would
    // show an unavailable head and one "Not compared" field instead.
    test('the /version pointer detail fills the dialog like the bare name', async ({ page }) => {
      await visit(page, locale, [POLICY_WRITE, CREATE_VERSION_POINTER]);
      const dialog = await openPublish(page);
      await dialog.locator(PUBLISH_BODY).fill(CONFLICT_DRAFT);
      await dialog.locator(PUBLISH_SUBMIT).click();

      const pointer = page.locator(CONFLICT);
      await expect(pointer).toBeVisible();
      // Nothing is inferred from the pointer's shape: the identifier the envelope names is what the
      // dialog shows, and the server column came from the body that identifier addresses.
      await expect(pointer.locator('.revision code')).toHaveText(POINTER_REVISION);
      await expect(pointer.locator('.field')).toHaveCount(2);
      const changed = pointer.locator('.field', { hasText: '/flaky_budget' });
      await expect(changed.locator('.server-value')).toContainText(
        String(head.body.body.flaky_budget),
      );
      await expect(changed.locator('.local-value')).toContainText('9');
      const removed = pointer.locator('.field', { hasText: '/late_window_minutes' });
      await expect(removed.locator('.server-value')).toContainText(
        String(head.body.body.late_window_minutes),
      );
      const pointerComparison = await comparedFields(pointer);

      // The same 412 with the token in the bare-name spelling: the same head, and every compared
      // pair must match, so only the matched detail's spelling differs between the two runs.
      await visit(page, locale, [POLICY_WRITE, CREATE_412]);
      const bare = await openPublish(page);
      await bare.locator(PUBLISH_BODY).fill(CONFLICT_DRAFT);
      await bare.locator(PUBLISH_SUBMIT).click();
      const conflict = page.locator(CONFLICT);
      await expect(conflict).toBeVisible();
      await expect(conflict.locator('.revision code')).toHaveText(POINTER_REVISION);
      expect(
        await comparedFields(conflict),
        'the pointer-form 412 must compare exactly what the bare-name 412 compares',
      ).toEqual(pointerComparison);
      expect(pointerComparison).toHaveLength(2);
    });
  });
}
