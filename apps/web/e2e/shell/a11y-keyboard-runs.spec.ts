// The Runs flow uses the keyboard alone on the real fixtures build: no click or force.
// Each assertion identifies focus by role/name, id or data hook, never by counting Tab stops.
// Keep overflowing scrollers, row copy/full-value actions and every shared pager in DOM order,
// including focusable disabled boundaries, with no anonymous stops. Attempts use server pages.
import { expect, test, type Locator, type Page } from '@playwright/test';
import runList from '../../../../fixtures/web-api/run-list/input.json';
import runList250 from '../../../../fixtures/web-api/run-list-250/input.json';
import { expectNoA11yViolations } from '../a11y/axe-scan';
import { activeElement, localePath, ORG_ACME, PROJECT_STOREFRONT, withVariants } from './support';
import { RUN_FILTER_BUTTON, RUN_RECORDS } from './run-list.support';
import {
  auditRunTabStops,
  enterRunDetails,
  tabThroughNarrowRunRecords,
  tabThroughRunControls,
  tabThroughTableScroller,
} from './run-table-keyboard.support';

/** The grant set that holds `run:read`; the default G1 session grants no run code. */
const RUN_READ_GRANT = '?fixture.variant.listPermissions=settings-policy-readonly';
const RUN_WITH_REVISION_AND_RELEASE = '?fixture.variant.getRun=detail-release';
const FIRST_RUN = runList.body.items[0]!.run_id;
const SELECT_STOPS = [
  '#run-state',
  'button[aria-label="Hint: Decision"]',
  '#run-decision',
  '#run-scope',
] as const;
const FILTER_STOPS = [...SELECT_STOPS, '#run-ref', '[data-run-apply]', '[data-run-clear]'] as const;

const FULL_PAGE_ROWS = 20;
const LARGE_PAGE_FIRST = runList250.body.items[0]!.run_id;
const LARGE_PAGE_SECOND = runList250.body.items[20]!.run_id;

function listAddress(...variants: string[]): string {
  return withVariants(
    localePath('en', `/dev/run-list/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}`),
    [RUN_READ_GRANT, ...variants],
  );
}

