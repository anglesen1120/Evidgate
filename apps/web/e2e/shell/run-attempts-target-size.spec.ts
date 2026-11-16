import { expect, test, type Locator, type Page } from '@playwright/test';
import attemptsFixture from '../../../../fixtures/web-api/run-attempts/input.json';

import { axeScan, expectNoA11yViolations } from '../a11y/axe-scan';
import { runDetailPath } from './run-detail.support';
import { LOCALES, seedStoredTheme, THEMES, type ShellLocale, type ShellTheme } from './support';

const failedAttempt = attemptsFixture.body.items.find(
  (attempt) => attempt.normalized_status === 'failed',
)!;

async function targetGeometry(target: Locator) {
  await target.scrollIntoViewIfNeeded();
  return target.evaluate((target) => {
    const identity = target.closest('[data-attempt-identity]')!;
    const number = identity.querySelector('.attempt-number')!;
    const row = identity.firstElementChild!;
    const box = target.getBoundingClientRect();
    const rowBox = row.getBoundingClientRect();
    const numberBox = number.getBoundingClientRect();
    const hits = [0.25, 0.5, 0.75].flatMap((x) =>
      [0.25, 0.5, 0.75].map((y) => {
        const hit = document.elementFromPoint(box.left + box.width * x, box.top + box.height * y);
        return hit !== null && target.contains(hit);
      }),
    );
    return {
      width: box.width,
      height: box.height,
      rowBottom: rowBox.bottom,
      bottom: box.bottom,
      numberTop: numberBox.top,
      hits,
    };
  });
}

async function openEnlargedAttempts(
  page: Page,
  locale: ShellLocale,
  theme: ShellTheme,
  fontSize: '100%' | '200%' = '200%',
) {
  await seedStoredTheme(page, theme);
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto(runDetailPath({ locale, decision: 'evaluated-fail-missing-shard' }));
  await expect(page.locator('app-run-detail-page')).toHaveAttribute(
    'data-run-detail-status',
    'ready',
  );
  await page.evaluate(() => document.fonts.ready);
  await page.addStyleTag({
    content: `
      [data-attempt-identity] app-data-table-value {
        font-family: monospace;
        font-size: ${fontSize};
        line-height: 1.5;
        letter-spacing: ${fontSize === '100%' ? '0.5em' : '0.12em'};
        word-spacing: 0.16em;
      }
    `,
  });
  await expect(
    page.locator('[data-attempt-identity] app-data-table-value .value').first(),
  ).toHaveClass(/clipped/);
  await page.locator('app-run-detail-page').getByRole('tab').nth(1).click();
  await page.mouse.move(0, 0);
}

async function placeUnderTopbar(target: Locator) {
  return target.evaluate((target) => {
    const scroller = document.scrollingElement!;
    const bar = document.querySelector('header.topbar')!;
    const barBottom = bar.getBoundingClientRect().bottom;
    scroller.scrollTop += target.getBoundingClientRect().bottom - barBottom - 13;
    const box = target.getBoundingClientRect();
    const hit = document.elementFromPoint(box.x + box.width / 2, box.y + box.height / 4);
    return {
      visibleHeight: box.bottom - barBottom,
      coveredByTopbar: hit !== null && hit.closest('header.topbar') !== null,
    };
  });
}

for (const locale of LOCALES) {
  for (const theme of THEMES) {
    test(`axe starts at a stable origin after a clipped attempt is scrolled under the bar (${locale}/${theme})`, async ({
      page,
    }) => {
      await openEnlargedAttempts(page, locale, theme, '100%');
      const target = page.locator(
        `#attempt-${failedAttempt.execution_key}-${failedAttempt.attempt_no} [data-attempt-identity] app-copy-value-button button`,
      );
      const covered = await placeUnderTopbar(target);
      expect(covered.visibleHeight).toBeGreaterThanOrEqual(12);
      expect(covered.visibleHeight).toBeLessThanOrEqual(14);
      expect(covered.coveredByTopbar).toBe(true);

      await expectNoA11yViolations(page, `enlarged attempt after Decision ${locale}/${theme}`);

      expect(await page.evaluate(() => scrollY)).toBe(0);
      await expect(page.locator('app-run-detail-page').getByRole('tab').nth(1)).toHaveAttribute(
        'aria-selected',
        'true',
      );
    });

    test(`enlarged attempt value and copy controls never overlap their record (${locale}/${theme})`, async ({
      page,
    }) => {
      await openEnlargedAttempts(page, locale, theme);
      for (const width of [1280, 768, 390]) {
        await page.setViewportSize({ width, height: 900 });
        const identities = page.locator('[data-attempt-identity]');
        for (const identity of await identities.all()) {
          const targets = identity.locator(
            'app-data-table-value .value, app-copy-value-button button',
          );
          for (const target of await targets.all()) {
            const geometry = await targetGeometry(target);
            const message = JSON.stringify(geometry);
            expect(geometry.width, message).toBeGreaterThanOrEqual(24);
            expect(geometry.height, message).toBeGreaterThanOrEqual(24);
            expect(geometry.bottom, message).toBeLessThanOrEqual(geometry.rowBottom + 1);
            expect(geometry.bottom, message).toBeLessThanOrEqual(geometry.numberTop + 1);
            expect(geometry.hits, message).toEqual(Array(9).fill(true));
          }
        }
      }
    });
  }
}

test('normalizing scan scroll does not hide a genuinely overlapping copy control', async ({
  page,
}) => {
  await openEnlargedAttempts(page, 'en', 'light', '100%');
  const button = page.locator('[data-attempt-identity] app-copy-value-button button').first();
  const status = await button.getAttribute('aria-describedby');
  await button.evaluate((target) => {
    const host = target.parentElement!;
    const box = target.getBoundingClientRect();
    const parent = host.getBoundingClientRect();
    host.style.position = 'relative';
    const overlay = document.createElement('button');
    overlay.type = 'button';
    overlay.setAttribute('aria-label', 'Overlapping test action');
    Object.assign(overlay.style, {
      position: 'absolute',
      padding: '0',
      border: '0',
      top: `${box.top - parent.top}px`,
      left: `${box.left - parent.left}px`,
      width: `${box.width}px`,
      height: `${box.height - 13}px`,
      background: 'currentColor',
      zIndex: '1',
    });
    host.append(overlay);
  });

  const results = await axeScan(page);
  const targetSize = results.violations.find((rule) => rule.id === 'target-size');
  expect(targetSize?.nodes.some((node) => node.html.includes(`aria-describedby="${status}"`))).toBe(
    true,
  );
  expect(await page.evaluate(() => scrollY)).toBe(0);
});
