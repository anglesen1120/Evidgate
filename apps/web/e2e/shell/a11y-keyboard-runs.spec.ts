// 09.8.1-T1.2 (story 09.8.1 AC2): the Runs flow with the keyboard alone, on the real fixtures
// build. Every step is a `page.keyboard` press — no `click`, no `force` — and every assertion names
// the element that holds focus by its own identity (role + accessible name, id, data hook), never by
// counting Tab stops.
//
// Runs uses named local scrollers only when content actually overflows, bounded rich-row
// pages, and explicit copy/full-value controls. The walk keeps those approved controls in
// DOM order, verifies their exact navigation/copy budget, and never permits anonymous stops.
import { expect, test, type Locator, type Page } from '@playwright/test';
import runList from '../../../../fixtures/web-api/run-list/input.json';
import runListPage2 from '../../../../fixtures/web-api/run-list-page-2/input.json';
import { expectNoA11yViolations } from '../a11y/axe-scan';
import { activeElement, localePath, ORG_ACME, PROJECT_STOREFRONT, withVariants } from './support';
import { RUN_FILTER_SUMMARY, RUN_RECORDS } from './run-list.support';
import {
  auditRunTabStops,
  tabThroughNarrowRunRecords,
  tabThroughRunControls,
  tabThroughTableScroller,
} from './run-table-keyboard.support';

/** The grant set that holds `run:read`; the default G1 session grants no run code. */
const RUN_READ_GRANT = '?fixture.variant.listPermissions=settings-policy-readonly';
/** The fixture's first row — the row link's accessible name is the whole id, not its abbreviation. */
const FIRST_RUN = runList.body.items[0]!.run_id;
/** The form controls in DOM order, between the route's region and the table. */
const FILTER_STOPS = [
  '#run-state',
  '#run-decision',
  '#run-scope',
  '#run-ref',
  '[data-run-apply]',
  '[data-run-clear]',
] as const;

/** The corpus's first page at the wire's own page size (`run-list-page-1-full`): 50 rows + a cursor. */
const FULL_PAGE_ROWS = 50;

/** `/dev/run-list/orgs/:orgId/projects/:projectId` plus the grant and one case variant. */
function listAddress(variant: string): string {
  return withVariants(
    localePath('en', `/dev/run-list/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}`),
    [RUN_READ_GRANT, variant],
  );
}

async function listIsReady(page: Page): Promise<void> {
  await expect(page.locator('app-run-list-page')).toHaveAttribute('data-run-list-status', 'ready');
}

/**
 * The focus ring every stop must show: the token pair the page styles its own controls with
 * (`--eg-focus-width` / `--eg-focus` on the light theme, which is this suite's default).
 */
async function expectFocusRing(target: Locator, offset: string): Promise<void> {
  await expect(target).toHaveCSS('outline-width', '2px');
  await expect(target).toHaveCSS('outline-style', 'solid');
  await expect(target).toHaveCSS('outline-color', 'rgb(0, 107, 102)');
  await expect(target).toHaveCSS('outline-offset', offset);
}

/** Skip link, then the route's own region: the first two stops of every page, by keyboard only. */
async function enterMain(page: Page): Promise<void> {
  await page.keyboard.press('Tab');
  await expect(page.locator('a.skip-link')).toBeFocused();
  await expect(page.locator('a.skip-link')).toHaveAccessibleName('Skip to main content');
  await page.keyboard.press('Enter');
  await expect(page.locator('#main-content')).toBeFocused();
}

/** The native disclosure is an intentional stop before the six filter controls. */
async function enterFilterDisclosure(page: Page): Promise<void> {
  await page.keyboard.press('Tab');
  const summary = page.locator(RUN_FILTER_SUMMARY);
  await expect(summary).toBeFocused();
  await expectFocusRing(summary, '2px');
  const disclosure = page.locator('[data-run-filter-disclosure]');
  if ((await disclosure.getAttribute('open')) === null) await page.keyboard.press('Enter');
  await expect(disclosure).toHaveAttribute('open', '');
}

