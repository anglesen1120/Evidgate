// Corpus rows pin the audit read, support marker, export and refusal contracts.
// Server cursor transitions and retained-row retries live in run-audit-cursor-paging.spec.ts;
// the default audit corpus fits within one twenty-row page.
import { expect, test, type Page } from '@playwright/test';
import { readFile } from 'node:fs/promises';

import auditList from '../../../../fixtures/web-api/audit-list/input.json';
import auditListEmpty from '../../../../fixtures/web-api/audit-list-empty/input.json';
import auditListSupport from '../../../../fixtures/web-api/audit-list-support/input.json';
import { expectNoA11yViolations } from '../a11y/axe-scan';
import {
  expectIndistinguishableNotFound,
  LOCALES,
  MEMBERS_READONLY_GRANTS,
  ORG_ACME,
  seedStoredTheme,
  SETTINGS_AUDIT_ONLY_GRANTS,
  settingsTabPath,
  THEMES,
  waitForDialogAnimations,
  withVariants,
} from './support';

/** `?fixture.variant.listAudit=support` — the same page with two support-access rows on top. */
const AUDIT_SUPPORT_PAGE = '?fixture.variant.listAudit=support';

/** `?fixture.variant.listAudit=empty` — the page with no audit entry at all. */
const AUDIT_EMPTY_PAGE = '?fixture.variant.listAudit=empty';

/** The fixed header row `audit-csv.ts` writes, in the column order every exported line follows. */
const CSV_COLUMNS = [
  'seq',
  'created_at',
  'actor_type',
  'actor_id',
  'action',
  'object_type',
  'object_id',
  'request_id',
  'support_grant_id',
  'metadata',
] as const;

/** The `metadata` key `support-access.ts` reads: the grant a support action was taken under. */
const SUPPORT_GRANT_KEY = 'support_grant_id';

/**
 * One audit row of the corpus, as the fixtures spell it. Declared here (rather than taken from the
 * generated SDK type) because the spec reads the JSON documents directly, and their `metadata` is a
 * different shape in each document.
 */
interface FixtureAuditRow {
  readonly id: string;
  readonly seq: number;
  readonly actor_type: string;
  readonly actor_id: string;
  readonly action: string;
  readonly created_at: string;
  readonly metadata: Readonly<Record<string, unknown>> | null;
}

/** The default page the tab opens with: six entries, no support-access row among them. */
const DEFAULT_ROWS: readonly FixtureAuditRow[] = auditList.body.items;

/** The `support` page: the same log with two `user` rows carrying a support grant on top. */
const SUPPORT_ROWS: readonly FixtureAuditRow[] = auditListSupport.body.items;

/**
 * The support-access marker of one corpus row, by the rule `support-access.ts` implements: the
 * `metadata.support_grant_id` marker alone flags a row here (no corpus row names the wire's
 * `platform_operator` actor — the fixture catalogue header records why), and a plain row is `null`.
 * Kept tri-state, because "operator without a recorded grant" and "ordinary row" render differently.
 */
function supportMarker(row: FixtureAuditRow): { readonly grantId: string | null } | null {
  const marker = row.metadata?.[SUPPORT_GRANT_KEY];
  if (typeof marker === 'string' && marker !== '') {
    return { grantId: marker };
  }
  return row.actor_type === 'platform_operator' ? { grantId: null } : null;
}

/** The rows the chip must appear on, in corpus order (newest first, the server's order). */
const FLAGGED_ROWS: readonly FixtureAuditRow[] = SUPPORT_ROWS.filter(
  (row) => supportMarker(row) !== null,
);

/** The rows that must render without a chip on the same page. */
const PLAIN_ROWS: readonly FixtureAuditRow[] = SUPPORT_ROWS.filter(
  (row) => supportMarker(row) === null,
);

