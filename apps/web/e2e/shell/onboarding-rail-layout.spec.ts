import { expect, test } from '@playwright/test';

import { expectCurrentStep, onboardingUrl, shell } from './onboarding-support';
import { LOCALES, seedStoredTheme, THEMES } from './support';

for (const locale of LOCALES) {
  for (const theme of THEMES) {
    test(`onboarding navigation fits its authored labels at rail boundaries — ${locale}, ${theme}`, async ({
      page,
    }, testInfo) => {
      await seedStoredTheme(page, theme);
      await page.setViewportSize({ width: 768, height: 1100 });
      await page.goto(onboardingUrl(locale, 'onboarding-enabled'));
      await expect(shell(page)).toBeVisible();
      await expectCurrentStep(page, 5);
      const chooser = shell(page).locator('#onboarding-step-select');
      const rail = shell(page).locator('[data-testid^="onboarding-select-step-"]');
      const measurements: unknown[] = [];

      for (const width of [768, 1024, 1279]) {
        await page.setViewportSize({ width, height: 1100 });
        await expect(chooser).toBeVisible();
        await expect(rail.first()).toBeHidden();
        await chooser.click();
        await page.getByRole('option').first().click();
        await expectCurrentStep(page, 1);
        await chooser.click();
        await page.getByRole('option').last().click();
        await expectCurrentStep(page, 5);
        const compact = await chooser.evaluate((element) => ({
          height: element.getBoundingClientRect().height,
          width: element.clientWidth,
          scrollWidth: element.scrollWidth,
          overflow:
            Math.max(document.documentElement.scrollWidth, document.body.scrollWidth) -
            document.documentElement.clientWidth,
        }));
        expect(compact.height).toBeGreaterThanOrEqual(44);
        expect(compact.scrollWidth).toBeLessThanOrEqual(compact.width + 1);
        measurements.push({ width, compact });
        expect(compact.overflow).toBeLessThanOrEqual(1);
        if (width === 768) {
          await testInfo.attach('onboarding-compact-768', {
            body: await page.screenshot({ fullPage: true }),
            contentType: 'image/png',
          });
        }
      }

      for (const width of [1280, 1440]) {
        await page.setViewportSize({ width, height: 1100 });
        await expect(chooser).toBeHidden();
        await expect(rail).toHaveCount(5);
        for (const button of await rail.all()) await expect(button).toBeVisible();
        const labels = await rail.evaluateAll((buttons) =>
          buttons.map((button) => {
            const label = button.lastElementChild;
            if (!label) throw new Error('Missing authored onboarding step label');
            const range = document.createRange();
            range.selectNodeContents(label);
            const rectangles = Array.from(range.getClientRects()).filter(
              (rectangle) => rectangle.width > 0,
            );
            const text = range.getBoundingClientRect();
            const bounds = button.getBoundingClientRect();
            return {
              step: button.getAttribute('data-testid'),
              text: label.textContent?.trim(),
              lines: new Set(rectangles.map((rectangle) => Math.round(rectangle.top))).size,
              height: bounds.height,
              textLeft: text.left,
              textRight: text.right,
              buttonLeft: bounds.left,
              buttonRight: bounds.right,
            };
          }),
        );
        for (const label of labels) {
          expect(label.text).toBeTruthy();
          expect(label.lines, `${width}px ${label.step}`).toBe(1);
          expect(label.height).toBeGreaterThanOrEqual(44);
          expect(label.height).toBeLessThanOrEqual(45);
          expect(label.textLeft).toBeGreaterThanOrEqual(label.buttonLeft);
          expect(label.textRight).toBeLessThanOrEqual(label.buttonRight);
        }
        await rail.first().click();
        await expectCurrentStep(page, 1);
        await rail.last().click();
        await expectCurrentStep(page, 5);
        const overflow = await page.evaluate(
          () =>
            Math.max(document.documentElement.scrollWidth, document.body.scrollWidth) -
            document.documentElement.clientWidth,
        );
        expect(overflow).toBeLessThanOrEqual(1);
        measurements.push({ width, labels, overflow });
        await testInfo.attach(`onboarding-rail-${width}`, {
          body: await page.screenshot({ fullPage: true }),
          contentType: 'image/png',
        });
      }

      await testInfo.attach('onboarding-navigation-measurements', {
        body: JSON.stringify(measurements, null, 2),
        contentType: 'application/json',
      });
    });
  }
}
