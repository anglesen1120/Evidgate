// The suite composition screen on the real fixtures build: one pinned revision read, its matrix,
// strategy and dependency list, the published revision's read-only notice, and the states the read
// can answer with (empty, 404, 403, 503). Every address here is the review one, because `suites` is
// a phase-G4 entry whose product deep link does not match in this build.
import { expect, test, type Page } from '@playwright/test';
import draftFixture from '../../../../fixtures/web-api/suite-revision/input.json';
import emptyFixture from '../../../../fixtures/web-api/suite-revision-empty/input.json';
import publishedFixture from '../../../../fixtures/web-api/suite-revision-published/input.json';
import {
  activeElement,
  expectNoA11yViolations,
  localePath,
  LOCALES,
  ORG_ACME,
  PROJECT_STOREFRONT,
  seedStoredTheme,
  THEMES,
  withVariants,
  type ShellLocale,
} from './support';

/** The grant set that holds `suite:read`; the default G1 session grants no suite code. */
const SUITE_READ_GRANT = '?fixture.variant.listPermissions=settings-policy-readonly';

const DRAFT = draftFixture.body;
const PUBLISHED = publishedFixture.body;
const EMPTY = emptyFixture.body;

/** The review address one revision hangs off (see `dev-routes.fixtures.ts`). */
const REVISIONS = [
  '/dev/suite-composition',
  `orgs/${ORG_ACME}`,
  `projects/${PROJECT_STOREFRONT}`,
  'suites',
  'revisions',
].join('/');

/** One composed revision address, with the case variant that selects its read. */
function revisionPath(locale: ShellLocale, variant: string, id: string = DRAFT.id): string {
  return withVariants(localePath(locale, `${REVISIONS}/${id}`), [
    SUITE_READ_GRANT,
    `?fixture.variant.getSuiteRevision=${variant}`,
  ]);
}

/** The host attribute the component sets from its own store status. */
async function composition(page: Page, status: string): Promise<void> {
  await expect(page.locator('app-suite-composition')).toHaveAttribute(
    'data-suite-composition-status',
    status,
  );
}

const MATRIX = '[data-testid="suite-matrix"]';
const STRATEGY = '[data-testid="suite-strategy"]';
const WARNINGS_UNAVAILABLE = '[data-testid="suite-warnings-unavailable"]';

