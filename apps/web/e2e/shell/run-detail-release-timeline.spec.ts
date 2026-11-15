import { expect, test, type Locator, type Page } from '@playwright/test';
import { expectNoA11yViolations } from '../a11y/axe-scan';
import { activeElement, LOCALES, type ShellLocale } from './support';
import { runDetailPath } from './run-detail.support';
import { tabThroughRunControls, tabThroughTableScroller } from './run-table-keyboard.support';

const COPY: Record<
  ShellLocale,
  { timeline: string; release: string; note: string; unbound: string; selector: string }
> = {
  en: {
    timeline: 'Run state timeline',
    release: 'Release',
    note: 'Events that arrive after cancel are kept as evidence and never complete the run.',
    unbound: 'No release bound to this run',
    selector: 'Releases bound to this run',
  },
  vi: {
    timeline: 'Dòng thời gian trạng thái lượt chạy',
    release: 'Bản phát hành',
    note: 'Các sự kiện đến sau khi hủy vẫn được giữ làm bằng chứng và không bao giờ làm lượt chạy hoàn tất.',
    unbound: 'Không có bản phát hành nào gắn với lượt chạy này',
    selector: 'Các bản phát hành gắn với lượt chạy này',
  },
};

async function ready(page: Page): Promise<void> {
  await expect(page.locator('app-run-detail-page')).toHaveAttribute(
    'data-run-detail-status',
    'ready',
  );
}

async function expectDigest(release: Locator, value: string): Promise<void> {
  const field = release.locator('[data-release-digest]');
  const disclosure = field.locator('button[aria-haspopup="dialog"]');
  if (await disclosure.count()) {
    await disclosure.click();
    const dialog = release.page().getByRole('dialog');
    await expect(dialog.locator('code.full-value bdi')).toHaveText(value);
    await release.page().keyboard.press('Escape');
    await expect(dialog).not.toBeVisible();
    await expect(disclosure).toBeFocused();
  } else {
    await expect(field.locator('code.full-value bdi')).toHaveText(value);
  }
}

async function expectActiveOption(selector: Locator, option: Locator): Promise<void> {
  const id = await option.getAttribute('id');
  if (id === null) throw new Error('Release option has no active-descendant id');
  await expect(selector).toHaveAttribute('aria-activedescendant', id);
}

async function openReleaseChooser(page: Page, selector: Locator): Promise<void> {
  await expect(selector).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(selector).toHaveAttribute('aria-expanded', 'true');
  const listbox = page.getByRole('listbox');
  await expect(listbox).toBeVisible();
  await expect(listbox.getByRole('option')).toHaveCount(2);
  // Wait for Brain's post-render selected-item activation before navigating.
  await expectActiveOption(selector, listbox.getByRole('option', { selected: true }));
}

