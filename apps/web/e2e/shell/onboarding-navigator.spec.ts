import { expect, test, type Page } from '@playwright/test';

import { expectNoA11yViolations } from '../a11y/axe-scan';
import { expectCurrentStep, onboardingUrl, shell, stepItem } from './onboarding-support';
import { LOCALES, seedStoredTheme, THEMES } from './support';

const WIDTHS = [390, 768, 1024, 1280, 1440] as const;

async function expectNavigatorGeometry(page: Page, width: number): Promise<void> {
  // Viewport-dependent Angular content settles after the browser's resize event.
  await expect
    .poll(() =>
      page.evaluate(
        () => document.documentElement.scrollWidth - document.documentElement.clientWidth,
      ),
    )
    .toBeLessThanOrEqual(1);
  const layout = await page.getByTestId('onboarding-stepper').evaluate((list) => {
    const card = list.closest('section');
    const body = document.getElementById('onboarding-step-body');
    if (!card || !body) throw new Error('Missing onboarding regions');
    return {
      cardWidth: card.getBoundingClientRect().width,
      bodyWidth: body.getBoundingClientRect().width,
      overflow: document.documentElement.scrollWidth - document.documentElement.clientWidth,
      steps: Array.from(list.querySelectorAll('button')).map((button) => {
        const label = button.querySelector('[data-step-label]');
        if (!label) throw new Error('Missing authored step label');
        const range = document.createRange();
        range.selectNodeContents(label);
        const bounds = button.getBoundingClientRect();
        return {
          x: bounds.x,
          y: bounds.y,
          width: bounds.width,
          height: bounds.height,
          labelY: label.getBoundingClientRect().y,
          textFits: Array.from(range.getClientRects()).every(
            (line) =>
              line.left >= bounds.left - 1 &&
              line.right <= bounds.right + 1 &&
              line.top >= bounds.top - 1 &&
              line.bottom <= bounds.bottom + 1,
          ),
        };
      }),
    };
  });
  expect(layout.steps).toHaveLength(5);
  expect(layout.overflow).toBeLessThanOrEqual(1);
  expect(Math.abs(layout.cardWidth - layout.bodyWidth)).toBeLessThanOrEqual(1);
  const first = layout.steps[0];
  if (!first) throw new Error('Empty step navigator');
  for (const step of layout.steps) {
    expect(step.width).toBeGreaterThanOrEqual(44);
    expect(step.height).toBeGreaterThanOrEqual(44);
    expect(step.textFits).toBe(true);
    expect(Math.abs(width >= 768 ? step.y - first.y : step.x - first.x)).toBeLessThanOrEqual(1);
    if (width >= 768) expect(Math.abs(step.labelY - first.labelY)).toBeLessThanOrEqual(1);
  }
}