for (const locale of LOCALES) {
  test(`pinned revision renders its matrix, strategy and dependencies (${locale})`, async ({
    page,
  }) => {
    await page.goto(revisionPath(locale, 'ok'));
    await composition(page, 'ready');
    await expect(page.getByTestId('suite-composition-state')).toHaveCount(1);
    await expect(page.locator(STRATEGY)).toBeVisible();
    await expect(page.locator(STRATEGY)).toContainText(String(DRAFT.shard_strategy.count));
    await expect(page.locator(MATRIX)).toBeVisible();
    await expect(page.locator(`${MATRIX} caption`)).toHaveCount(1);
    await expect(page.locator(`${MATRIX} thead th[scope="col"]`)).toHaveCount(
      new Set(DRAFT.members.map((member) => member.variant_hash)).size + 1,
    );
    await expect(page.locator(`${MATRIX} tbody th[scope="row"]`)).toHaveCount(
      new Set(DRAFT.members.map((member) => member.case_revision_id)).size,
    );
    await expect(page.locator(`${MATRIX} [data-suite-member-id]`)).toHaveCount(
      DRAFT.members.length,
    );
    // The read carries no warnings member, so the screen says the list is unavailable: it never
    // renders an empty list (which would claim "no warnings") and never a count.
    await expect(page.locator(WARNINGS_UNAVAILABLE)).toBeVisible();
    // The block carries the reason text, not a blank panel.
    await expect(page.locator(`${WARNINGS_UNAVAILABLE} p`)).toHaveCount(2);
    await expect(page.locator(WARNINGS_UNAVAILABLE)).toContainText(/\S/);
    await expect(page.getByTestId('suite-warnings-empty')).toHaveCount(0);
    await expect(page.getByTestId('suite-warnings-items')).toHaveCount(0);
    await expect(page.getByTestId('suite-published-notice')).toHaveCount(0);
    await page.reload();
    await composition(page, 'ready');
    await expect(page.locator(MATRIX)).toBeVisible();
  });

  test(`a published revision shows the read-only notice and no edit control (${locale})`, async ({
    page,
  }) => {
    await page.goto(revisionPath(locale, 'published', PUBLISHED.id));
    await composition(page, 'ready');
    await expect(page.getByTestId('suite-published-notice')).toBeVisible();
    await expect(page.locator('[data-revision-status]')).toHaveText(PUBLISHED.status);
    await expect(page.locator('[data-revision-no]')).toHaveText(String(PUBLISHED.revision_no));
    await expect(page.locator(MATRIX)).toBeVisible();
    // The read is read-only everywhere, and a published revision is immutable on the server too:
    // the surface offers no control that could be mistaken for an edit affordance.
    const surface = page.getByTestId('suite-composition');
    await expect(
      surface.locator(
        'button, [role="button"], input, select, textarea, [contenteditable]:not([contenteditable="false"])',
      ),
    ).toHaveCount(0);
  });

  test(`a revision with no member renders its own empty state (${locale})`, async ({ page }) => {
    await page.goto(revisionPath(locale, 'empty', EMPTY.id));
    await composition(page, 'ready');
    await expect(page.locator(MATRIX)).toHaveCount(0);
    // The heading stays the screen's own name; the empty revision says so where the matrix would
    // have been, so the two never collapse into one message.
    await expect(page.getByTestId('suite-composition-state')).toBeFocused();
    await expect(page.getByTestId('suite-matrix-empty')).toBeVisible();
    await expect(page.locator(STRATEGY)).toBeVisible();
    await expect(page.getByTestId('suite-warnings-items')).toHaveCount(0);
    await expect(page.getByTestId('suite-published-notice')).toHaveCount(0);
  });

  test(`refused, missing and unavailable revisions render their own states (${locale})`, async ({
    page,
  }) => {
    const states = [
      { variant: 'revision-not-found', status: 'not-found' },
      { variant: 'revision-forbidden', status: 'forbidden' },
      { variant: 'revision-unavailable', status: 'unavailable' },
    ] as const;
    for (const state of states) {
      await page.goto(revisionPath(locale, state.variant));
      await composition(page, state.status);
      await expect(page.getByTestId('suite-composition-state')).toBeVisible();
      await expect(page.locator(MATRIX)).toHaveCount(0);
      await expect(page.locator(STRATEGY)).toHaveCount(0);
      await expect(page.locator(WARNINGS_UNAVAILABLE)).toHaveCount(0);
    }
  });

  test(`the matrix region is a keyboard stop with a visible focus ring (${locale})`, async ({
    page,
  }) => {
    await page.goto(revisionPath(locale, 'ok'));
    await composition(page, 'ready');
    // The screen moves focus to its own state heading, so the route's region is announced first.
    await expect(page.getByTestId('suite-composition-state')).toBeFocused();
    await page.keyboard.press('Tab');
    const focused = await activeElement(page);
    // The matrix scroll region is the only stop between the heading and the end of the route.
    expect(focused.className, 'matrix region focus').toContain('matrix-scroll');
    const region = page.locator('.matrix-scroll');
    await expect(region).toBeFocused();
    expect(await region.evaluate((element) => element.matches(':focus-visible'))).toBe(true);
    await expect(region).toHaveCSS('outline-width', '2px');
    await expect(region).toHaveCSS('outline-style', 'solid');
  });
}

for (const locale of LOCALES) {
  for (const theme of THEMES) {
    test(`WCAG and mobile containment (${locale}, ${theme})`, async ({ page }) => {
      await seedStoredTheme(page, theme);
      await page.setViewportSize({ width: 360, height: 800 });
      await page.goto(revisionPath(locale, 'ok'));
      await composition(page, 'ready');
      await expect(page.locator(MATRIX)).toBeVisible();
      expect(
        await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth),
      ).toBe(true);
      await expectNoA11yViolations(page, `suite composition ${locale}/${theme}/360px`);
      await page.setViewportSize({ width: 1440, height: 900 });
      expect(
        await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth),
      ).toBe(true);
      await expectNoA11yViolations(page, `suite composition ${locale}/${theme}/1440px`);
    });
  }
}
