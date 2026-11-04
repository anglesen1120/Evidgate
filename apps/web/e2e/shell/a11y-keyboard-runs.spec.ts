// 09.8.1-T1.2 (story 09.8.1 AC2): the Runs flow with the keyboard alone, on the real fixtures
// build. Every step is a `page.keyboard` press — no `click`, no `force` — and every assertion names
// the element that holds focus by its own identity (role + accessible name, id, data hook), never by
// counting Tab stops.
//
// Since S146 (09.3.1-T4/T2.2) the run detail reads the manifest members and the attempts keyset from
// the wire, so the manifest copy button (F-RUN-01) is a real stop of this walk. The attempts pager
// (F-RUN-02) needs more than 50 rows to appear, which no corpus page has, so it stays pinned at
// component level: `attempts-table.spec.ts` ("hands focus to the opposite pager button when a page
// turn disables the activated one"); the load-more control is walked in `run-detail.spec.ts`.
// Everything the browser can render is walked here: the list, its filters, the virtual table, the
// paging error, and the `Load more` append (F-RUN-03).
import { expect, test, type Locator, type Page } from '@playwright/test';
import runList from '../../../../fixtures/web-api/run-list/input.json';
import runListPage2 from '../../../../fixtures/web-api/run-list-page-2/input.json';
import {
  activeElement,
  expectNoA11yViolations,
  localePath,
  ORG_ACME,
  PROJECT_STOREFRONT,
  withVariants,
} from './support';

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

test('the list, a row and the way back are keyboard-reachable and identifiable', async ({
  page,
}) => {
  await page.goto(listAddress('?fixture.variant.listRuns=runs'));
  await listIsReady(page);
  await enterMain(page);

  for (const selector of FILTER_STOPS) {
    await page.keyboard.press('Tab');
    await expect(page.locator(selector)).toBeFocused();
    await expectFocusRing(page.locator(selector), '2px');
  }

  // One stop for the plain table: its own scroll wrapper, under its own name.
  await page.keyboard.press('Tab');
  const region = page.getByRole('region', { name: 'Project runs' });
  await expect(region).toBeFocused();
  await expectFocusRing(region, '-2px');

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
  // A path change hands focus to the route's own region, so the visitor starts inside the page.
  expect((await activeElement(page)).id).toBe('main-content');

  await page.keyboard.press('Tab');
  const back = page.getByRole('link', { name: 'Back to runs' });
  await expect(back).toBeFocused();
  expect((await back.boundingBox())?.height ?? 0).toBeGreaterThanOrEqual(44);

  // The attempts table's scroll region follows the header. The default attempts page (seven rows,
  // no further cursor) has no pager or load-more button, so the region is the last stop of that
  // section.
  await page.keyboard.press('Tab');
  const attemptsRegion = page.getByRole('region', { name: 'Attempts table' });
  await expect(attemptsRegion).toBeFocused();
  await expect(page.locator('app-attempts-table [data-attempt-row]')).toHaveCount(7);
  await expect(page.locator('app-attempts-table button')).toHaveCount(0);

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

  // The selected panel's revision picker is a combobox (the page is pinned to a frozen revision,
  // so it is a real stop of the walk), then the manifest panel's copy button ends the page.
  await page.keyboard.press('Tab');
  await expect(page.getByRole('combobox', { name: 'Evidence revision' })).toBeFocused();

  await page.keyboard.press('Tab');
  const copy = page.getByRole('button', { name: 'Copy full manifest hash' });
  await expect(copy).toBeFocused();
  expect((await copy.boundingBox())?.height ?? 0).toBeGreaterThanOrEqual(44);

  await page.keyboard.press('Tab');
  expect((await activeElement(page)).tag).toBe('body');
  await expect(page.getByRole('dialog')).toHaveCount(0);
});

