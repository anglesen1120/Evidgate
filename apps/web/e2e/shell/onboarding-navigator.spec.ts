import { expect, test, type Page } from '@playwright/test';

import { expectNoA11yViolations } from '../a11y/axe-scan';
import { expectCurrentStep, onboardingUrl, shell, stepItem } from './onboarding-support';
import { LOCALES, seedStoredTheme, THEMES } from './support';

const WIDTHS = [390, 768, 1024, 1280, 1440] as const;

async function expectNavigatorGeometry(page: Page, width: number): Promise<void> {
  const toggle = page.getByTestId('onboarding-step-toggle');
  if (width < 768 && (await toggle.getAttribute('aria-expanded')) === 'false') {
    await toggle.click();
  }
  await expect(page.getByTestId('onboarding-stepper')).toBeVisible();
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
        if (width < 768) {
          await expect(page.getByTestId('onboarding-stepper')).toBeHidden();
          await expectNoA11yViolations(page, `collapsed steps ${locale}/${theme}/${width}`);
          const notice = page.locator('app-desktop-required-notice');
          await expect(notice.getByRole('note')).toBeVisible();
          expect(
            await notice.evaluate((element) => {
              const health = document.querySelector('app-connection-health');
              const cli = document.querySelector('app-cli-quickstart');
              return (
                !!health &&
                !!cli &&
                !!(element.compareDocumentPosition(health) & Node.DOCUMENT_POSITION_FOLLOWING) &&
                !!(element.compareDocumentPosition(cli) & Node.DOCUMENT_POSITION_FOLLOWING)
              );
            }),
          ).toBe(true);
        }
        await page.evaluate(async () => {
          await document.fonts.ready;
        });
        await expectNavigatorGeometry(page, width);
        for (const status of await page.locator('[data-step-status]').all()) {
          expect(
            await status.evaluate((element) => {
              const icon = element.querySelector('hlm-icon');
              const text = icon?.nextElementSibling;
              if (!icon || !text) throw new Error('Missing step status');
              const range = document.createRange();
              range.selectNodeContents(text);
              const firstLine = range.getClientRects()[0];
              const bounds = icon.getBoundingClientRect();
              return !!firstLine && bounds.top < firstLine.bottom && bounds.bottom > firstLine.top;
            }),
          ).toBe(true);
        }
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

    test(`compact disclosure keyboard and focus return — ${locale}, ${theme}`, async ({ page }) => {
      await seedStoredTheme(page, theme);
      await page.setViewportSize({ width: 390, height: 1100 });
      await page.goto(onboardingUrl(locale, 'onboarding-project'));
      await expectCurrentStep(page, 2);
      const toggle = page.getByTestId('onboarding-step-toggle');
      const list = page.getByTestId('onboarding-stepper');
      await toggle.focus();
      await page.keyboard.press('Enter');
      await expect(toggle).toHaveAttribute('aria-expanded', 'true');
      await page.keyboard.press('Escape');
      await expect(list).toBeHidden();
      await expect(toggle).toBeFocused();
      await page.keyboard.press('Space');
      await expect(list).toBeVisible();
      await page.keyboard.press('Tab');
      await expect(page.getByTestId('onboarding-select-step-1')).toBeFocused();
      await page.keyboard.press('End');
      const blocked = page.getByTestId('onboarding-select-step-5');
      await expect(blocked).toBeFocused();
      await expect(blocked).toHaveAttribute('aria-disabled', 'true');
      await page.keyboard.press('Space');
      await expectCurrentStep(page, 2);
      await expect(toggle).toHaveAttribute('aria-expanded', 'true');
      await page.keyboard.press('Escape');
      await expect(list).toBeHidden();
      await expect(toggle).toBeFocused();
      await page.keyboard.press('Space');
      await expect(list).toBeVisible();
      await page.keyboard.press('Tab');
      await page.keyboard.press('Enter');
      await expectCurrentStep(page, 1);
      await expect(list).toBeHidden();
      await expect(page.locator('#onboarding-step-title')).toBeFocused();
      await toggle.click();
      await expect(list).toBeVisible();
      await page.getByTestId('onboarding-select-step-2').focus();
      await page.keyboard.press('Space');
      await expectCurrentStep(page, 2);
      await expect(list).toBeHidden();
      await expect(page.locator('#onboarding-step-title')).toBeFocused();
      await toggle.click();
      await expect(list).toBeVisible();
      await page.getByTestId('onboarding-select-step-2').click();
      await expect(list).toBeHidden();
      await expect(page.locator('#onboarding-step-title')).toBeFocused();
    });

    test(`compact state survives rotation at 767/768 — ${locale}, ${theme}`, async ({ page }) => {
      await seedStoredTheme(page, theme);
      await page.setViewportSize({ width: 767, height: 1100 });
      await page.goto(onboardingUrl(locale, 'onboarding-project'));
      await expectCurrentStep(page, 2);
      const list = page.getByTestId('onboarding-stepper');
      const original = await list.elementHandle();
      const toggle = page.getByTestId('onboarding-step-toggle');
      await expect(list).toBeHidden();
      await page.setViewportSize({ width: 768, height: 1100 });
      await expect(list).toBeVisible();
      await expect(toggle).toBeHidden();
      await page.getByTestId('onboarding-select-step-1').focus();
      await page.setViewportSize({ width: 767, height: 1100 });
      await expect(list).toBeHidden();
      await expect(toggle).toBeFocused();
      await toggle.click();
      for (const width of [768, 767, 390, 1440, 390]) {
        await page.setViewportSize({ width, height: 1100 });
        await expect(list).toBeVisible();
        await expectCurrentStep(page, 2);
        await expect(toggle).toHaveAttribute('aria-expanded', 'true');
        expect(await list.evaluate((element, previous) => element === previous, original)).toBe(
          true,
        );
      }
      const routeBeforeResources = page.url();
      await page.getByTestId('onboarding-project-context').getByRole('link').click();
      await expect(page.locator('#onboarding-resources')).toBeFocused();
      await expect(page).toHaveURL(routeBeforeResources);
      await expect(page.getByTestId('onboarding-evidence-panel')).toHaveCount(1);
    });

    for (const [seed, step] of [
      ['onboarding-empty', 1],
      ['onboarding-connected', 3],
      ['onboarding-enabled', 5],
    ] as const) {
      test(`compact summary from confirmed ${seed} — ${locale}, ${theme}`, async ({ page }) => {
        await seedStoredTheme(page, theme);
        await page.setViewportSize({ width: 390, height: 1100 });
        await page.goto(onboardingUrl(locale, seed));
        await expectCurrentStep(page, step);
        const viewed = page.getByTestId('onboarding-viewed-summary');
        const next = page.getByTestId('onboarding-next-summary');
        await expect(viewed).toContainText(locale === 'vi' ? `${step}/5` : `${step} of 5`);
        await expect(viewed).toContainText(
          (await page.locator('#onboarding-step-title').textContent())?.trim() ?? '',
        );
        await expect(next).toContainText(
          seed === 'onboarding-enabled'
            ? locale === 'vi'
              ? 'Đã hoàn thành tất cả 5 bước.'
              : 'All 5 steps complete.'
            : locale === 'vi'
              ? `Bước cần làm ${step}/5`
              : `Next required ${step} of 5`,
        );
        await expect(page.getByTestId('onboarding-project-context')).toHaveCount(
          seed === 'onboarding-empty' ? 0 : 1,
        );
        for (const width of [320, 390]) {
          await page.setViewportSize({ width, height: 1100 });
          await expect
            .poll(() => page.evaluate(() => document.documentElement.scrollWidth - innerWidth))
            .toBeLessThanOrEqual(1);
          await expectNoA11yViolations(page, `collapsed ${seed}/${locale}/${theme}/${width}`);
          await expectNavigatorGeometry(page, width);
          await expectNoA11yViolations(page, `expanded ${seed}/${locale}/${theme}/${width}`);
          await page.getByTestId('onboarding-step-toggle').click();
          await expect(page.getByTestId('onboarding-stepper')).toBeHidden();
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
    await page.getByTestId('onboarding-step-toggle').click();
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