async function runRequestCount(page: Page): Promise<number> {
  return page.evaluate(
    () =>
      (window.__evidgateFixtureRequests ?? []).filter((entry) =>
        new URL(entry.url, location.origin).pathname.endsWith('/runs'),
      ).length,
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

/** The filter disclosure precedes the controls and their optional guidance. */
async function enterFilterDisclosure(page: Page): Promise<void> {
  await page.keyboard.press('Tab');
  const button = page.locator(RUN_FILTER_BUTTON);
  await expect(button).toBeFocused();
  await expectFocusRing(button, '2px');
  if ((await button.getAttribute('aria-expanded')) === 'false') await page.keyboard.press('Enter');
  await expect(button).toHaveAttribute('aria-expanded', 'true');
}

test('the list, a row and the way back are keyboard-reachable and identifiable', async ({
  page,
}) => {
  await page.goto(listAddress('?fixture.variant.listRuns=runs', RUN_WITH_REVISION_AND_RELEASE));
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
  const firstRow = page.locator(`[data-run-id="${FIRST_RUN}"]`).getByRole('link');
  await expect(firstRow).toHaveAccessibleName(`main (${FIRST_RUN})`);
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

  // The header's inspect-evidence action sits between the back link and the run summary.
  await page.keyboard.press('Tab');
  await expect(page.getByRole('button', { name: 'Inspect evidence' })).toBeFocused();
  const summary = page.getByRole('region', { name: 'Stored run decision' });
  for (const name of ['Show full Evidence revision', 'Copy Evidence revision']) {
    await page.keyboard.press('Tab');
    await expect(summary.getByRole('button', { name, exact: true })).toBeFocused();
  }
  const context = page.locator('app-run-header').last();
  await tabThroughRunControls(page, context.locator('section > dl'), 3);
  await enterRunDetails(page, context.locator('details'));
  await tabThroughRunControls(page, context.locator('details'), 2);
  await expect(page.getByRole('region', { name: 'Attempts table' })).toHaveAttribute(
    'tabindex',
    '-1',
  );
  await expect(page.locator('app-attempts-table [data-attempt-row]')).toHaveCount(7);
  for (const [selector, rows, copies, summary] of [
    ['app-attempts-table', 'app-data-table', 7, 'Showing 1–7'],
    ['#producers', 'app-data-table', 2, 'Showing 1–2 of 2'],
  ] as const) {
    const root = page.locator(selector);
    await tabThroughTableScroller(page, root);
    await tabThroughRunControls(page, root.locator(rows), copies);
    await expect(root.getByRole('navigation', { name: 'Table pagination' })).toHaveCount(0);
    await expect(root.locator('app-data-table-pager .page-range')).toHaveText(summary);
  }

  // Evidence and the latest live decision are tabs: only the selected tab is a Tab stop, arrows move
  // inside the list, and the selected panel is the next stop; the hidden panel leaves the order.
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
  await expect(
    page.getByRole('button', { name: 'Hint: Evidence revision', exact: true }),
  ).toBeFocused();
  await page.keyboard.press('Tab');
  await expect(page.getByRole('combobox', { name: 'Evidence revision' })).toBeFocused();
  await expect(page.locator('app-evidence-panel app-copy-value-button button')).toHaveCount(0);
  const manifest = page.locator('app-manifest-panel details');
  await enterRunDetails(page, manifest);
  await tabThroughRunControls(page, manifest, 3);
  const copy = page.getByRole('button', { name: 'Copy full manifest hash' });
  expect((await copy.boundingBox())?.height ?? 0).toBeGreaterThanOrEqual(44);
  const release = page.locator('app-release-panel');
  await expect(release.locator('.actions button')).toHaveCount(4);
  for (const [hook, label, value] of [
    ['data-release-digest', 'Digest for component web', `sha256:${'a'.repeat(64)}`],
    [
      'data-release-environment',
      'Environment revision',
      /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/,
    ],
  ] as const) {
    const field = release.locator(`[${hook}]`);
    const disclosure = field.getByRole('button', { name: `Show full ${label}`, exact: true });
    const copyButton = field.getByRole('button', { name: `Copy ${label}`, exact: true });
    await page.keyboard.press('Tab');
    await expect(disclosure).toBeFocused();
    await page.keyboard.press('Enter');
    const dialog = page.getByRole('dialog');
    await expect(dialog.locator('code.full-value bdi')).toHaveText(value);
    await page.keyboard.press('Escape');
    await expect(dialog).not.toBeVisible();
    await expect(disclosure).toBeFocused();
    await page.keyboard.press('Tab');
    await expect(copyButton).toBeFocused();
    if (hook === 'data-release-digest') {
      expect((await copyButton.boundingBox())?.height ?? 0).toBeGreaterThanOrEqual(44);
    }
  }
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
  for (const selector of SELECT_STOPS) {
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

test('the narrow server pages keep every fact reachable and preserve focus on a page turn', async ({
  page,
}) => {
  await page.setViewportSize({ width: 800, height: 800 });
  await page.goto(listAddress('?fixture.variant.listRuns=250-rows'));
  await listIsReady(page);
  const records = page.locator(RUN_RECORDS);
  await expect(records).toHaveCount(20);
  await expect(records.first().locator('dt')).toHaveCount(6);
  await expect(records.first().locator('dd')).toHaveCount(6);
  await expect(page.locator('app-run-list-table .scroll[tabindex="0"]')).toHaveCount(0);
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(800);
  await enterMain(page);
  await enterFilterDisclosure(page);
  await auditRunTabStops(page.locator('app-run-list-page'), '250-records/first-page');
  for (const selector of FILTER_STOPS) {
    await page.keyboard.press('Tab');
    await expect(page.locator(selector)).toBeFocused();
  }
  await tabThroughNarrowRunRecords(page, page.locator('app-run-list-table .eg-table-records'), 20);
  const pager = page.locator('app-data-table-pager');
  const previous = pager.getByRole('button', { name: 'Previous page', exact: true });
  const next = pager.getByRole('button', { name: 'Next page', exact: true });
  await expect(previous).toHaveAttribute('aria-disabled', 'true');
  for (const control of [pager.getByRole('combobox'), previous, next]) {
    await page.keyboard.press('Tab');
    await expect(control).toBeFocused();
  }
  await page.keyboard.press('Enter');
  await expect(records.first()).toHaveAttribute('data-row-id', LARGE_PAGE_SECOND);
  await expect(records).toHaveCount(20);
  await expect(next).toBeFocused();
  await auditRunTabStops(page.locator('app-run-list-page'), '250-records/second-page');
  for (const control of [previous, pager.getByRole('combobox')]) {
    await page.keyboard.press('Shift+Tab');
    await expect(control).toBeFocused();
  }
  await page.keyboard.press('Shift+Tab');
  const lastId = await records.last().getAttribute('data-row-id');
  const lastView = records.last().getByRole('button', { name: `View ${lastId}`, exact: true });
  await expect(lastView).toBeFocused();
  await page.keyboard.press('Enter');
  const dialog = page.getByRole('dialog');
  await expect(dialog.locator('dd bdi').first()).toHaveText(lastId!);
  await page.keyboard.press('Tab');
  await expect(dialog.getByRole('button', { name: 'Copy Run ID' })).toBeFocused();
  await page.keyboard.press('Escape');
  await expect(lastView).toBeFocused();
  await page.keyboard.press('Shift+Tab');
  await expect(records.last().getByRole('link')).toBeFocused();
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
  // Opening the filter disclosure preserves the same hint and field order during recovery.
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

test('Next replaces the page and retains its keyboard focus while busy and at the boundary', async ({
  page,
}) => {
  await page.goto(listAddress('?fixture.variant.listRuns=paging-33&fixture.latency=1000'));
  await listIsReady(page);
  const rows = page.locator(RUN_RECORDS);
  await expect(rows).toHaveCount(FULL_PAGE_ROWS);
  await expect(page.locator('app-run-list-table cdk-virtual-scroll-viewport')).toHaveCount(0);
  await enterMain(page);
  await enterFilterDisclosure(page);
  await auditRunTabStops(page.locator('app-run-list-page'), 'cursor/first-page');
  for (const selector of FILTER_STOPS) {
    await page.keyboard.press('Tab');
    await expect(page.locator(selector)).toBeFocused();
  }
  await tabThroughTableScroller(page, page.locator('app-run-list-table'));
  for (let row = 0; row < FULL_PAGE_ROWS; row++) {
    await tabThroughRunControls(page, rows.nth(row), 0, true);
  }
  const pager = page.locator('app-data-table-pager');
  const previous = pager.getByRole('button', { name: 'Previous page', exact: true });
  const next = pager.getByRole('button', { name: 'Next page', exact: true });
  for (const control of [pager.getByRole('combobox'), previous, next]) {
    await page.keyboard.press('Tab');
    await expect(control).toBeFocused();
    await expectFocusRing(control, '2px');
  }
  const firstPageRequests = await runRequestCount(page);
  await page.keyboard.press('Enter');
  await expect(pager.getByRole('navigation')).toHaveAttribute('aria-busy', 'true');
  await expect(next).not.toHaveAttribute('disabled');
  await expect(next).toHaveAttribute('aria-disabled', 'true');
  await expect(next).toBeFocused();
  await page.keyboard.press('Enter');
  await page.keyboard.press('Space');
  await expect(rows).toHaveCount(13);
  await expect(rows.first()).toHaveAttribute('data-row-id', LARGE_PAGE_SECOND);
  await expect(next).toBeFocused();
  await expect(next).toHaveAttribute('aria-disabled', 'true');
  expect(await runRequestCount(page)).toBe(firstPageRequests + 1);
  await expect(next).not.toHaveAttribute('disabled');
  await page.keyboard.press('Enter');
  await page.keyboard.press('Space');
  await expect(next).toBeFocused();
  await expect(rows).toHaveCount(13);
  await expect(rows.first()).toHaveAttribute('data-row-id', LARGE_PAGE_SECOND);
  expect(await runRequestCount(page)).toBe(firstPageRequests + 1);
  await auditRunTabStops(page.locator('app-run-list-page'), 'cursor/last-page');
  await page.keyboard.press('Shift+Tab');
  await expect(previous).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(rows).toHaveCount(20);
  await expect(rows.first()).toHaveAttribute('data-row-id', LARGE_PAGE_FIRST);
  await expect(previous).toBeFocused();
  await expect(previous).toHaveAttribute('aria-disabled', 'true');
  await expect(page.getByRole('dialog')).toHaveCount(0);
});

test('Next preserves pager focus in fitted tablet records', async ({ page }) => {
  await page.setViewportSize({ width: 768, height: 900 });
  await page.goto(listAddress('?fixture.variant.listRuns=paging-33&fixture.latency=1000'));
  await listIsReady(page);
  const rows = page.locator(RUN_RECORDS);
  await expect(rows).toHaveCount(20);
  await expect(page.locator('app-run-list-table .eg-table-narrow')).toHaveCount(1);
  const next = page.getByRole('button', { name: 'Next page', exact: true });
  await next.focus();
  await page.keyboard.press('Enter');
  await expect(next).toBeFocused();
  await expect(rows).toHaveCount(13);
  await expect(rows.first()).toHaveJSProperty('tagName', 'LI');
  await expect(rows.first()).toHaveAttribute('data-row-id', LARGE_PAGE_SECOND);
  await expect(next, JSON.stringify(await activeElement(page))).toBeFocused();
  await expect(page.locator('app-run-list-table .eg-table-narrow')).toHaveCount(1);
});