/** The shared table's real records enclose every cell; the actor marker is only one fact. */
const AUDIT_ROWS = 'app-audit-table [data-row-id]';
const SUPPORT_ROWS_SELECTOR = `${AUDIT_ROWS}:has([data-audit-support="true"])`;
const PLAIN_ROWS_SELECTOR = `${AUDIT_ROWS}:has([data-audit-support="false"])`;

const SUPPORT_CHIPS = `${AUDIT_ROWS} [data-audit-support-chip]`;

/** The tab's own phase hook, awaited before any content claim (loading → ready). */
async function expectAuditReady(page: Page): Promise<void> {
  const host = page.locator('app-audit-page');
  await expect(host).toHaveCount(1);
  await expect(host).toHaveAttribute('data-audit-phase', 'ready');
}

/** The `datetime` of each matching row's leading `<time>` cell, in DOM order. */
async function renderedTimestamps(page: Page, selector: string): Promise<string[]> {
  return page
    .locator(`${selector} time[datetime]`)
    .evaluateAll((nodes) => nodes.map((node) => node.getAttribute('datetime') ?? ''));
}

test.describe('AC7: an audit:read session reads the log and exports it', () => {
  for (const locale of LOCALES) {
    test(`the filter form, the default page and the export control render (${locale})`, async ({
      page,
    }) => {
      // Only `audit:read` + `organization:read`: the tab must open on that alone, so nothing here
      // depends on a broader grant set the product happens to ship.
      await page.goto(withVariants(settingsTabPath(locale, 'audit'), [SETTINGS_AUDIT_ONLY_GRANTS]));
      await expectAuditReady(page);

      // Portalled options retain the wire enums in data-value, independently of locale.
      const form = page.locator('app-audit-page form');
      await expect(form).toHaveCount(1);
      const actionField = form.locator('#audit-action');
      const hint = form.locator('button[aria-controls="audit-action-hint"]');
      const note = form.locator('#audit-action-hint');
      const label = await form.locator('label[for="audit-action"]').innerText();
      await expect(hint).toHaveAccessibleName(`${locale === 'vi' ? 'Gợi ý' : 'Hint'}: ${label}`);
      const guidance = (await note.textContent())?.trim();
      expect(guidance).toBeTruthy();
      await expect(actionField).toHaveAccessibleDescription(guidance ?? '');
      await expect(note).toBeHidden();
      for (const key of ['Enter', 'Space']) {
        await hint.press(key);
        await expect(note).toBeVisible();
        await hint.press('Escape');
        await expect(note).toBeHidden();
        await expect(hint).toBeFocused();
      }
      const actor = form.locator('button#audit-actor');
      await expect(actor).toHaveRole('combobox');
      await actor.click();
      const actorOptions = page.getByRole('listbox').getByRole('option');
      await expect(actorOptions).toHaveCount(6);
      expect(
        await actorOptions.evaluateAll((options) =>
          options.map((option) => option.getAttribute('data-value')),
        ),
      ).toEqual(['', 'user', 'ci_producer', 'agent', 'platform_operator', 'system']);
      await expect(page.getByRole('listbox')).toHaveAccessibleName(
        await page.locator('label[for="audit-actor"]').innerText(),
      );
      await waitForDialogAnimations(page, 'hlm-select-content[data-state="open"]');
      await expectNoA11yViolations(page, `Audit actor options ${locale}`);
      await page.keyboard.press('Escape');
      await expect(actor).toHaveAttribute('aria-expanded', 'false');
      await expect(form.locator('input#audit-action')).toBeVisible();
      await expect(form.locator('input#audit-object')).toBeVisible();
      await expect(form.locator('input#audit-from[type="datetime-local"]')).toBeVisible();
      await expect(form.locator('input#audit-to[type="datetime-local"]')).toBeVisible();
      await expect(form.locator('[data-audit-apply]')).toBeEnabled();
      await expect(form.locator('[data-audit-clear]')).toBeEnabled();

      // The table renders the corpus page whole — the count, the order and the action cells are
      // the fixture's, and no row here is a support-access row.
      await expect(page.locator(AUDIT_ROWS)).toHaveCount(DEFAULT_ROWS.length);
      expect(await renderedTimestamps(page, AUDIT_ROWS)).toEqual(
        DEFAULT_ROWS.map((row) => row.created_at),
      );
      expect(
        await page
          .locator(`${AUDIT_ROWS} [data-column-priority="primary"] .value`)
          .evaluateAll((cells) => cells.map((cell) => (cell as HTMLElement).innerText.trim())),
      ).toEqual(
        DEFAULT_ROWS.map((row) =>
          row.action === 'run.cancel'
            ? locale === 'vi'
              ? 'Hủy lượt chạy'
              : 'Run canceled'
            : row.action,
        ),
      );
      expect(
        await page
          .locator(`${AUDIT_ROWS} [data-column-priority="primary"] app-data-table-value`)
          .evaluateAll((cells) => cells.map((cell) => cell.getAttribute('title'))),
      ).toEqual(DEFAULT_ROWS.map((row) => row.action));
      await expect(page.locator(SUPPORT_ROWS_SELECTOR)).toHaveCount(0);
      await expect(page.locator(SUPPORT_CHIPS)).toHaveCount(0);

      // The export control is part of the page, idle until it is used.
      await expect(page.locator('app-audit-export[data-audit-export-phase="idle"]')).toHaveCount(1);
      await expect(page.locator('[data-audit-export]')).toBeEnabled();
      const pager = page.locator('app-data-table-pager');
      await expect(pager.getByRole('combobox')).toContainText('20');
      for (const direction of ['previous', 'next']) {
        await expect(pager.locator(`[data-page="${direction}"]`)).toHaveAttribute(
          'aria-disabled',
          'true',
        );
      }
    });
  }

  test('Export CSV downloads one file whose name and header row are the contract', async ({
    page,
  }) => {
    await page.goto(withVariants(settingsTabPath('en', 'audit'), [SETTINGS_AUDIT_ONLY_GRANTS]));
    await expectAuditReady(page);
    await expect(page.locator(AUDIT_ROWS)).toHaveCount(DEFAULT_ROWS.length);

    const [download] = await Promise.all([
      page.waitForEvent('download'),
      page.locator('[data-audit-export]').click(),
    ]);

    // The name is `evidgate-audit-<orgId>-<yyyy-mm-dd>.csv`: the org of the address and the day of
    // the export, built by `audit-export.component.ts`.
    const today = new Date().toISOString().slice(0, 10);
    expect(download.suggestedFilename()).toMatch(
      new RegExp(`^evidgate-audit-${ORG_ACME}-\\d{4}-\\d{2}-\\d{2}\\.csv$`),
    );
    expect(download.suggestedFilename()).toBe(`evidgate-audit-${ORG_ACME}-${today}.csv`);

    // RFC 4180 lines contain the fixed header and every row of this terminal corpus page.
    const file = await download.path();
    expect(file, 'the download has a file on disk').not.toBeNull();
    const text = await readFile(file ?? '', 'utf8');
    const lines = text.split('\r\n').filter((line) => line !== '');
    expect(text.endsWith('\r\n'), 'every line ends with CRLF, the last one included').toBe(true);
    expect(lines[0]?.startsWith('seq,created_at,actor_type'), 'the header leads the file').toBe(
      true,
    );
    expect(lines[0]).toBe(CSV_COLUMNS.join(','));
    expect(lines.length).toBe(DEFAULT_ROWS.length + 1);

    const newest = DEFAULT_ROWS[0];
    if (newest === undefined) {
      throw new Error('fixtures/web-api/audit-list holds no row');
    }
    expect(lines[1]?.startsWith(`${newest.seq},${newest.created_at},${newest.actor_type},`)).toBe(
      true,
    );

    // The control reports the export it finished, next to the button that ran it.
    await expect(page.locator('[data-audit-export-done]')).toHaveCount(1);
  });
});