test('the list, a row and the way back are keyboard-reachable and identifiable', async ({
  page,
}) => {
  await page.goto(listAddress('?fixture.variant.listRuns=runs'));
  await listIsReady(page);
  await enterMain(page);
  await enterFilterDisclosure(page);
  await auditRunTabStops(page.locator('app-run-list-page'), 'list-to-detail/list');

  for (const selector of FILTER_STOPS) {
    await page.keyboard.press('Tab');
    await expect(page.locator(selector)).toBeFocused();
    await expectFocusRing(page.locator(selector), '2px');
  }

  // Overflow adds exactly one named local-scroller stop, never a redundant wrapper stop.
  await tabThroughTableScroller(page, page.locator('app-run-list-table'));

  await page.keyboard.press('Tab');
  const firstRow = page.getByRole('link', { name: FIRST_RUN });
  await expect(firstRow).toBeFocused();
  expect((await firstRow.boundingBox())?.height ?? 0).toBeGreaterThanOrEqual(44);

  await page.keyboard.press('Enter');
  await expect.poll(() => new URL(page.url()).pathname.endsWith(`/${FIRST_RUN}`)).toBe(true);
  await expect(page.locator('app-run-detail-page')).toHaveAttribute(
    'data-run-detail-status',
    'ready',
  );
  await auditRunTabStops(page.locator('app-run-detail-page'), 'list-to-detail/detail-evidence');
  // A path change hands focus to the route's own region, so the visitor starts inside the page.
  expect((await activeElement(page)).id).toBe('main-content');

  await page.keyboard.press('Tab');
  const back = page.getByRole('link', { name: 'Back to runs' });
  await expect(back).toBeFocused();
  expect((await back.boundingBox())?.height ?? 0).toBeGreaterThanOrEqual(44);

  await tabThroughRunControls(page, page.locator('app-run-header').first(), 2);
  await page.keyboard.press('Tab');
  const attemptsRegion = page.getByRole('region', { name: 'Attempts table' });
  await expect(attemptsRegion).toBeFocused();
  await expect(page.locator('app-attempts-table [data-attempt-row]')).toHaveCount(7);
  await tabThroughRunControls(page, page.locator('app-attempts-table tbody'), 7);

  // Evidence and the latest live decision are tabs: only the selected tab is a Tab stop, the arrow
  // keys move inside the list, and the selected panel is the next stop. The hidden panel leaves the
  // order, so the walk never lands on it.
  await page.keyboard.press('Tab');
  const evidenceTab = page.getByRole('tab', { name: 'Evidence' });
  await expect(evidenceTab).toBeFocused();
  await page.keyboard.press('ArrowRight');
  const decisionTab = page.getByRole('tab', { name: 'Latest live decision' });
  await expect(decisionTab).toBeFocused();
  await expect(decisionTab).toHaveAttribute('aria-selected', 'true');
  const decisionPanel = page.getByRole('tabpanel');
  await expect(decisionPanel).toHaveAttribute('tabindex', '0');
  await expect(page.locator('[role="tabpanel"][hidden]')).toHaveAttribute('tabindex', '-1');
  await page.keyboard.press('Tab');
  await expect(decisionPanel).toBeFocused();
  await expectFocusRing(decisionPanel, '2px');

  // Back to evidence: selection and the roving tabindex follow the arrow in the render, and the
  // panel stops change over with it.
  await page.keyboard.press('Shift+Tab');
  await expect(decisionTab).toBeFocused();
  await page.keyboard.press('ArrowLeft');
  await expect(evidenceTab).toBeFocused();
  await expect(evidenceTab).toHaveAttribute('aria-selected', 'true');
  await expect(decisionTab).toHaveAttribute('tabindex', '-1');
  await expect(page.locator('[role="tabpanel"][hidden]')).toHaveAttribute('tabindex', '-1');
  await page.keyboard.press('Tab');
  const evidencePanel = page.getByRole('tabpanel');
  await expect(evidencePanel).toBeFocused();
  await expect(evidencePanel).toHaveAttribute('tabindex', '0');

  // The revision picker precedes the peer timeline/context area, then manifest provenance.
  await page.keyboard.press('Tab');
  await expect(page.getByRole('combobox', { name: 'Evidence revision' })).toBeFocused();
  await expect(page.locator('app-evidence-panel app-copy-value-button button')).toHaveCount(0);
  await tabThroughRunControls(page, page.locator('app-run-header').last(), 4);
  await tabThroughRunControls(page, page.locator('app-manifest-panel'), 3);
  const copy = page.getByRole('button', { name: 'Copy full manifest hash' });
  expect((await copy.boundingBox())?.height ?? 0).toBeGreaterThanOrEqual(44);
  await tabThroughTableScroller(page, page.locator('#producers'));
  await tabThroughRunControls(page, page.locator('#producers'), 2);
  await page.keyboard.press('Tab');
  const digest = page.getByRole('button', {
    name: 'Copy full digest for component web',
    exact: true,
  });
  await expect(digest).toBeFocused();
  expect((await digest.boundingBox())?.height ?? 0).toBeGreaterThanOrEqual(44);
  await page.keyboard.press('Tab');
  expect((await activeElement(page)).tag).toBe('body');
  await expect(page.getByRole('dialog')).toHaveCount(0);
});