for (const locale of LOCALES) {
  test(`a canceled run shows one current step, the after-cancel note and no bound release (${locale})`, async ({
    page,
  }) => {
    await page.goto(runDetailPath({ locale, run: 'canceled' }));
    await ready(page);
    const copy = COPY[locale];
    const timeline = page.getByRole('region', { name: copy.timeline });
    await expect(timeline.locator('[data-timeline-steps] > li')).toHaveCount(6);
    await expect(timeline.locator('[aria-current="step"]')).toHaveCount(1);
    await expect(timeline.locator('[aria-current="step"]')).toHaveAttribute(
      'data-step',
      'canceled',
    );
    await expect(timeline.locator('[data-step="complete"]')).toHaveAttribute(
      'data-status',
      'not-reached',
    );
    const reached = await timeline
      .locator('[data-status="reached"]')
      .evaluateAll((steps) => steps.map((step) => step.getAttribute('data-step')));
    expect(reached).not.toContain('complete');
    await expect(timeline.locator('[data-after-cancel-note]')).toContainText(copy.note);
    await expect(timeline.locator('[data-after-cancel-count]')).not.toContainText('events arrived');
    const release = page.getByRole('region', { name: copy.release, exact: true });
    await expect(release.locator('[data-release-unbound]')).toContainText(copy.unbound);
    await expect(release.locator('button')).toHaveCount(0);
  });

  test(`a bound run loads release components without a single-release selector (${locale})`, async ({
    page,
  }) => {
    await page.goto(runDetailPath({ locale, run: 'release' }));
    await ready(page);
    const release = page.getByRole('region', { name: COPY[locale].release, exact: true });
    await expect(release.locator('[data-release-name]')).toContainText('Storefront release');
    await expect(release.locator('[data-release-component]')).toHaveCount(1);
    await expectDigest(release, `sha256:${'a'.repeat(64)}`);
    await expect(release.getByRole('combobox')).toHaveCount(0);
    await expect(release.locator('[data-release-unbound]')).toHaveCount(0);
    await expectNoA11yViolations(page, `bound release ${locale}`);
  });

  test(`two distinct releases select only their own components (${locale})`, async ({ page }) => {
    await page.goto(runDetailPath({ locale, run: 'releases' }));
    await ready(page);
    const release = page.getByRole('region', { name: COPY[locale].release, exact: true });
    const selector = release.getByRole('combobox', { name: COPY[locale].selector });
    await selector.click();
    const options = page.getByRole('option');
    await expect(options).toHaveCount(2);
    await expect(options.nth(0)).toHaveAttribute('aria-selected', 'true');
    await expect(options.nth(1)).toHaveAttribute('aria-selected', 'false');
    const name = await options.nth(1).textContent();
    await options.nth(1).click();
    await expect(selector).toHaveText(name ?? '');
    await expect(release.locator('[data-release-name]')).toContainText('API release');
    await expect(release.locator('[data-release-component]')).toHaveCount(1);
    await expectDigest(release, `sha256:${'b'.repeat(64)}`);
  });
}

for (const locale of LOCALES) {
  test(`release typeahead searches the visible name rather than the id (${locale})`, async ({
    page,
  }) => {
    await page.goto(runDetailPath({ locale, run: 'releases' }));
    await ready(page);
    const release = page.locator('app-release-panel');
    const selector = release.getByRole('combobox');
    await selector.focus();
    await openReleaseChooser(page, selector);
    const target = page.getByRole('option', { name: /^API release/ });
    await expect(target).toHaveAttribute('data-value', '018f0000-0000-7000-8000-000000000092');
    await page.keyboard.type('API');
    await expectActiveOption(selector, target);
    await expect(release.locator('[data-release-name]')).toContainText('Storefront');
    await page.keyboard.press('Enter');
    await expect(selector).toHaveAttribute('aria-expanded', 'false');
    await expect(selector).toBeFocused();
    await expect(release.locator('[data-release-name]')).toContainText('API release');
  });
}

test('the unbound regions add no keyboard stops', async ({ page }) => {
  await page.goto(runDetailPath({ run: 'canceled' }));
  await ready(page);
  await expect(page.locator('app-run-timeline button, app-release-panel button')).toHaveCount(0);
  const manifest = page.locator('app-manifest-panel');
  const copies = manifest.locator('app-copy-value-button button');
  await expect(copies).toHaveCount(3);
  await copies.first().focus();
  for (const copy of (await copies.all()).slice(1)) {
    await page.keyboard.press('Tab');
    await expect(copy).toBeFocused();
  }
  const producers = page.locator('#producers');
  await tabThroughTableScroller(page, producers);
  await tabThroughRunControls(page, producers, 2);
  await page.keyboard.press('Tab');
  expect((await activeElement(page)).tag).toBe('body');
  const producerCopies = producers.locator('app-copy-value-button button');
  for (const copy of (await producerCopies.all()).reverse()) {
    await page.keyboard.press('Shift+Tab');
    await expect(copy).toBeFocused();
  }
  const scroller = producers.locator('.scroll[tabindex="0"]');
  if (await scroller.count()) {
    await page.keyboard.press('Shift+Tab');
    await expect(scroller).toBeFocused();
  }
  await page.keyboard.press('Shift+Tab');
  await expect(copies.last()).toBeFocused();
  await expectNoA11yViolations(page, 'unbound release and timeline');
});

