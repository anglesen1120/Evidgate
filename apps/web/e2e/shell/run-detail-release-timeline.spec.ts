// The run state timeline and the release region on the real page: the canceled run is the state
// that shows both the informational after-cancel note and the release's shared unavailable state,
// because `GET /v1/runs/{r}` serves no release id and no transition history.
import { expect, test, type Page } from '@playwright/test';
import { activeElement, expectNoA11yViolations, LOCALES, type ShellLocale } from './support';
import { runDetailPath } from './run-detail.support';

/** The copy that identifies each region, per locale bundle. */
const COPY: Record<
  ShellLocale,
  { timeline: string; release: string; note: string; unavailable: string; unbound: string }
> = {
  en: {
    timeline: 'Run state timeline',
    release: 'Release',
    note: 'Events that arrive after cancel are kept as evidence and never complete the run.',
    unavailable: 'This run does not name a release',
    unbound: 'No release bound to this run',
  },
  vi: {
    timeline: 'Dòng thời gian trạng thái lượt chạy',
    release: 'Bản phát hành',
    note: 'Các sự kiện đến sau khi hủy vẫn được giữ làm bằng chứng và không bao giờ làm lượt chạy hoàn tất.',
    unavailable: 'không nêu bản phát hành',
    unbound: 'Không có bản phát hành nào gắn với lần chạy này',
  },
};

async function ready(page: Page): Promise<void> {
  await expect(page.locator('app-run-detail-page')).toHaveAttribute(
    'data-run-detail-status',
    'ready',
  );
}

for (const locale of LOCALES) {
  test(`a canceled run shows one current step, the after-cancel note and no bound release (${locale})`, async ({
    page,
  }) => {
    await page.goto(runDetailPath({ locale, run: 'canceled' }));
    await ready(page);
    const copy = COPY[locale];

    const timeline = page.getByRole('region', { name: copy.timeline });
    await expect(timeline).toBeVisible();
    // The main path's five steps and the canceled terminal step; one of them is the current one.
    await expect(timeline.locator('[data-timeline-steps] > li')).toHaveCount(6);
    const current = timeline.locator('[aria-current="step"]');
    await expect(current).toHaveCount(1);
    await expect(current).toHaveAttribute('data-step', 'canceled');
    await expect(current).toHaveAttribute('data-status', 'current');

    // Cancellation ends the run anywhere on the path, so no step is claimed as complete.
    await expect(timeline.locator('[data-step="complete"]')).toHaveAttribute(
      'data-status',
      'not-reached',
    );
    const reached = await timeline
      .locator('[data-status="reached"]')
      .evaluateAll((steps) => steps.map((step) => step.getAttribute('data-step')));
    expect(reached).not.toContain('complete');

    await expect(timeline.locator('[data-after-cancel-note]')).toContainText(copy.note);
    // The response serves no after-cancel count, so the region reports none.
    await expect(timeline.locator('[data-after-cancel-count]')).not.toContainText('events arrived');

    const release = page.getByRole('region', { name: copy.release });
    await expect(release).toBeVisible();
    await expect(release.locator('[data-release-unavailable]')).toContainText(copy.unavailable);
    await expect(release.locator('[data-release-unbound]')).toHaveCount(0);
    await expect(page.locator('app-run-detail-page')).not.toContainText(copy.unbound);
  });
}

test('the two regions add no keyboard stop and keep their own name', async ({ page }) => {
  await page.goto(runDetailPath({ run: 'canceled' }));
  await ready(page);
  const timeline = page.getByRole('region', { name: COPY.en.timeline });
  const release = page.getByRole('region', { name: COPY.en.release });

  // Nothing inside either region is interactive, so the walk passes straight from the manifest
  // panel's copy button to the end of the page and back.
  await expect(timeline.locator('a, button, input, select, textarea, [tabindex="0"]')).toHaveCount(
    0,
  );
  await expect(release.locator('a, button, input, select, textarea, [tabindex="0"]')).toHaveCount(
    0,
  );
  const copyHash = page.getByRole('button', { name: 'Copy full manifest hash' });
  await copyHash.focus();
  await expect(copyHash).toBeFocused();
  await page.keyboard.press('Tab');
  expect((await activeElement(page)).tag).toBe('body');
  await page.keyboard.press('Shift+Tab');
  await expect(copyHash).toBeFocused();

  await expectNoA11yViolations(page, 'run detail, release and timeline regions');
});

test('at 320px both regions wrap instead of scrolling the page sideways', async ({ page }) => {
  await page.setViewportSize({ width: 320, height: 800 });
  await page.goto(runDetailPath({ run: 'canceled' }));
  await ready(page);
  const timeline = page.getByRole('region', { name: COPY.en.timeline });
  const release = page.getByRole('region', { name: COPY.en.release });

  for (const region of [timeline, release]) {
    await expect(region).toBeVisible();
    expect(
      await region.evaluate((element) => element.scrollWidth <= element.clientWidth),
      'a region overflows its own column at 320px',
    ).toBe(true);
  }
  expect(
    await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth),
    'the page scrolls sideways at 320px',
  ).toBe(true);
});