test('the selected panel precedes its three decision provenance copy controls', async ({
  page,
}) => {
  await page.goto(
    withVariants(
      localePath(
        'en',
        `/dev/run-detail/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}/runs/${FIRST_RUN}`,
      ),
      [
        RUN_READ_GRANT,
        '?fixture.variant.getRun=detail-ready',
        '?fixture.variant.listRunAttempts=ok',
        '?fixture.variant.getRunDecision=evaluated-fail-missing-shard',
        // The decision was evaluated at revision 2, so pinning there raises no mismatch action.
        '?evidence_revision=2',
      ],
    ),
  );
  await expect(page.locator('app-run-detail-page')).toHaveAttribute(
    'data-run-detail-status',
    'ready',
  );
  await page.getByRole('tab', { name: 'Evidence' }).focus();
  await page.keyboard.press('ArrowRight');
  const decision = page.getByRole('tab', { name: 'Latest live decision' });
  await expect(decision).toHaveAttribute('aria-selected', 'true');
  await expect(page.locator('[data-decision-mismatch]')).toHaveCount(0);
  await expect(page.locator('app-decision-view a[href]')).toHaveCount(0);
  await expect(page.locator('app-decision-view app-copy-value-button button')).toHaveCount(3);

  // The panel remains its own stop; its three provenance actions follow, and hidden evidence
  // never enters the order.
  const panel = page.getByRole('tabpanel');
  await expect(panel).toHaveCount(1);
  await expect(panel).toHaveAttribute('tabindex', '0');
  await auditRunTabStops(panel, 'decision-provenance-panel');
  await expect(page.locator('[role="tabpanel"][hidden]')).toHaveAttribute('tabindex', '-1');
  await page.keyboard.press('Tab');
  await expect(panel).toBeFocused();
  await expectFocusRing(panel, '2px');
  await tabThroughRunControls(page, page.locator('app-decision-view'), 3);
  const copies = page.locator('app-decision-view app-copy-value-button button');
  for (const target of [copies.nth(1), copies.first(), panel]) {
    await page.keyboard.press('Shift+Tab');
    await expect(target).toBeFocused();
  }
  await expectNoA11yViolations(page, 'run detail, decision panel and provenance controls');
});

test('a filter change keeps the focus that asked for it, and Clear restores the table order', async ({
  page,
}) => {
  await page.goto(listAddress('?fixture.variant.listRuns=runs'));
  await listIsReady(page);
  await enterMain(page);
  await enterFilterDisclosure(page);

  // The three selects are comboboxes reached and left by Tab alone; their option lists are a
  // portaled overlay opened with Enter or an arrow key, so the value this walk sets is the text
  // filter's — the one a keystroke sequence can type unambiguously.
  for (const selector of ['#run-state', '#run-decision', '#run-scope']) {
    await page.keyboard.press('Tab');
    await expect(page.locator(selector)).toBeFocused();
    await expectFocusRing(page.locator(selector), '2px');
  }
  await page.keyboard.press('Tab');
  const ref = page.locator('#run-ref');
  await expect(ref).toBeFocused();
  await page.keyboard.insertText('release/việt');

  await page.keyboard.press('Tab');
  const apply = page.locator('[data-run-apply]');
  await expect(apply).toBeFocused();
  await page.keyboard.press('Enter');
  await expect.poll(() => new URL(page.url()).searchParams.get('ref')).toBe('release/việt');
  // A query-only navigation describes a filter, not a new page: the control that asked keeps focus.
  await expect(apply).toBeFocused();

  // Shift+Tab reaches the value that was applied, unchanged, and Tab returns to the submit button.
  await page.keyboard.press('Shift+Tab');
  await expect(ref).toBeFocused();
  await expect(ref).toHaveValue('release/việt');
  await page.keyboard.press('Tab');
  await expect(apply).toBeFocused();

  await page.keyboard.press('Tab');
  const clear = page.locator('[data-run-clear]');
  await expect(clear).toBeFocused();
  await page.keyboard.press('Enter');
  await expect.poll(() => new URL(page.url()).searchParams.has('ref')).toBe(false);
  expect(new URL(page.url()).searchParams.has('state')).toBe(false);
  // Clearing a server filter re-reads the list; the walk resumes only once that read has settled,
  // because a Tab pressed during the swap would land on the table instance being replaced.
  await page.waitForLoadState('networkidle');
  await listIsReady(page);
  await expect(page.locator('app-run-list-table [data-run-row]')).toHaveCount(
    runList.body.items.length,
  );
  await expect(clear).toBeFocused();
  await auditRunTabStops(page.locator('app-run-list-page'), 'filter-clear');
  // The tab order survives the re-read, including only an actually overflowing scroller.
  await tabThroughTableScroller(page, page.locator('app-run-list-table'));
  await page.keyboard.press('Tab');
  await expect(page.locator('app-run-list-table [data-run-row] a').first()).toBeFocused();
  await expect(page.getByRole('dialog')).toHaveCount(0);
});