test.describe('AC8: the support-access chip marks exactly the flagged rows', () => {
  for (const locale of LOCALES) {
    test(`only the rows that record a grant carry the chip (${locale})`, async ({ page }) => {
      await page.goto(
        withVariants(settingsTabPath(locale, 'audit'), [
          SETTINGS_AUDIT_ONLY_GRANTS,
          AUDIT_SUPPORT_PAGE,
        ]),
      );
      await expectAuditReady(page);

      // One render holds both cases: flagged rows with a chip, plain rows without one.
      await expect(page.locator(AUDIT_ROWS)).toHaveCount(SUPPORT_ROWS.length);
      await expect(page.locator(SUPPORT_ROWS_SELECTOR)).toHaveCount(FLAGGED_ROWS.length);
      await expect(page.locator(PLAIN_ROWS_SELECTOR)).toHaveCount(PLAIN_ROWS.length);
      await expect(page.locator(SUPPORT_CHIPS)).toHaveCount(FLAGGED_ROWS.length);

      // Which rows: the flagged ones are the corpus rows carrying the marker, in the server's
      // newest-first order — and no plain row carries a chip.
      expect(await renderedTimestamps(page, SUPPORT_ROWS_SELECTOR)).toEqual(
        FLAGGED_ROWS.map((row) => row.created_at),
      );
      for (const row of PLAIN_ROWS) {
        const plain = page.locator(PLAIN_ROWS_SELECTOR, {
          has: page.locator(`time[datetime="${row.created_at}"]`),
        });
        await expect(plain).toHaveCount(1);
        await expect(plain.locator('[data-audit-support-chip]')).toHaveCount(0);
      }

      // The description names the grant the row records — never a grant derived from the actor or
      // the request, and never a blank chip.
      for (const row of FLAGGED_ROWS) {
        const grantId = supportMarker(row)?.grantId ?? '';
        expect(grantId, `the corpus flags ${row.id} with a grant id`).not.toBe('');
        const record = page.locator(AUDIT_ROWS, {
          has: page.locator(`time[datetime="${row.created_at}"]`),
        });
        await record.locator('[data-audit-view]').click();
        await expect(page.locator('[data-audit-dialog]')).toHaveCSS('width', '560px');
        const chip = page.getByRole('dialog').locator('.support-access');
        await expect(chip).toHaveClass(/support-access/);
        await expect(chip).toHaveAccessibleName(/\S/);
        await chip.focus();
        const tooltip = page.getByRole('tooltip');
        await expect(tooltip).toBeVisible();
        await expect(tooltip).toContainText(grantId);
        await expect(chip).toHaveAttribute(
          'aria-describedby',
          (await tooltip.getAttribute('id')) ?? '',
        );
        await page.keyboard.press('Escape');
        await expect(tooltip).not.toBeVisible();
        await page.keyboard.press('Escape');
        await expect(record.locator('[data-audit-view]')).toBeFocused();
      }
      // Restored row-action focus exposes its tooltip after every chip tooltip has closed.
      await expect(page.getByRole('tooltip')).toHaveCount(1);
    });

    test(`the chip is reachable from the keyboard and its description is its label (${locale})`, async ({
      page,
    }) => {
      await page.goto(
        withVariants(settingsTabPath(locale, 'audit'), [
          SETTINGS_AUDIT_ONLY_GRANTS,
          AUDIT_SUPPORT_PAGE,
        ]),
      );
      await expectAuditReady(page);

      // From the dialog heading, Request Copy and Support access are the first two stops.
      for (const row of FLAGGED_ROWS) {
        const record = page.locator(AUDIT_ROWS, {
          has: page.locator(`time[datetime="${row.created_at}"]`),
        });
        const trigger = record.locator('[data-audit-view]');
        await trigger.focus();
        await page.keyboard.press('Enter');
        const details = page.getByRole('dialog');
        await expect(details).toBeVisible();
        await page.keyboard.press('Tab');
        const requestCopy = details.locator('app-copyable-identifier').first().getByRole('button');
        await expect(requestCopy).toHaveCount(1);
        await expect(requestCopy).toBeFocused();
        await page.keyboard.press('Tab');
        const chip = page.getByRole('dialog').locator('.support-access');
        await expect(chip).toBeFocused();
        await expect(chip).toHaveClass(/support-access/);
        await expect(chip).toHaveAccessibleName(/\S/);
        const tooltip = page.getByRole('tooltip');
        await expect(tooltip).toBeVisible();
        const descriptionId = await tooltip.getAttribute('id');
        expect(descriptionId).toBeTruthy();
        await expect(chip).toHaveAttribute('aria-describedby', descriptionId ?? '');
        const grantId = supportMarker(row)?.grantId ?? '';
        expect(grantId).not.toBe('');
        await expect(tooltip).toContainText(grantId);
        await page.keyboard.press('Escape');
        await expect(tooltip).not.toBeVisible();
        await expect(chip).toBeFocused();
        await page.keyboard.press('Escape');
        await expect(trigger).toBeFocused();
      }
    });
  }
});

