// 09.1.1-T4.4 (plan 09.1.1 §10 T4, §2 AC6; T8 hand-over): the 412 conflict dialog as a real
// Chromium runs it. The T8 unit specs assert the component's own state; none of them can show what
// the browser does with a native `showModal()` dialog — the top layer, Tab containment, Escape as
// `cancel`, the backdrop hit test, and the inertness of everything behind it.
//
// Assertions below state what `ConflictDialogComponent` actually defines, read from its source
// (`src/app/shared/ui/conflict/conflict-dialog.component.ts`):
//   - opening: `afterRenderEffect` calls `dialog.showModal()` and remembers `document.activeElement`
//     as the opener (`:194-203`);
//   - dismissal: `dismiss()` closes the dialog, emits `keepEditing`, and focuses the remembered
//     opener (`:229-247`) — Escape reaches it through the native `cancel` event and the component's
//     own `keydown.escape`, both of which call `cancel()`;
//   - a click dismisses ONLY when its target is the `<dialog>` element itself AND its coordinates
//     are outside the dialog's box (`:219-231`); a click on any child never dismisses.
//
// Defects this file finds in `shared/ui` are REPORTED, not fixed (the task owns `shell/**` only).
import { expect, test, type Page } from '@playwright/test';

import { axeScan, axeViolations, localePath, LOCALES, THEMES } from './support';

const OPENER = '[data-demo="conflict-opener"]';
const DIALOG = '[data-demo="conflict"] dialog';

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
  expect(await page.locator(DIALOG).evaluate((dialog: HTMLDialogElement) => dialog.open)).toBe(
    true,
  );
}

/**
 * Where focus is, relative to the dialog, after one Tab or Shift+Tab: `inside:<text>`,
 * `outside:<text>` for a page control, or `body` — Chromium's own stop between the last control of
 * a modal dialog and the first one, where nothing in the document is focused.
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

      // The component relies entirely on the platform for containment, so this walks well past the
      // three focusable controls (Close, Keep editing, Reload current) in both directions.
      const forward: string[] = [];
      for (let index = 0; index < 5; index += 1) forward.push(await focusAfterKey(page, 'Tab'));
      const backward: string[] = [];
      for (let index = 0; index < 5; index += 1)
        backward.push(await focusAfterKey(page, 'Shift+Tab'));

      const inside = (steps: string[]) => steps.filter((step) => step.startsWith('inside'));
      const outside = (steps: string[]) => steps.filter((step) => step.startsWith('outside'));
      const bodyStops = (steps: string[]) => steps.filter((step) => step === 'body');

      // Containment: no page control is reachable in either direction. Chromium lets one Tab leave
      // a modal dialog for a stop where `document.activeElement` is `body` — the page is inert and
      // cannot take focus, so nothing behind the dialog becomes reachable — and the next Tab
      // re-enters. The sequences are asserted as measured, not idealised; that stop is the
      // platform's and is in the hand-over notes.
      expect(outside(forward), forward.join(' | ')).toEqual([]);
      expect(outside(backward), backward.join(' | ')).toEqual([]);
      expect(bodyStops(forward).length, forward.join(' | ')).toBeLessThanOrEqual(1);
      expect(bodyStops(backward).length, backward.join(' | ')).toBeLessThanOrEqual(1);
      for (const steps of [forward, backward]) {
        steps.forEach((step, index) => {
          if (step !== 'body' || index === steps.length - 1) return;
          expect(
            steps[index + 1],
            `the Tab after the body stop re-enters: ${steps.join(' | ')}`,
          ).toMatch(/^inside:/);
        });
      }
      // Containment is not the same as a stuck focus ring: the cycle really visits the dialog's
      // controls, and it visits the same set in both directions.
      expect(new Set(inside(forward)).size, forward.join(' | ')).toBeGreaterThan(1);
      expect(new Set(inside(backward))).toEqual(new Set(inside(forward)));
    });

    test('Escape dismisses, leaves the top layer and returns focus to the opener', async ({
      page,
    }) => {
      await openGallery(page, locale, 'light');
      await openDialog(page);

      // `:modal` is the browser's own statement that the dialog is in the top layer and the rest of
      // the document is inert — the property `showModal()` exists for, and one jsdom cannot have.
      const modalWhileOpen = await page
        .locator(DIALOG)
        .evaluate((dialog: HTMLDialogElement) => dialog.matches(':modal'));
      expect(modalWhileOpen, 'the dialog is modal while open').toBe(true);

      await page.keyboard.press('Escape');

      await expect(page.locator(DIALOG)).toBeHidden();
      const afterClose = await page.locator(DIALOG).evaluate((dialog: HTMLDialogElement) => ({
        open: dialog.open,
        modal: dialog.matches(':modal'),
      }));
      expect(afterClose).toEqual({ open: false, modal: false });
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

      // A click in the viewport corner hits the ::backdrop; Chromium reports the `<dialog>` as the
      // target with coordinates outside its box, which is exactly the component's condition.
      await page.mouse.click(4, 4);
      await expect(page.locator(DIALOG)).toBeHidden();
      await expect(page.locator(OPENER)).toBeFocused();
    });

    test('the rest of the page is inert while the dialog is open', async ({ page }) => {
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
            isDialog: element === dialog,
            tag: element?.tagName.toLowerCase() ?? 'nothing',
          };
        },
        {
          selector: DIALOG,
          x: (box?.x ?? 0) + (box?.width ?? 0) / 2,
          y: (box?.y ?? 0) + (box?.height ?? 0) / 2,
        },
      );
      // The backdrop sits in the top layer, so the opener's own pixels now belong to the dialog.
      expect(hit, `elementFromPoint over the opener returned ${hit.tag}`).toEqual({
        isDialog: true,
        tag: 'dialog',
      });

      // Programmatic focus is refused too: a modal dialog makes everything else inert, so the
      // opener cannot take focus until the dialog closes.
      const focused = await page.evaluate((selector) => {
        const openerElement = document.querySelector(
          '[data-demo="conflict-opener"]',
        ) as HTMLElement;
        openerElement.focus();
        const dialog = document.querySelector(selector) as HTMLElement;
        return dialog.contains(document.activeElement);
      }, DIALOG);
      expect(focused, 'focus is still inside the dialog after focusing the opener').toBe(true);

      // Chromium's accessibility tree: recorded, not asserted, because how much of the inert page
      // stays exposed is the browser's decision, not the component's.
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

        const results = await axeScan(page);
        const contrast = [...results.passes, ...results.incomplete].filter(
          (rule) => rule.id === 'color-contrast',
        );
        expect(
          contrast.length,
          `axe evaluated color-contrast with the dialog open`,
        ).toBeGreaterThan(0);

        // Nothing at all — not even the gallery's own pinned finding. The page behind a modal
        // dialog is inert and axe skips inert subtrees, so `aria-required-children` on the virtual
        // table's `role="table"` wrapper (pinned in `ui-gallery.spec.ts`) is unreachable from here.
        // What this proves is that the dialog surface adds no violation with contrast on.
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