test('the 250-row narrow records keep every fact reachable and preserve focus on a page turn', async ({
  page,
}) => {
  await page.setViewportSize({ width: 800, height: 800 });
  await page.goto(listAddress('?fixture.variant.listRuns=250-rows'));
  await listIsReady(page);
  const records = page.locator(RUN_RECORDS);
  await expect(records).toHaveCount(200);
  await expect(records.first().locator('dt')).toHaveCount(7);
  await expect(records.first().locator('dd')).toHaveCount(7);
  await expect(page.locator('app-run-list-table .scroll[tabindex="0"]')).toHaveCount(0);
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(800);
  await enterMain(page);
  await enterFilterDisclosure(page);
  await auditRunTabStops(page.locator('app-run-list-page'), '250-records/first-page');
  for (const selector of FILTER_STOPS) {
    await page.keyboard.press('Tab');
    await expect(page.locator(selector)).toBeFocused();
  }
  await tabThroughNarrowRunRecords(page, page.locator('app-run-list-table .eg-table-records'), 200);
  const pager = page.locator('app-run-list-table nav');
  const previous = pager.getByRole('button').first();
  const next = pager.getByRole('button').last();
  await expect(previous).toBeDisabled();
  await page.keyboard.press('Tab');
  await expect(next).toBeFocused();
  await expect(next).toHaveAccessibleName(/\S/);
  await page.keyboard.press('Enter');
  await expect(records).toHaveCount(50);
  await expect(next).toBeDisabled();
  await auditRunTabStops(page.locator('app-run-list-page'), '250-records/last-page');
  await expect(previous).toBeFocused();
  expect((await activeElement(page)).tag).not.toBe('body');
  await page.keyboard.press('Shift+Tab');
  await expect(records.last().locator('app-copy-value-button button')).toBeFocused();
  await expect(records.last()).toBeInViewport();
  await expectNoA11yViolations(page, 'run list, 250 labelled records');
});

test('a retried 503 hands focus to the page heading instead of dropping it to body', async ({
  page,
}) => {
  await page.clock.install();
  await page.goto(listAddress('?fixture.variant.listRuns=unavailable'));
  await expect(page.locator('app-run-list-page')).toHaveAttribute('data-run-list-status', 'error');
  const retry = page.locator('app-retry-after button');
  // The wait disables the only control in the panel, so it is not a Tab stop until it elapses.
  await expect(retry).toBeDisabled();
  await page.clock.fastForward(5_000);
  await expect(retry).toBeEnabled();

  await enterMain(page);
  await enterFilterDisclosure(page);
  await auditRunTabStops(page.locator('app-run-list-page'), 'retry-503');
  // The newly introduced disclosure is explicit; the six filter stops keep their exact order.
  for (const selector of FILTER_STOPS) {
    await page.keyboard.press('Tab');
    await expect(page.locator(selector)).toBeFocused();
  }
  await page.keyboard.press('Tab');
  await expect(retry).toBeFocused();

  // The retry destroys the panel that held focus. The shared replacement hands the visitor's place
  // to the page heading, so the reload is announced from a place that still exists (F-RUN-05).
  await page.keyboard.press('Enter');
  const heading = page.locator('app-run-list-page h1');
  await expect(heading).toBeFocused();
  await expect(heading).toHaveAttribute('tabindex', '-1');
  expect((await activeElement(page)).tag).not.toBe('body');
  // The fixture answers 503 again, so the panel returns — and focus stays on the heading rather
  // than being thrown back at a control the visitor did not choose.
  await expect(page.locator('app-retry-after')).toBeVisible();
  await expect(heading).toBeFocused();
  await expect(page.getByRole('dialog')).toHaveCount(0);
});