test.describe('AC7: an org with no audit activity yet', () => {
  for (const locale of LOCALES) {
    test(`the empty page renders the shared empty pattern (${locale})`, async ({ page }) => {
      await page.goto(
        withVariants(settingsTabPath(locale, 'audit'), [
          SETTINGS_AUDIT_ONLY_GRANTS,
          AUDIT_EMPTY_PAGE,
        ]),
      );
      await expectAuditReady(page);

      // Ready with nothing in it — not the loading skeleton and not an error: the shared state
      // component answers `empty` on its own (09.1.1's `app-request-state`). The corpus document is
      // itself empty, so this cannot pass on a page that silently dropped its rows.
      expect(auditListEmpty.body.items.length, 'the empty page holds no row').toBe(0);
      await expect(page.locator('app-empty-pattern')).toHaveCount(1);
      await expect(page.locator('app-error-pattern')).toHaveCount(0);
      await expect(page.locator('app-loading-pattern')).toHaveCount(0);
      await expect(page.locator(AUDIT_ROWS)).toHaveCount(0);

      // The export stays available: "no rows on this page" is not "no export", and the download of
      // an empty log is still an honest document (the CSV header alone).
      await expect(page.locator('[data-audit-export]')).toBeEnabled();
    });
  }
});

test.describe('AC1: a caller without audit:read gets the not-found page, not a refusal', () => {
  test('the audit address is indistinguishable from a path that never existed', async ({
    page,
  }) => {
    // `settings-members-readonly` holds `membership:read`, `organization:read` and `role:read` —
    // another Settings tab is open, so the 404 cannot be "no Settings tab at all", and it must not
    // be a 403 either (09.7.1-T1 AC1, `settings-tabs.spec.ts`). That suite's cases cover the
    // *members* and *agents* tabs against other grant sets; this tab's refusal is asserted here.
    const refused = withVariants(settingsTabPath('en', 'audit'), [MEMBERS_READONLY_GRANTS]);
    const unknown = withVariants(settingsTabPath('en', 'zzz-unknown'), [MEMBERS_READONLY_GRANTS]);
    const rendered = await expectIndistinguishableNotFound(page, [refused, unknown]);

    // The refusal never names what it refused, and no part of the tab rendered behind the pattern.
    expect(
      rendered
        .map((entry) => entry.text)
        .join(' ')
        .toLowerCase(),
    ).not.toContain('audit');
    await expect(page.locator('app-audit-page')).toHaveCount(0);
    await expect(page.locator(AUDIT_ROWS)).toHaveCount(0);
  });
});