test('the compact release chooser and both identifier actions preserve keyboard reachability', async ({
  page,
}) => {
  await page.goto(runDetailPath({ run: 'releases' }));
  await ready(page);
  const release = page.locator('app-release-panel');
  const selector = release.getByRole('combobox');
  await expect(release.locator('[data-release-digest]')).toBeVisible();
  const manifestCopies = page.locator('app-manifest-panel app-copy-value-button button');
  await expect(manifestCopies).toHaveCount(3);
  await manifestCopies.first().focus();
  for (const copy of (await manifestCopies.all()).slice(1)) {
    await page.keyboard.press('Tab');
    await expect(copy).toBeFocused();
  }
  await tabThroughTableScroller(page, page.locator('#producers'));
  await tabThroughRunControls(page, page.locator('#producers'), 2);
  await page.keyboard.press('Tab');
  await expect(selector).toBeFocused();
  await openReleaseChooser(page, selector);
  const options = page.getByRole('option');
  await page.keyboard.press('End');
  await expectActiveOption(selector, options.last());
  await page.keyboard.press('Enter');
  await expect(selector).toHaveAttribute('aria-expanded', 'false');
  await expect(selector).toBeFocused();
  await expect(release.locator('[data-release-name]')).toContainText('API release');
  const identifierControls = release.locator('app-copyable-identifier .actions button');
  await expect(identifierControls).toHaveCount(4);
  await expect(release.locator('app-copy-value-button button')).toHaveCount(2);
  for (const control of await identifierControls.all()) {
    await page.keyboard.press('Tab');
    await expect(control).toBeFocused();
    await expect(control).toHaveAccessibleName(/\S/);
  }
  await page.keyboard.press('Tab');
  expect((await activeElement(page)).tag).toBe('body');
  await expectNoA11yViolations(page, 'two-release selector');
});

for (const status of ['forbidden', 'not-found'] as const) {
  test(`release ${status} stays inside a ready run`, async ({ page }) => {
    await page.goto(runDetailPath({ run: 'release-refused', release: status }));
    await ready(page);
    await expect(page.locator(`app-release-panel app-${status}-pattern`)).toBeVisible();
    await expect(page.locator('app-manifest-panel')).toBeVisible();
    await expect(page.locator('app-run-timeline')).toBeVisible();
  });
}

for (const locale of LOCALES) {
  test(`at 320px full release names remain recoverable in a compact chooser (${locale})`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 320, height: 800 });
    await page.goto(runDetailPath({ locale, run: 'releases' }));
    await ready(page);
    await expect(page.locator('[data-release-digest]')).toBeVisible();
    for (const region of [page.locator('app-run-timeline'), page.locator('app-release-panel')]) {
      expect(await region.evaluate((element) => element.scrollWidth <= element.clientWidth)).toBe(
        true,
      );
    }
    const selector = page.locator('app-release-panel').getByRole('combobox');
    await expect(selector).toHaveCount(1);
    const box = await selector.boundingBox();
    expect(box?.height).toBe(44);
    await selector.click();
    const options = page.getByRole('option');
    await expect(options).toHaveCount(2);
    const selectedName = await options.nth(1).textContent();
    await options.nth(1).click();
    await expect(selector).toHaveText(selectedName ?? '');
    await expect(page.locator('#release-choice-name')).toHaveText(selectedName ?? '');
    expect(
      await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth),
    ).toBe(true);
  });
}

