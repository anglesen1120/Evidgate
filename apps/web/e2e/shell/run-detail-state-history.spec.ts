// The run detail timeline is dated only by the served `state_history`, so these cases pin the two
// shapes a reviewer can reach: a run whose recorded transitions are served, and the legacy run the
// corpus keeps (a `null` history) which must say so instead of inventing times.
import { expect, test, type Page } from '@playwright/test';
import { expectNoA11yViolations } from '../a11y/axe-scan';
import { runDetailPath, type RunStateVariant } from './run-detail.support';
import { currentTheme, seedStoredTheme, THEMES } from './support';

/** The widths the timeline has to stay clean at: a desktop layout and the narrow reflow. */
const WIDTHS = [1280, 320] as const;

/** The transitions `detail-with-history` serves, oldest first; no step can carry another time. */
const SERVED_TIMES = [
  '2026-11-07T08:10:00Z',
  '2026-11-07T08:42:00Z',
  '2026-11-07T09:05:00Z',
] as const;

/** The note an unknown `state_history` shows in place of times. */
const HISTORY_UNAVAILABLE = 'History unavailable for this run';

async function openTimeline(page: Page, run: RunStateVariant): Promise<void> {
  await page.goto(runDetailPath({ run }));
  await expect(page.locator('app-run-detail-page')).toHaveAttribute(
    'data-run-detail-status',
    'ready',
  );
  await expect(page.locator('[data-run-timeline]')).toBeVisible();
}

/** The `datetime` of each dated step, in the order the served history renders them. */
async function datedSteps(page: Page): Promise<(string | null)[]> {
  return page
    .locator('[data-timeline-steps] [data-step-time] time')
    .evaluateAll((steps) => steps.map((step) => step.getAttribute('datetime')));
}

test('a served history dates its recorded steps and shows no unknown-history note', async ({
  page,
}) => {
  await openTimeline(page, 'with-history');
  const timeline = page.locator('[data-run-timeline]');

  // The note stands for an unknown or a known-empty history; a served one replaces it.
  await expect(timeline.locator('[data-step-times]')).toHaveCount(0);
  await expect(timeline).not.toContainText(HISTORY_UNAVAILABLE);

  const times = timeline.locator('[data-step-time] time');
  await expect(times).toHaveCount(SERVED_TIMES.length);
  expect(await datedSteps(page)).toEqual([...SERVED_TIMES]);

  // The creation row is never served, so the implicit first step stays reached without a time.
  await expect(timeline.locator('[data-step="created"] [data-step-time]')).toHaveCount(0);
  await expect(timeline.locator('[data-step="complete"] [data-step-time] time')).toHaveAttribute(
    'datetime',
    SERVED_TIMES[2],
  );

  // The app's date pipe formats the wire value; the raw ISO string would mean a second format.
  await expect(times.first()).not.toHaveText(SERVED_TIMES[0]);
  await expect(times.first()).toHaveText('11/7/2026, 8:10:00\u202fAM UTC');
});

test('an unknown history says so and dates no step', async ({ page }) => {
  await openTimeline(page, 'ready');
  const timeline = page.locator('[data-run-timeline]');

  await expect(timeline.locator('[data-step-times]')).toHaveText(HISTORY_UNAVAILABLE);
  await expect(timeline.locator('[data-step-time]')).toHaveCount(0);
});

for (const theme of THEMES) {
  for (const width of WIDTHS) {
    test(`the timeline and its history note pass axe in ${theme} at ${width}px`, async ({
      page,
    }) => {
      await page.setViewportSize({ width, height: 900 });
      await page.emulateMedia({ colorScheme: theme });
      await seedStoredTheme(page, theme);

      await openTimeline(page, 'with-history');
      expect(await currentTheme(page)).toBe(theme);
      await expectNoA11yViolations(page, `run detail timeline ${theme}/${width}`);

      // The note instead of the times: its pill is a separate contrast and target case.
      await openTimeline(page, 'ready');
      await expect(page.locator('[data-run-timeline] [data-step-times]')).toHaveText(
        HISTORY_UNAVAILABLE,
      );
      await expectNoA11yViolations(page, `run detail unknown history ${theme}/${width}`);
    });
  }
}
