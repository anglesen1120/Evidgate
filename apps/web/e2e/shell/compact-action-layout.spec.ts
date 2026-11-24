import { expect, test, type Locator, type Page } from '@playwright/test';
import { expectCurrentStep, onboardingUrl } from './onboarding-support';
import { runDetailPath } from './run-detail.support';
import {
  LOCALES,
  ORG_ACME,
  SETTINGS_ADMIN_GRANTS,
  THEMES,
  localePath,
  seedStoredTheme,
  withVariants,
} from './support';

function actionLayoutFailures(elements: Element[]) {
  if (elements.length === 0) return ['no visible actions'];
  return elements.flatMap((element) => {
    // The responsive table can replace its row nodes; scroll and measure the same live snapshot.
    element.scrollIntoView({ behavior: 'instant', block: 'nearest', inline: 'nearest' });
    const range = document.createRange();
    range.selectNodeContents(element);
    const fragments = Array.from(range.getClientRects()).filter((rect) => rect.width > 0);
    const lines = new Set(fragments.map((rect) => Math.round(rect.top))).size;
    const bounds = element.getBoundingClientRect();
    const cell = element.closest('td, dd')?.getBoundingClientRect();
    const scroller = element.closest('.spartan-table-container')?.getBoundingClientRect();
    const left = Math.max(cell?.left ?? 0, scroller?.left ?? 0, 0);
    const right = Math.min(cell?.right ?? innerWidth, scroller?.right ?? innerWidth, innerWidth);
    const textFits = fragments.every(
      (rect) => rect.left >= bounds.left - 1 && rect.right <= bounds.right + 1,
    );
    const reachable = bounds.left >= left - 1 && bounds.right <= right + 1;
    return lines === 1 && textFits && reachable
      ? []
      : [{ label: element.textContent?.trim(), lines, textFits, reachable }];
  });
}

async function expectSingleLineActions(page: Page, buttons: Locator): Promise<void> {
  await expect(buttons.first()).toBeVisible();
  await page.evaluate(() => document.fonts.ready);
  for (const width of [390, 768, 900, 1440]) {
    await page.setViewportSize({ width, height: 900 });
    await expect
      .poll(() => buttons.evaluateAll(actionLayoutFailures), {
        message: `short actions at ${width}px`,
      })
      .toEqual([]);
  }
}
function actionContainmentFailures(elements: Element[]) {
  if (elements.length === 0) return ['no visible actions'];
  return elements.flatMap((element) => {
    element.scrollIntoView({ behavior: 'instant', block: 'nearest', inline: 'nearest' });
    const bounds = element.getBoundingClientRect();
    const cell = element.closest('td, dd')?.getBoundingClientRect();
    const scroller = element.closest('.spartan-table-container')?.getBoundingClientRect();
    const left = Math.max(cell?.left ?? 0, scroller?.left ?? 0, 0);
    const right = Math.min(cell?.right ?? innerWidth, scroller?.right ?? innerWidth, innerWidth);
    const inside = bounds.left >= left - 1 && bounds.right <= right + 1;
    return inside
      ? []
      : [{ label: element.textContent?.trim(), left: bounds.left, right: bounds.right }];
  });
}

async function expectContainedAction(page: Page, button: Locator): Promise<void> {
  await expect(button).toBeVisible();
  await page.evaluate(() => document.fonts.ready);
  for (const width of [390, 768, 900, 1440]) {
    await page.setViewportSize({ width, height: 900 });
    await expect
      .poll(() => button.evaluateAll(actionContainmentFailures), {
        message: `icon-only action outside its cell at ${width}px`,
      })
      .toEqual([]);
  }
}

for (const locale of LOCALES) {
  for (const theme of THEMES) {
    test.describe(`Short actions (${locale}, ${theme})`, () => {
      test.beforeEach(async ({ page }) => seedStoredTheme(page, theme));

      test('quota project and state View labels fit on one line', async ({ page }) => {
        await page.goto(
          withVariants(localePath(locale, `/orgs/${ORG_ACME}/settings/quota-retention`), [
            SETTINGS_ADMIN_GRANTS,
            '?fixture.variant.getUsageSummary=per-project',
          ]),
        );
        const project = page.locator('[data-quota-projects] [data-quota-view]:visible');
        const state = page.locator(
          'table[aria-labelledby="quota-state-title"] [data-quota-view]:visible',
        );
        await expectSingleLineActions(page, project);
        await expectSingleLineActions(page, state);
      });

      test('onboarding resource and credential View labels fit on one line', async ({ page }) => {
        await page.goto(onboardingUrl(locale, 'onboarding-token'));
        await expectCurrentStep(page, 3);
        await page.getByTestId('onboarding-back').click();
        await expectCurrentStep(page, 2);
        await expect(page.getByTestId('connect-token-row')).toBeVisible();
        await expect(page.getByTestId('onboarding-evidence-panel')).toBeVisible();
        await expectSingleLineActions(page, page.locator('button[data-onboarding-record]:visible'));
      });

      test('run ID yields space to its one-line Copy label', async ({ page }) => {
        await page.goto(runDetailPath({ locale }));
        const copy = page.locator('dd[data-run-id] app-copy-value-button button');
        await expect(copy).toHaveText(locale === 'vi' ? 'Sao chép' : 'Copy');
        await expect(copy).toHaveCSS('white-space', 'nowrap');
        const id = page.locator('dd[data-run-id] bdi');
        // Stress only the display: the fixture service's real run ID must remain a valid UUID.
        const longId = 'a'.repeat(128);
        await id.evaluate((node, value) => {
          node.textContent = value;
        }, longId);
        await expectContainedAction(page, copy);
        await page.setViewportSize({ width: 390, height: 900 });
        await expect(id).toHaveText(longId);
        await expect
          .poll(async () => {
            const idEnd = await id.evaluate((node) => node.getBoundingClientRect().right);
            const copyStart = await copy.evaluate((node) => node.getBoundingClientRect().left);
            return idEnd - copyStart;
          })
          .toBeLessThanOrEqual(0);
      });
    });
  }
}