test('Load more appends the next page and leaves focus on the row it appended', async ({
  page,
}) => {
  // `page-1-full` is the corpus's first page at the wire's own page size: 50 rows and a cursor, so
  // the list's own top-up stops short of the page limit and the `Load more` control stays on screen
  // for a real press. The latency keeps the follow observable while it is in flight.
  await page.goto(listAddress('?fixture.variant.listRuns=page-1-full&fixture.latency=1000'));
  await listIsReady(page);
  const rows = page.locator(RUN_RECORDS);
  await expect(rows).toHaveCount(FULL_PAGE_ROWS);
  // Below 200 rows the table renders every row, so each one is a real Tab stop of this walk.
  await expect(page.locator('app-run-list-table cdk-virtual-scroll-viewport')).toHaveCount(0);

  await enterMain(page);
  await enterFilterDisclosure(page);
  await auditRunTabStops(page.locator('app-run-list-page'), 'load-more/first-page');
  for (const selector of FILTER_STOPS) {
    await page.keyboard.press('Tab');
    await expect(page.locator(selector)).toBeFocused();
  }
  await tabThroughTableScroller(page, page.locator('app-run-list-table'));

  // Each served row has its navigation link, one original-ID copy action and only measured
  // full-value disclosures. The paging control follows every one of those controls.
  for (let row = 0; row < FULL_PAGE_ROWS; row++) {
    await tabThroughRunControls(page, rows.nth(row), 1, 1);
  }
  const loadMore = page.locator('[data-run-load-more]');
  await page.keyboard.press('Tab');
  await expect(loadMore).toBeFocused();
  await expectFocusRing(loadMore, '2px');

  // F-RUN-03: the control is never disabled for its own request, because a disabled element cannot
  // hold focus — the place has to survive the follow, and the paging section says it is busy.
  await page.keyboard.press('Enter');
  const paging = page.locator('section.pagination');
  await expect(paging).toHaveAttribute('aria-busy', 'true');
  await expect(loadMore).toBeEnabled();
  await expect(loadMore).toBeFocused();

  // The follow is answered by `run-list-page-2` and ends the cursor, so the control unmounts under
  // the visitor's focus: the place moves to the first row the follow appended, never to `body`, and
  // the visit continues from there instead of restarting at the skip link.
  await expect(rows).toHaveCount(FULL_PAGE_ROWS + runListPage2.body.items.length);
  await expect(loadMore).toHaveCount(0);
  const appended = rows.nth(FULL_PAGE_ROWS);
  await expect(appended).toHaveAttribute('data-row-id', runListPage2.body.items[0]!.run_id);
  const appendedLink = appended.locator('a');
  await expect(appendedLink, JSON.stringify(await activeElement(page))).toBeFocused();
  await auditRunTabStops(page.locator('app-run-list-page'), 'load-more/appended');
  expect((await activeElement(page)).tag).toBe('a');
  for (const control of await appended.locator('button, [tabindex="0"]').all()) {
    await page.keyboard.press('Tab');
    await expect(control).toBeFocused();
    if (
      (await control.getAttribute('role')) === 'button' ||
      (await control.evaluate((element) => element.matches('[hlmBadge].spartan-badge--clipped')))
    ) {
      await page.keyboard.press('Escape');
      await expect(control).toBeFocused();
    }
  }

  await page.keyboard.press('Tab');
  await expect(rows.nth(FULL_PAGE_ROWS + 1).locator('a')).toBeFocused();
  await expect(page.getByRole('dialog')).toHaveCount(0);
});

test('Load more preserves appended-row focus in fitted tablet records', async ({ page }) => {
  await page.setViewportSize({ width: 900, height: 900 });
  await page.goto(listAddress('?fixture.variant.listRuns=page-1-full&fixture.latency=1000'));
  await listIsReady(page);
  const rows = page.locator(RUN_RECORDS);
  await expect(rows).toHaveCount(FULL_PAGE_ROWS);
  await expect(page.locator('app-run-list-table .eg-table-narrow')).toHaveCount(1);
  const loadMore = page.locator('[data-run-load-more]');
  await loadMore.focus();
  await page.keyboard.press('Enter');
  await expect(loadMore).toBeFocused();
  await expect(rows).toHaveCount(FULL_PAGE_ROWS + runListPage2.body.items.length);
  await expect(loadMore).toHaveCount(0);
  const appended = rows.nth(FULL_PAGE_ROWS);
  await expect(appended).toHaveJSProperty('tagName', 'LI');
  await expect(appended).toHaveAttribute('data-row-id', runListPage2.body.items[0]!.run_id);
  await expect(appended.locator('a'), JSON.stringify(await activeElement(page))).toBeFocused();
  await expect(page.locator('app-run-list-table .eg-table-narrow')).toHaveCount(1);
});