for (const locale of LOCALES) {
  test(`an unbound run makes no release read at 320px (${locale})`, async ({ page }) => {
    await page.setViewportSize({ width: 320, height: 800 });
    await page.goto(runDetailPath({ locale, run: 'canceled' }));
    await ready(page);
    await expect(page.locator('app-release-panel [data-release-unbound]')).toHaveText(
      COPY[locale].unbound,
    );
    const reads = await page.evaluate(() =>
      (window.__evidgateFixtureRequests ?? []).filter((request) =>
        new URL(request.url).pathname.includes('/releases/'),
      ),
    );
    expect(reads).toEqual([]);
    await expect(page.locator('app-release-panel button')).toHaveCount(0);
  });

  test(`an unretryable release load error hides components but preserves the run (${locale})`, async ({
    page,
  }) => {
    await page.goto(
      `${runDetailPath({ locale, run: 'release-refused' })}&fixture.variant.getRelease=missing`,
    );
    await ready(page);
    await expect(page.locator('app-release-panel app-error-pattern')).toBeVisible();
    await expect(page.locator('app-release-panel [data-release-component]')).toHaveCount(0);
    await expect(page.locator('app-release-panel [data-release-unbound]')).toHaveCount(0);
    await expect(page.locator('app-manifest-panel')).toBeVisible();
    await expect(page.locator('app-release-panel app-error-pattern button')).toHaveCount(0);
    await expectNoA11yViolations(page, `release load error ${locale}`);
  });

  test(`two releases switch back by keyboard without duplicate reads at 320px (${locale})`, async ({
    page,
  }, testInfo) => {
    await page.setViewportSize({ width: 320, height: 800 });
    await page.goto(runDetailPath({ locale, run: 'releases' }));
    await ready(page);
    const release = page.locator('app-release-panel');
    const selector = release.getByRole('combobox');
    const digest = release.locator('[data-release-digest] code.full-value bdi');
    const options = page.getByRole('option');
    await expect(digest).toHaveText(`sha256:${'a'.repeat(64)}`);
    await expect(selector).toHaveCount(1);
    await selector.focus();
    await openReleaseChooser(page, selector);
    await page.keyboard.press('End');
    await expectActiveOption(selector, options.last());
    await page.keyboard.press('Enter');
    await expect(selector).toHaveAttribute('aria-expanded', 'false');
    await expect(selector).toBeFocused();
    await expect(digest).toHaveText(`sha256:${'b'.repeat(64)}`);
    await openReleaseChooser(page, selector);
    await page.keyboard.press('Home');
    await expectActiveOption(selector, options.first());
    await page.keyboard.press('Enter');
    await expect(selector).toHaveAttribute('aria-expanded', 'false');
    await expect(selector).toBeFocused();
    await expect(digest).toHaveText(`sha256:${'a'.repeat(64)}`);
    await openReleaseChooser(page, selector);
    await expect(options.nth(0)).toHaveAttribute('aria-selected', 'true');
    await expect(options.nth(1)).toHaveAttribute('aria-selected', 'false');
    await page.keyboard.press('Enter');
    await expect(selector).toHaveAttribute('aria-expanded', 'false');
    await expect(selector).toBeFocused();
    await expect(digest).toHaveText(`sha256:${'a'.repeat(64)}`);
    const reads = await page.evaluate(() =>
      (window.__evidgateFixtureRequests ?? [])
        .map((request) => new URL(request.url).pathname)
        .filter((path) => path.includes('/releases/')),
    );
    expect(reads).toEqual([
      '/v1/releases/018f0000-0000-7000-8000-000000000091',
      '/v1/releases/018f0000-0000-7000-8000-000000000092',
      '/v1/releases/018f0000-0000-7000-8000-000000000091',
    ]);
    expect(
      await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth),
    ).toBe(true);
    await release.screenshot({
      path: testInfo.outputPath(`release-components-${locale}-320px.png`),
    });
  });
}