test.describe('the Audit tab is WCAG 2.2 AA clean (00 §5.6 W9)', () => {
  // Every state this build gives the page: the plain page, the one carrying support-access chips
  // (a native button with a portalled tooltip) and
  // the empty state, whose shared pattern action is the only control on it.
  const STATES = [
    { name: 'default', variant: '' },
    { name: 'support', variant: AUDIT_SUPPORT_PAGE },
    { name: 'empty', variant: AUDIT_EMPTY_PAGE },
  ] as const;

  for (const state of STATES) {
    for (const theme of THEMES) {
      test(`${state.name} page, en bundle, ${theme} tokens`, async ({ page }) => {
        await seedStoredTheme(page, theme);
        await page.goto(
          withVariants(settingsTabPath('en', 'audit'), [SETTINGS_AUDIT_ONLY_GRANTS, state.variant]),
        );
        await expectAuditReady(page);

        await expectNoA11yViolations(page, `audit ${state.name} en/${theme}`);
      });
    }
  }
});

test.describe('at 360px the page does not scroll sideways', () => {
  test('labelled audit records retain full details without sideways document scroll', async ({
    page,
  }) => {
    await page.setViewportSize({ width: 360, height: 740 });
    await page.goto(
      withVariants(settingsTabPath('en', 'audit'), [
        SETTINGS_AUDIT_ONLY_GRANTS,
        AUDIT_SUPPORT_PAGE,
      ]),
    );
    await expectAuditReady(page);
    await expect(page.locator(AUDIT_ROWS)).toHaveCount(SUPPORT_ROWS.length);

    const records = page.locator('app-audit-table app-data-table-records').getByRole('list');
    await expect(records).toBeVisible();
    await expect(records.getByRole('listitem')).toHaveCount(SUPPORT_ROWS.length);
    const overflow = await page.evaluate(
      () => document.documentElement.scrollWidth - document.documentElement.clientWidth,
    );
    expect(overflow).toBeLessThanOrEqual(0);
    const caption = page.locator('app-audit-table app-data-table > .caption');
    await expect(caption).toBeVisible();
    await expect(records).toHaveAccessibleName((await caption.innerText()).trim());
    const captionBox = await caption.boundingBox();
    const tableBox = await page.locator('app-audit-table app-data-table').boundingBox();
    if (captionBox === null || tableBox === null)
      throw new Error('audit caption or records have no box');
    expect(captionBox.width).toBeCloseTo(tableBox.width, 0);
    for (const record of await records.getByRole('listitem').all()) {
      const terms = record.locator(':scope > dl > .eg-table-fact > dt');
      expect(await terms.count()).toBeGreaterThan(0);
      await expect(record.locator(':scope > dl > .eg-table-fact > dd')).toHaveCount(
        await terms.count(),
      );
      for (const term of await terms.all()) await expect(term).toBeVisible();
    }
    for (const row of FLAGGED_ROWS) {
      const supportRow = page.locator(AUDIT_ROWS, {
        has: page.locator(`time[datetime="${row.created_at}"]`),
      });
      const trigger = supportRow.locator('[data-audit-view]');
      await trigger.focus();
      await page.keyboard.press('Enter');
      const details = page.getByRole('dialog');
      await expect(details).toBeVisible();
      await expect(details.locator('[data-audit-dialog]')).toHaveCSS('width', '360px');
      await expect(details.locator('[data-audit-dialog]')).toHaveCSS('height', '740px');
      const description = details.locator('.support-access + span');
      await expect(description).toBeVisible();
      const grantId = supportMarker(row)?.grantId;
      if (grantId === undefined || grantId === null) throw new Error('flagged row has no grant id');
      await expect(description).toContainText(grantId);
      await page.keyboard.press('Escape');
      await expect(trigger).toBeFocused();
    }

    // Narrow inspection needs no overflow region: disclosures remain keyboard-operable above.
    await expect(page.locator('app-audit-table [role="region"]')).toHaveCount(0);

    await expectNoA11yViolations(page, 'audit support 360px en/light');
  });
});
