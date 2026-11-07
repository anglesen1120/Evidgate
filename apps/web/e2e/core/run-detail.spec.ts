// The core run-detail flow: one run opened from the list, and the FAIL a missing shard produces.
// The wide matrix (pending, PASS, unavailable, revision pins, a11y) belongs to the shell suite's
// run-detail-evidence.spec.ts, so these two cases are only the shardable "the screen is usable" pair.
import { expect, test } from '@playwright/test';

import runDetail from '../../../../fixtures/web-api/run-detail/input.json';
import runList from '../../../../fixtures/web-api/run-list/input.json';
import { runDetailPath } from '../shell/run-detail.support';
import {
  localePath,
  LOCALES,
  ORG_ACME,
  PROJECT_STOREFRONT,
  withVariants,
  type ShellLocale,
} from '../shell/support';
import { RunDetailPage } from './pages/run-detail.page';

/** The corpus grant set that holds `run:read`; the G1 default set grants no run code at all. */
const RUN_READ_GRANT = '?fixture.variant.listPermissions=settings-policy-readonly';
/** The list corpus, and the detail answers the row click lands on for the same run. */
const RUNS = '?fixture.variant.listRuns=runs';
const DETAIL_READY = '?fixture.variant.getRun=detail-ready';
/** That run is the list corpus's first row, so the click below opens a run the list really shows. */
const RUN_ID = runDetail.body.run_id;
/** A wire reason code, not a label: both locale bundles render it verbatim. */
const MISSING_SHARD = 'missing_shard';

/**
 * The review address of the run list. The row link preserves this query, so the detail page it
 * opens reads the same per-operation variants the list was asked for.
 */
function listPath(locale: ShellLocale): string {
  return withVariants(
    localePath(locale, `/dev/run-list/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}`),
    [RUN_READ_GRANT, RUNS, DETAIL_READY],
  );
}

for (const locale of LOCALES) {
  test(`a run opens from the list into its detail (${locale})`, async ({ page }) => {
    const detail = new RunDetailPage(page);
    await detail.openList(listPath(locale));
    await expect(detail.rows).toHaveCount(runList.body.items.length);

    await detail.openRun(RUN_ID);

    // The navigation keeps the list's query, so the header facts are the requested variant's.
    expect(new URL(page.url()).searchParams.get('fixture.variant.getRun')).toBe('detail-ready');
    await expect(detail.headerId).toHaveText(RUN_ID);
    await expect(detail.headerRef).toContainText(runDetail.body.ref.name);
    await expect(detail.headerVersion).toHaveText(String(runDetail.body.version));
  });

  test(`an evaluated FAIL shows the missing shard and never PASS (${locale})`, async ({ page }) => {
    const detail = new RunDetailPage(page);
    await detail.openDetail(runDetailPath({ locale, decision: 'evaluated-fail-missing-shard' }));

    // Evidence is the default tab, so the missing shard is readable without switching tabs.
    await expect(detail.evidencePanel.locator('[data-evidence-missing] .reason-code')).toHaveText(
      MISSING_SHARD,
    );

    await detail.showDecision();
    await expect(detail.decisionChip).toHaveAttribute('data-tone', 'danger');
    await expect(detail.decisionPanel.locator('app-decision-chip')).toContainText('FAIL');
    await expect(detail.decisionPanel.locator('.decision-reason-list')).toContainText(
      MISSING_SHARD,
    );

    expect(await detail.text(), 'an incomplete evidence revision is never a PASS').not.toContain(
      'PASS',
    );
  });
}
