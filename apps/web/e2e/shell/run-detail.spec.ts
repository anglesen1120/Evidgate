// 09.3.1-T2: fixture-backed real page, both locale bundles and theme token sets.
import { expect, test, type Page } from '@playwright/test';
import attemptsFixture from '../../../../fixtures/web-api/run-attempts/input.json';
import readyFixture from '../../../../fixtures/web-api/run-detail/input.json';
import collecting from '../../../../fixtures/web-api/run-detail-collecting/input.json';
import runList from '../../../../fixtures/web-api/run-list/input.json';
import { expectNoA11yViolations } from '../a11y/axe-scan';
import {
  localePath,
  LOCALES,
  ORG_ACME,
  PROJECT_STOREFRONT,
  seedStoredTheme,
  THEMES,
  withVariants,
  type ShellLocale,
} from './support';

const RUN = readyFixture.body;
const GRANT = '?fixture.variant.listPermissions=settings-policy-readonly';
function path(
  locale: ShellLocale,
  variant = 'detail-ready',
  id = RUN.run_id,
  attempts = 'ok',
  revisions = 'ok',
) {
  return withVariants(
    localePath(
      locale,
      `/dev/run-detail/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}/runs/${id}`,
    ),
    [
      GRANT,
      `?fixture.variant.getRun=${variant}`,
      `?fixture.variant.listRunAttempts=${attempts}`,
      `?fixture.variant.listEvidenceRevisions=${revisions}`,
    ],
  );
}
async function ready(page: Page) {
  await expect(page.locator('app-run-detail-page')).toHaveAttribute(
    'data-run-detail-status',
    'ready',
  );
}
function cancelPath(locale: ShellLocale, variant: string, runId: string) {
  return withVariants(
    localePath(
      locale,
      `/dev/run-detail/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}/runs/${runId}`,
    ),
    ['?fixture.variant.listPermissions=run-actions', `?fixture.variant.getRun=${variant}`],
  );
}
async function openDisclosure(page: Page, details: string) {
  const disclosure = page.locator(details);
  await disclosure.locator(':scope > summary').click();
  await expect(disclosure).toHaveJSProperty('open', true);
}
for (const locale of LOCALES) {
  test(`deep link reload retains stored identity, manifest and attempts (${locale})`, async ({
    page,
  }) => {
    await page.goto(path(locale));
    await ready(page);
    await expect(page.locator('[data-run-id] > bdi')).toHaveText(RUN.run_id);
    await expect(page.locator('[data-run-version]')).toHaveText(String(RUN.version));
    await expect(page.locator('[data-run-purpose]')).toHaveText(
      locale === 'vi' ? 'Chạy lại từ dữ liệu' : 'Replay',
    );
    await openDisclosure(page, 'app-run-header details');
    await expect(page.locator('[data-run-evidence-revision] > bdi')).toHaveText(
      RUN.current_evidence_revision_id,
    );
    await expect(page.locator('[data-run-environment] bdi')).toHaveText(
      RUN.environment.environment_key,
    );
    await expect(page.locator('[data-run-environment]')).toHaveJSProperty('tagName', 'DD');
    await expect(page.locator('app-run-header [data-run-policy]')).toHaveCount(0);
    await expect(page.locator('[data-run-fork]')).toHaveText(locale === 'vi' ? 'Không' : 'No');
    await openDisclosure(page, 'app-manifest-panel details');
    await expect(page.locator('app-manifest-panel [data-manifest-hash] bdi')).toHaveText(
      RUN.manifest_hash,
    );
    await expect(page.locator('app-manifest-panel li bdi')).toHaveText(RUN.declared_producers);
    await expect(page.locator('app-manifest-panel app-status-pill')).toHaveCount(0);
    await expect(page.locator('app-attempts-table [data-attempt-row]')).toHaveCount(
      attemptsFixture.body.items.length,
    );
    await expect(
      page.locator('app-attempts-table [data-attempts-healing-unavailable]'),
    ).toContainText(locale === 'vi' ? 'Chưa được cung cấp' : 'Not provided');
    await expect(page.locator('app-run-detail-page h1')).toHaveCount(1);
    // The default decision fixture is a served `pending` body: its chip is the pending one and no
    // verdict chip or outcome pill exists.
    await expect(page.locator('app-run-detail-page app-decision-chip [data-tone]')).toHaveAttribute(
      'data-tone',
      'pending',
    );
    await expect(page.locator('app-run-detail-page app-outcome-pill')).toHaveCount(0);
    expect(await page.locator('app-run-detail-page').textContent()).not.toContain('PASS');
    await page.reload();
    await ready(page);
    await expect(page.locator('[data-run-id] > bdi')).toHaveText(RUN.run_id);
    await openDisclosure(page, 'app-run-header details');
    await expect(page.locator('[data-run-evidence-revision] > bdi')).toHaveText(
      RUN.current_evidence_revision_id,
    );
  });
  test(`terminal runs hide cancel and say why; active runs keep it (${locale})`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto(cancelPath(locale, 'detail-ready', RUN.run_id));
    await ready(page);
    await expect(page.locator('[data-run-cancel-trigger]')).toHaveCount(0);
    await expect(page.locator('#run-cancel-disabled-reason')).toContainText(
      locale === 'vi' ? 'đã ở trạng thái kết thúc' : 'already terminal',
    );
    await page.goto(cancelPath(locale, 'detail-cancel-poll', collecting.body.run_id));
    await ready(page);
    const trigger = page.locator('[data-run-cancel-trigger]');
    await expect(trigger).toBeVisible();
    await expect(trigger).not.toHaveAttribute('aria-disabled', 'true');
    await expect(page.locator('#run-cancel-disabled-reason')).toHaveCount(0);
  });
  test(`attempts server pages replace rows, go back and reset selected size (${locale})`, async ({
    page,
  }) => {
    await page.goto(path(locale, 'detail-ready', RUN.run_id, 'page-1'));
    await ready(page);
    const rows = page.locator('app-attempts-table [data-attempt-row]');
    const pager = page.locator('app-attempts-table app-data-table-pager');
    const next = pager.locator('[data-page="next"]');
    const previous = pager.locator('[data-page="previous"]');
    await expect(rows).toHaveCount(4);
    const firstPage = await rows.allTextContents();
    await expect(pager.locator('hlm-select-trigger button')).toContainText('20');
    await expect(previous).toHaveAttribute('aria-disabled', 'true');
    await next.focus();
    await page.keyboard.press('Enter');
    await expect(rows).toHaveCount(3);
    await expect(next).toHaveAttribute('aria-disabled', 'true');
    await expect(previous).toHaveAttribute('aria-disabled', 'false');
    await expect(next).toBeFocused();
    await previous.click();
    await expect(rows).toHaveCount(4);
    expect(await rows.allTextContents()).toEqual(firstPage);
    await expect(previous).toHaveAttribute('aria-disabled', 'true');
    await next.click();
    await expect(rows).toHaveCount(3);
    await pager.locator('hlm-select-trigger button').click();
    await page.getByRole('option', { name: '50', exact: true }).click();
    await expect(pager.locator('hlm-select-trigger button')).toContainText('50');
    await expect(rows).toHaveCount(4);
    await expect(previous).toHaveAttribute('aria-disabled', 'true');
  });
  test(`empty and unavailable attempts reads render their own states (${locale})`, async ({
    page,
  }) => {
    await page.goto(path(locale, 'detail-ready', RUN.run_id, 'empty'));
    await ready(page);
    await expect(page.locator('app-attempts-table app-empty-pattern')).toBeVisible();
    await page.goto(path(locale, 'detail-ready', RUN.run_id, 'unavailable'));
    await ready(page);
    await expect(page.locator('app-attempts-table app-error-pattern')).toBeVisible();
    await expect(page.locator('app-run-header .run-summary')).toBeVisible();
  });
  test(`foreign and missing ids share one 404 view; 403 is separate (${locale})`, async ({
    page,
  }) => {
    await page.goto(path(locale, 'detail-not-found', '018f0000-0000-7000-8000-000000000fa1'));
    await expect(page.locator('app-run-detail-page')).toHaveAttribute(
      'data-run-detail-status',
      'not-found',
    );
    await expect(page.locator('app-not-found-pattern')).toBeVisible();
    const foreign = await page.locator('app-run-detail-page').textContent();
    await page.goto(path(locale, 'detail-not-found', '018f0000-0000-7000-8000-000000000fa2'));
    await expect(page.locator('app-not-found-pattern')).toBeVisible();
    expect(await page.locator('app-run-detail-page').textContent()).toBe(foreign);
    await expect(page.locator('app-run-header')).toHaveCount(0);
    await page.goto(path(locale, 'detail-forbidden'));
    await expect(page.locator('app-run-detail-page')).toHaveAttribute(
      'data-run-detail-status',
      'forbidden',
    );
    await expect(page.locator('app-forbidden-pattern')).toBeVisible();
    await expect(page.locator('app-run-header')).toHaveCount(0);
  });
  test(`ref formatting is escaped and null evidence never implies pass (${locale})`, async ({
    page,
  }) => {
    await page.goto(path(locale, 'detail-ref-format'));
    await ready(page);
    await expect(page.locator('[data-run-ref] bdi')).toHaveText('detail/việt⟨U+202E⟩<script>');
    expect(await page.locator('[data-run-ref]').textContent()).not.toContain('\u202e');
    await expect(page.locator('[data-run-unverified]')).toBeVisible();
    await expect(page.locator('app-run-detail-page script')).toHaveCount(0);
    await page.goto(path(locale, 'detail-null-evidence', RUN.run_id, 'ok', 'empty'));
    await ready(page);
    await expect(page.locator('[data-run-evidence-revision]')).toHaveText(
      locale === 'vi' ? 'Chưa ghi nhận' : 'None recorded',
    );
    // A run with no current evidence revision has no frozen revision page either.
    await expect(page.locator('#evidence-panel [data-evidence-empty]')).toBeVisible();
    expect(await page.locator('app-run-detail-page').textContent()).not.toContain('PASS');
  });
  test(`list row navigation and browser back preserve filters (${locale})`, async ({ page }) => {
    const listPath = localePath(
      locale,
      `/dev/run-list/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}`,
    );
    await page.goto(
      withVariants(listPath, [
        GRANT,
        '?fixture.variant.listRuns=runs',
        '?fixture.variant.getRun=detail-ready',
        '?state=complete&ref=main',
      ]),
    );
    await expect(page.locator('app-run-list-page')).toHaveAttribute(
      'data-run-list-status',
      'ready',
    );
    const before = page.url();
    const first = runList.body.items[0]!;
    const rowLink = page.locator(`[data-run-row][data-run-id="${first.run_id}"] a`);
    await rowLink.focus();
    await page.keyboard.press('Enter');
    await ready(page);
    expect(new URL(page.url()).pathname).toBe(`${listPath}/${first.run_id}`);
    expect(new URL(page.url()).searchParams.get('state')).toBe('complete');
    expect(new URL(page.url()).searchParams.get('ref')).toBe('main');
    await page.reload();
    await ready(page);
    await page.goBack();
    await expect(page.locator('app-run-list-page')).toHaveAttribute(
      'data-run-list-status',
      'ready',
    );
    expect(page.url()).toBe(before);
    await expect(page.locator('#run-state')).toContainText(
      locale === 'vi' ? 'Hoàn tất' : 'Complete',
    );
    await expect(page.locator('#run-ref')).toHaveValue('main');
  });
}
for (const locale of LOCALES) {
  for (const theme of THEMES) {
    test(`WCAG and mobile containment (${locale}, ${theme})`, async ({ page }) => {
      await seedStoredTheme(page, theme);
      await page.setViewportSize({ width: 360, height: 800 });
      await page.goto(path(locale, 'detail-ref-format'));
      await ready(page);
      const back = page.locator('app-run-detail-page app-page-header a');
      await back.focus();
      await expect(back).toBeFocused();
      expect((await back.boundingBox())?.height).toBeGreaterThanOrEqual(44);
      expect(
        await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth),
      ).toBe(true);
      await expectNoA11yViolations(page, `run detail ${locale}/${theme}/360px`);
      await page.setViewportSize({ width: 1440, height: 900 });
      expect(
        await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth),
      ).toBe(true);
      await expectNoA11yViolations(page, `run detail ${locale}/${theme}/1440px`);
    });
  }
}
