// The run detail's evidence panel, revision pin and decision region, exercised on the real
// fixture-backed page in both locale bundles and both theme token sets.
//
// Every case names the state it wants per operation, because the dev backend selects a document by
// `?fixture.variant.<operationId>=<variant>` and answers 501 for anything the corpus does not hold
// (the default `ok` decision body is `pending`, so a case that wants a verdict must ask for it).
// Assertions key on the `data-*` hooks of the two regions, never on a translated label — except
// where the label itself is the contract (the newer-revision banner), and then both locales are
// asserted separately.
import { expect, test, type Page } from '@playwright/test';
import { EVIDENCE_REVISIONS_PAGE } from '../../src/app/core/dev-fixtures/evidence-revision-fixtures';
import runDetail from '../../../../fixtures/web-api/run-detail/input.json';
import {
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

const RUN = runDetail.body;
/** Revisions arrive newest first: the newest is the default pin, the incomplete one is 2. */
const REVISIONS = EVIDENCE_REVISIONS_PAGE.items;
const NEWEST_REVISION = REVISIONS[0]!;
const INCOMPLETE_REVISION = REVISIONS[1]!;
const OLDEST_REVISION = REVISIONS[2]!;

const GRANT = '?fixture.variant.listPermissions=settings-policy-readonly';

type DecisionVariant =
  'ok' | 'evaluated-fail-missing-shard' | 'evaluated-pass' | 'shadow-refused' | 'unavailable';

type RevisionsVariant = 'ok' | 'empty' | 'unavailable';

const BANNER_TITLE: Record<ShellLocale, string> = {
  en: 'A newer revision exists',
  vi: 'Đã có phiên bản mới hơn',
};

/** One review address: the review route, the run, and one variant per operation it reads. */
function detailPath(
  locale: ShellLocale,
  decision: DecisionVariant = 'ok',
  revisions: RevisionsVariant = 'ok',
  search = '',
): string {
  return withVariants(
    localePath(
      locale,
      `/dev/run-detail/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}/runs/${RUN.run_id}${search}`,
    ),
    [
      GRANT,
      '?fixture.variant.getRun=detail-ready',
      '?fixture.variant.listRunAttempts=ok',
      `?fixture.variant.getRunDecision=${decision}`,
      `?fixture.variant.listEvidenceRevisions=${revisions}`,
    ],
  );
}

async function ready(page: Page): Promise<void> {
  await expect(page.locator('app-run-detail-page')).toHaveAttribute(
    'data-run-detail-status',
    'ready',
  );
}

/** The page's own text, collapsed: the "no verdict anywhere" assertions read it once. */
async function pageText(page: Page): Promise<string> {
  return ((await page.locator('app-run-detail-page').textContent()) ?? '').replace(/\s+/g, ' ');
}

function pinnedRevision(page: Page): string | null {
  return new URL(page.url()).searchParams.get('evidence_revision');
}

for (const locale of LOCALES) {
  test(`a pending decision states pending and carries no verdict (${locale})`, async ({ page }) => {
    await page.goto(detailPath(locale));
    await ready(page);

    const decision = page.locator('#decision-panel');
    await expect(decision.locator('[data-decision-pending]')).toBeVisible();
    const chip = decision.locator('app-decision-chip [data-tone]');
    await expect(chip).toHaveAttribute('data-tone', 'pending');
    await expect(chip.locator('.verdict__value')).toHaveText(
      locale === 'vi' ? 'Chưa đánh giá' : 'Not evaluated',
    );
    await expect(decision.locator('app-outcome-pill')).toHaveCount(0);
    await expect(decision.locator('[data-decision-provenance]')).toHaveCount(0);
    await expect(decision.locator('[data-decision-shadow]')).toHaveCount(0);

    // The evidence badge is collection completeness of a revision row, not a decision verdict.
    await expect(page.locator('#evidence-panel app-evidence-badge [data-tone]')).toHaveAttribute(
      'data-tone',
      'success',
    );
    await expect(page.locator('#evidence-panel [data-evidence-selected]')).toContainText(
      String(NEWEST_REVISION.revision_no),
    );
    await expect(page.locator('#evidence-panel [data-evidence-newer]')).toHaveCount(0);

    const text = await pageText(page);
    for (const verdict of ['PASS', 'FAIL', 'INCONCLUSIVE']) {
      expect(text, `a pending decision must not render ${verdict}`).not.toContain(verdict);
    }
    expect(pinnedRevision(page)).toBe(String(NEWEST_REVISION.revision_no));
  });

  test(`incomplete evidence and a FAIL decision show the missing shard (${locale})`, async ({
    page,
  }) => {
    await page.goto(detailPath(locale, 'evaluated-fail-missing-shard'));
    await ready(page);

    const decision = page.locator('#decision-panel');
    await expect(decision.locator('app-decision-chip [data-tone]')).toHaveAttribute(
      'data-tone',
      'danger',
    );
    await expect(decision.locator('app-decision-chip')).toContainText('FAIL');
    await expect(decision.locator('[data-decision-revision] dd')).toHaveText(
      String(INCOMPLETE_REVISION.revision_no),
    );
    await expect(decision.locator('app-outcome-pill [data-tone]')).toHaveAttribute(
      'data-tone',
      'danger',
    );
    await expect(decision.locator('[data-decision-mismatch]')).toHaveCount(0);

    await expect(page.locator('#evidence-panel app-evidence-badge [data-tone]')).toHaveAttribute(
      'data-tone',
      'warning',
    );
    await expect(page.locator('#evidence-panel [data-evidence-missing] .reason-code')).toHaveText(
      INCOMPLETE_REVISION.missing,
    );
    await expect(page.locator('#evidence-panel [data-evidence-selected]')).toContainText(
      String(INCOMPLETE_REVISION.revision_no),
    );
    expect(pinnedRevision(page)).toBe(String(INCOMPLETE_REVISION.revision_no));

    const text = await pageText(page);
    expect(text, 'an incomplete evidence revision is never a PASS').not.toContain('PASS');
  });

  test(`an evaluated PASS decision renders its verdict, outcome and empty reasons (${locale})`, async ({
    page,
  }) => {
    await page.goto(detailPath(locale, 'evaluated-pass'));
    await ready(page);

    const decision = page.locator('#decision-panel');
    await expect(decision.locator('app-decision-chip [data-tone]')).toHaveAttribute(
      'data-tone',
      'success',
    );
    await expect(decision.locator('app-decision-chip')).toContainText('PASS');
    await expect(decision.locator('app-outcome-pill [data-tone]')).toHaveAttribute(
      'data-tone',
      'success',
    );
    await expect(decision.locator('[data-decision-no-reasons]')).toBeVisible();
    await expect(decision.locator('[data-decision-mode] dd')).toHaveText('live');
    await expect(decision.locator('[data-decision-revision] dd')).toHaveText(
      String(OLDEST_REVISION.revision_no),
    );

    // The decision pins the revision it was evaluated at, and the banner names the newer one.
    await expect(page.locator('#evidence-panel [data-evidence-newer]')).toBeVisible();
    await expect(page.locator('#evidence-panel [data-evidence-newer]')).toContainText(
      BANNER_TITLE[locale],
    );
  });

  test(`a shadow decision never fills the live slot (${locale})`, async ({ page }) => {
    await page.goto(detailPath(locale, 'shadow-refused'));
    await ready(page);

    const decision = page.locator('#decision-panel');
    await expect(decision.locator('[data-decision-shadow]')).toBeVisible();
    await expect(decision.locator('[data-decision-unavailable]')).toBeVisible();
    await expect(decision.locator('app-decision-chip')).toHaveCount(0);
    await expect(decision.locator('app-outcome-pill')).toHaveCount(0);

    const text = await pageText(page);
    expect(text, 'the refused shadow verdict must not appear').not.toContain('PASS');
  });

  test(`selecting an older revision pins it in the URL and keeps its own data (${locale})`, async ({
    page,
  }) => {
    await page.goto(detailPath(locale));
    await ready(page);

    const select = page.locator('[data-evidence-revision-select]');
    await expect(select).toHaveValue(String(NEWEST_REVISION.revision_no));
    await expect(page.locator('#evidence-panel [data-evidence-newer]')).toHaveCount(0);

    await select.selectOption(String(INCOMPLETE_REVISION.revision_no));

    await expect(page.locator('#evidence-panel [data-evidence-newer]')).toBeVisible();
    await expect(page.locator('#evidence-panel [data-evidence-newer]')).toContainText(
      BANNER_TITLE[locale],
    );
    await expect(page.locator('#evidence-panel [data-evidence-selected]')).toContainText(
      String(INCOMPLETE_REVISION.revision_no),
    );
    await expect(select).toHaveValue(String(INCOMPLETE_REVISION.revision_no));
    await expect.poll(() => pinnedRevision(page)).toBe(String(INCOMPLETE_REVISION.revision_no));

    // The pinned row's own completeness, missing reason and evaluation reference stay on screen,
    // and the pending decision region is untouched by the pin.
    await expect(page.locator('#evidence-panel app-evidence-badge [data-tone]')).toHaveAttribute(
      'data-tone',
      'warning',
    );
    await expect(page.locator('#evidence-panel [data-evidence-missing] .reason-code')).toHaveText(
      INCOMPLETE_REVISION.missing,
    );
    await expect(page.locator('#evidence-panel [data-evidence-evaluation]')).toContainText(
      String(INCOMPLETE_REVISION.evaluation_id),
    );
    await expect(page.locator('#decision-panel [data-decision-pending]')).toBeVisible();
    expect(new URL(page.url()).searchParams.get('fixture.variant.getRunDecision')).toBe('ok');
  });

  test(`an evidence_revision deep link restores the pin over the decision's own revision (${locale})`, async ({
    page,
  }) => {
    await page.goto(
      detailPath(
        locale,
        'evaluated-fail-missing-shard',
        'ok',
        `?evidence_revision=${OLDEST_REVISION.revision_no}`,
      ),
    );
    await ready(page);

    const select = page.locator('[data-evidence-revision-select]');
    await expect(select).toHaveValue(String(OLDEST_REVISION.revision_no));
    await expect(page.locator('#evidence-panel [data-evidence-newer]')).toBeVisible();
    await expect(page.locator('#evidence-panel [data-evidence-historical]')).toBeVisible();
    await expect(page.locator('#evidence-panel [data-evidence-selected]')).toContainText(
      String(OLDEST_REVISION.revision_no),
    );
    // The decision is still the accepted body, clearly scoped to its own revision.
    await expect(page.locator('#decision-panel app-decision-chip')).toContainText('FAIL');
    await expect(page.locator('#decision-panel [data-decision-mismatch]')).toBeVisible();

    await page.reload();
    await ready(page);
    await expect(select).toHaveValue(String(OLDEST_REVISION.revision_no));
    await expect(page.locator('#evidence-panel [data-evidence-newer]')).toBeVisible();
    await expect(page.locator('#decision-panel [data-decision-mismatch]')).toBeVisible();
    expect(pinnedRevision(page)).toBe(String(OLDEST_REVISION.revision_no));
  });

  test(`a fragment lands on the attempt row and focuses it (${locale})`, async ({ page }) => {
    // The fixture's first attempt: its row id is the documented anchor shape.
    const anchor = 'attempt-4da7edd402de84998aaa51a9d5f3a67f44219ebd3f25424664882f8b722953e3-1';
    await page.goto(`${detailPath(locale)}#${anchor}`);
    await ready(page);

    const target = page.locator(`#${anchor}`);
    await expect(target).toBeVisible();
    await expect(target).toBeInViewport();
    await expect(target).toBeFocused();
  });

  test(`a run with no frozen revision says so and infers no verdict (${locale})`, async ({
    page,
  }) => {
    await page.goto(detailPath(locale, 'ok', 'empty'));
    await ready(page);

    await expect(page.locator('#evidence-panel [data-evidence-empty]')).toBeVisible();
    await expect(page.locator('[data-evidence-revision-select]')).toHaveCount(0);
    await expect(page.locator('#decision-panel [data-decision-pending]')).toBeVisible();
    expect(pinnedRevision(page)).toBeNull();
    expect(await pageText(page)).not.toContain('PASS');
  });

  test(`a failed decision read and a failed revision read fail on their own regions (${locale})`, async ({
    page,
  }) => {
    await page.goto(detailPath(locale, 'unavailable', 'ok'));
    await ready(page);
    // The decision region owns its failure (the corpus answers a retryable 503 with Retry-After,
    // so the region renders the refusal window); the revision list still answers.
    await expect(page.locator('#decision-panel app-retry-after')).toBeVisible();
    await expect(page.locator('#decision-panel app-decision-chip')).toHaveCount(0);
    await expect(page.locator('[data-evidence-revision-select]')).toHaveValue(
      String(NEWEST_REVISION.revision_no),
    );

    await page.goto(detailPath(locale, 'ok', 'unavailable'));
    await ready(page);
    // The reverse: the evidence panel owns its failure and the pending decision stays.
    await expect(page.locator('#evidence-panel app-error-pattern')).toBeVisible();
    await expect(page.locator('[data-evidence-revision-select]')).toHaveCount(0);
    await expect(page.locator('#decision-panel [data-decision-pending]')).toBeVisible();
    await expect(page.locator('app-run-header .run-summary')).toBeVisible();
  });
}

for (const locale of LOCALES) {
  for (const theme of THEMES) {
    test(`WCAG 2.2 AA across the evidence and decision states (${locale}, ${theme})`, async ({
      page,
    }) => {
      await seedStoredTheme(page, theme);

      await page.goto(detailPath(locale));
      await ready(page);
      await expect(page.locator('#decision-panel [data-decision-pending]')).toBeVisible();
      await expectNoA11yViolations(page, `run detail pending ${locale}/${theme}`);

      await page.goto(detailPath(locale, 'evaluated-fail-missing-shard'));
      await ready(page);
      await expect(page.locator('#evidence-panel [data-evidence-missing] .reason-code')).toHaveText(
        INCOMPLETE_REVISION.missing,
      );
      await expectNoA11yViolations(page, `run detail incomplete evidence ${locale}/${theme}`);

      await page.goto(
        detailPath(
          locale,
          'evaluated-fail-missing-shard',
          'ok',
          `?evidence_revision=${OLDEST_REVISION.revision_no}`,
        ),
      );
      await ready(page);
      await expect(page.locator('#evidence-panel [data-evidence-newer]')).toBeVisible();
      await expectNoA11yViolations(page, `run detail revision banner ${locale}/${theme}`);

      await page.goto(detailPath(locale, 'ok', 'unavailable'));
      await ready(page);
      await expect(page.locator('#evidence-panel app-error-pattern')).toBeVisible();
      await expectNoA11yViolations(page, `run detail evidence error ${locale}/${theme}`);

      await page.goto(detailPath(locale, 'unavailable', 'empty'));
      await ready(page);
      await expect(page.locator('#decision-panel app-retry-after')).toBeVisible();
      await expectNoA11yViolations(page, `run detail decision error ${locale}/${theme}`);
    });
  }
}