test('the selected tab panel is a stop of its own when the decision has no control', async ({
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
  await expect(page.locator('app-decision-view a[href], app-decision-view button')).toHaveCount(0);

  // With no control inside, the panel itself is the only stop between the tab list and the
  // manifest panel; the hidden evidence panel is out of the order.
  const panel = page.getByRole('tabpanel');
  await expect(panel).toHaveCount(1);
  await expect(panel).toHaveAttribute('tabindex', '0');
  await expect(page.locator('[role="tabpanel"][hidden]')).toHaveAttribute('tabindex', '-1');
  await page.keyboard.press('Tab');
  await expect(panel).toBeFocused();
  await expectFocusRing(panel, '2px');
  await page.keyboard.press('Tab');
  await expect(page.getByRole('button', { name: 'Copy full manifest hash' })).toBeFocused();
  await page.keyboard.press('Shift+Tab');
  await expect(panel).toBeFocused();
  await expectNoA11yViolations(page);
});

test('a filter change keeps the focus that asked for it, and Clear restores the table order', async ({
  page,
}) => {
  await page.goto(listAddress('?fixture.variant.listRuns=runs'));
  await listIsReady(page);
  await enterMain(page);

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
  // The tab order from the controls is intact afterwards: the table, then its rows.
  await page.keyboard.press('Tab');
  await expect(page.getByRole('region', { name: 'Project runs' })).toBeFocused();
  await page.keyboard.press('Tab');
  await expect(page.locator('app-run-list-table [data-run-row] a').first()).toBeFocused();
  await expect(page.getByRole('dialog')).toHaveCount(0);
});

test('the 250-row table keeps both scroll axes on named stops and the visitor inside it', async ({
  page,
}) => {
  // Stay above the stacked-record breakpoint but below the desktop table minimum.
  await page.setViewportSize({ width: 800, height: 800 });
  await page.goto(listAddress('?fixture.variant.listRuns=250-rows'));
  await listIsReady(page);
  await expect(page.locator('app-run-list-table [data-run-row]').first()).toBeVisible();
  // Both scroll axes have named stops. Truncated values can add their own tooltip stops.
  await expect(
    page.locator('app-run-list-table :is([role="region"], [role="rowgroup"])[tabindex="0"]'),
  ).toHaveCount(2);

  await enterMain(page);
  for (const selector of FILTER_STOPS) {
    await page.keyboard.press('Tab');
    await expect(page.locator(selector)).toBeFocused();
  }

  const region = page.locator('app-run-list-table .table-scroll');
  await page.keyboard.press('Tab');
  await expect(region).toBeFocused();
  await expect(region).toHaveAccessibleName('Project runs');
  await expectFocusRing(region, '-2px');

  // The region is the only way to the last columns: the viewport is exactly as wide as the table,
  // so keyboard scrolling has to happen on the wrapper, and it must actually move.
  const scrollLeft = () => region.evaluate((node) => node.scrollLeft);
  expect(await scrollLeft()).toBe(0);
  await page.keyboard.press('ArrowRight');
  expect(await scrollLeft()).toBeGreaterThan(0);
  await page.keyboard.press('End');
  const reach = await region.evaluate((node) => node.scrollWidth - node.clientWidth);
  expect(await scrollLeft()).toBe(reach);
  await page.keyboard.press('Home');
  expect(await scrollLeft()).toBe(0);

  await page.keyboard.press('Tab');
  const scroller = page.locator('app-run-list-table cdk-virtual-scroll-viewport');
  await expect(scroller).toBeFocused();
  await expect(scroller).toHaveAccessibleName('Project runs rows');
  await expectFocusRing(scroller, '-2px');

  await page.keyboard.press('Tab');
  await expect(page.locator('app-run-list-table [data-run-row] a').first()).toBeFocused();

  // Scrolling the window past the rendered range never drops focus to the document: the guard moves
  // it to the viewport that is still on screen.
  await page.keyboard.press('Shift+Tab');
  await expect(scroller).toBeFocused();
  await page.keyboard.press('End');
  await expect(scroller).toBeFocused();
  await expect(page.locator('app-run-list-table [data-run-row]').last()).toBeInViewport();
  expect((await activeElement(page)).tag).not.toBe('body');

  await expectNoA11yViolations(page, 'run list, 250 rows');
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
  // The panel is behind the filter form; focus must cross it without ever resting on body.
  for (let press = 0; press < FILTER_STOPS.length + 1; press++) {
    if (await page.evaluate(() => document.activeElement?.closest('app-retry-after') !== null)) {
      break;
    }
    await page.keyboard.press('Tab');
    expect((await activeElement(page)).tag).not.toBe('body');
  }
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
  const rows = page.locator('app-run-list-table [data-run-row]');
  await expect(rows).toHaveCount(FULL_PAGE_ROWS);
  // Below 200 rows the table renders every row, so each one is a real Tab stop of this walk.
  await expect(page.locator('app-run-list-table cdk-virtual-scroll-viewport')).toHaveCount(0);

  await enterMain(page);
  for (const selector of FILTER_STOPS) {
    await page.keyboard.press('Tab');
    await expect(page.locator(selector)).toBeFocused();
  }
  await page.keyboard.press('Tab');
  await expect(page.getByRole('region', { name: 'Project runs' })).toBeFocused();

  // The rows are the table's own stops in server order; the paging section follows the table.
  for (let row = 0; row < FULL_PAGE_ROWS; row++) {
    await page.keyboard.press('Tab');
    await expect(rows.nth(row).locator('a')).toBeFocused();
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
  await expect(appended).toHaveAttribute('data-run-id', runListPage2.body.items[0]!.run_id);
  const appendedLink = appended.locator('a');
  await expect(appendedLink).toBeFocused();
  expect((await activeElement(page)).tag).toBe('a');

  await page.keyboard.press('Tab');
  await expect(rows.nth(FULL_PAGE_ROWS + 1).locator('a')).toBeFocused();
  await expect(page.getByRole('dialog')).toHaveCount(0);
});