for (const locale of LOCALES) {
  for (const theme of THEMES) {
    for (const width of WIDTHS) {
      test(`single onboarding navigator at ${width}px — ${locale}, ${theme}`, async ({
        page,
      }, testInfo) => {
        await seedStoredTheme(page, theme);
        await page.setViewportSize({ width, height: 1100 });
        await page.goto(onboardingUrl(locale, 'onboarding-project'));
        await expectCurrentStep(page, 2);
        await expect(shell(page).getByRole('navigation')).toHaveCount(1);
        await expect(shell(page).locator('hlm-select')).toHaveCount(0);
        await page.evaluate(async () => {
          await document.fonts.ready;
        });
        await expectNavigatorGeometry(page, width);
        await expectNoA11yViolations(
          page,
          `single onboarding navigator ${locale}/${theme}/${width}`,
        );
        const screenshot = testInfo.outputPath(`onboarding-${locale}-${theme}-${width}.png`);
        await page.screenshot({ path: screenshot, fullPage: true });
        await testInfo.attach('onboarding', { path: screenshot, contentType: 'image/png' });
      });
    }
    for (const seed of ['onboarding-empty', 'onboarding-enabled'] as const) {
      test(`navigator boundary resources — ${seed}, ${locale}, ${theme}`, async ({ page }) => {
        await seedStoredTheme(page, theme);
        await page.goto(onboardingUrl(locale, seed));
        await expectCurrentStep(page, seed === 'onboarding-empty' ? 1 : 5);
        await page.evaluate(async () => {
          await document.fonts.ready;
        });
        const count = page.getByTestId('onboarding-progress-count');
        const confirmed = await count.textContent();
        for (const width of [320, 390, 767, 768, 1024, 1279, 1280, 1440]) {
          await page.setViewportSize({ width, height: 1100 });
          await expectNavigatorGeometry(page, width);
          if (seed === 'onboarding-enabled') {
            for (const step of [1, 5] as const) {
              await page.getByTestId(`onboarding-select-step-${step}`).click();
              await expectCurrentStep(page, step);
              await expect(count).toHaveText(confirmed ?? '');
              await expectNavigatorGeometry(page, width);
            }
          }
        }
      });
    }
  }

  test(`step keyboard focus and activation remain separate from completion — ${locale}`, async ({
    page,
  }) => {
    await page.goto(onboardingUrl(locale, 'onboarding-project'));
    await expectCurrentStep(page, 2);
    const count = page.getByTestId('onboarding-progress-count');
    const confirmed = await count.textContent();
    const first = page.getByTestId('onboarding-select-step-1');
    const second = page.getByTestId('onboarding-select-step-2');
    const blocked = page.getByTestId('onboarding-select-step-3');
    await first.focus();
    await page.keyboard.press('ArrowRight');
    await expect(second).toBeFocused();
    await page.keyboard.press('ArrowRight');
    await expect(blocked).toBeFocused();
    await expect(blocked).toHaveAttribute('aria-disabled', 'true');
    await expect(blocked).toHaveAttribute('aria-describedby', 'onboarding-locked-reason');
    await expect(page.locator('#onboarding-locked-reason')).toBeVisible();
    for (const key of ['Enter', 'Space']) {
      await page.keyboard.press(key);
      await expectCurrentStep(page, 2);
      await expect(blocked).toBeFocused();
    }
    await page.keyboard.press('Home');
    await expect(first).toBeFocused();
    await expectCurrentStep(page, 2);
    await page.keyboard.press('Enter');
    await expectCurrentStep(page, 1);
    await expect(page.locator('#onboarding-step-title')).toBeFocused();
    await second.focus();
    await page.keyboard.press('Space');
    await expectCurrentStep(page, 2);
    await expect(page.locator('#onboarding-step-title')).toBeFocused();
    await expect(count).toHaveText(confirmed ?? '');
    await expect(stepItem(page, 1)).toHaveAttribute('data-done', 'true');
    await expect(stepItem(page, 2)).toHaveAttribute('data-done', 'false');
  });
}

for (const theme of THEMES) {
  test(`Vietnamese navigation reflows with enlarged text and spacing — ${theme}`, async ({
    page,
  }) => {
    await seedStoredTheme(page, theme);
    await page.setViewportSize({ width: 320, height: 1100 });
    await page.goto(onboardingUrl('vi', 'onboarding-project'));
    await expectCurrentStep(page, 2);
    await page.getByTestId('onboarding-stepper').evaluate((list) => {
      const text = Array.from(list.querySelectorAll<HTMLElement>('span')).map((element) => ({
        element,
        size: Number.parseFloat(getComputedStyle(element).fontSize),
      }));
      for (const { element, size } of text) {
        element.style.fontSize = `${size * 2}px`;
        element.style.lineHeight = '1.5';
        element.style.letterSpacing = '0.12em';
        element.style.wordSpacing = '0.16em';
      }
      for (const label of list.querySelectorAll('[data-step-label]')) {
        const original = label.textContent ?? '';
        label.textContent = `${original} ${original.slice(0, Math.ceil(original.length * 0.4))}`;
      }
    });
    await expectNavigatorGeometry(page, 320);
  });
}
