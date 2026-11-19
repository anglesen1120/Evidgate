import { expect, test, type Locator, type Page } from '@playwright/test';
import { expectNoA11yViolations } from '../a11y/axe-scan';
import {
  FIXTURE_REQUEST_LOG,
  type FixtureCallRecord,
} from '../../src/app/core/dev-fixtures/fixture-request-log';
import {
  localePath,
  LOCALES,
  ORG_ACME,
  PROJECT_STOREFRONT,
  seedStoredTheme,
  THEMES,
  withVariant,
} from './support';

const COLUMNS =
  'kind,key,parent_key,item_key,node_kind,title,description,precondition,steps,priority,state,code,position';
const CSV = COLUMNS + '\nnode,n1,,,module,Checkout,,,,,,,0\n';
const base = `/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}/authoring/import-export`;
async function calls(page: Page, operation: 'import' | 'export'): Promise<FixtureCallRecord[]> {
  return page.evaluate(
    ({ key, suffix }) =>
      (window[key] ?? []).filter((request) =>
        new URL(request.url, location.origin).pathname.endsWith(suffix),
      ),
    { key: FIXTURE_REQUEST_LOG, suffix: '/authoring/' + operation },
  );
}
async function keyboardTo(page: Page, target: Locator): Promise<void> {
  for (let step = 0; step < 140; step++) {
    if (await target.evaluate((element) => element === document.activeElement)) return;
    await page.keyboard.press('Tab');
  }
  await expect(target).toBeFocused();
}
async function pick(page: Page): Promise<void> {
  await page
    .locator('[data-import-file]')
    .setInputFiles({ name: 'authoring.csv', mimeType: 'text/csv', buffer: Buffer.from(CSV) });
}
async function selectJson(page: Page): Promise<void> {
  await page.locator('#authoring-export-format').click();
  await page.getByRole('option', { name: 'JSON', exact: true }).click();
  await expect(page.getByRole('listbox')).toHaveCount(0);
}

for (const locale of LOCALES) {
  for (const theme of THEMES) {
    for (const width of [360, 1280]) {
      test(`import is confirm-only and export stays accessible (${locale}, ${theme}, ${width})`, async ({
        page,
      }) => {
        await page.setViewportSize({ width, height: 900 });
        await seedStoredTheme(page, theme);
        await page.goto(localePath(locale, base));
        await expect(page.locator('main h1')).toHaveCount(1);
        expect(await calls(page, 'import')).toEqual([]);
        if (width === 360) {
          await expect(page.locator('[data-import-file]')).toHaveCount(0);
          await expect(page.locator('[data-import-confirm]')).toHaveCount(0);
        } else {
          await expect(page.locator('[data-import-confirm]')).toBeDisabled();
          await pick(page);
          await expect(page.locator('app-import-preview')).toContainText('Checkout');
          expect(await calls(page, 'import')).toEqual([]);
          await keyboardTo(page, page.locator('[data-import-preview]'));
          await page.keyboard.press('Enter');
          await expect(page.locator('[data-import-confirm]')).toBeEnabled();
          const preview = (await calls(page, 'import'))[0];
          expect(new URL(preview!.url, 'https://example.test').searchParams.get('dry_run')).toBe(
            'true',
          );
          await page.locator('[data-import-confirm]').click();
          await expect(page.locator('[data-import-success]')).toBeVisible();
          const imported = await calls(page, 'import');
          expect(imported).toHaveLength(2);
          expect(
            new URL(imported[1]!.url, 'https://example.test').searchParams.get('dry_run'),
          ).toBe('false');
          expect(imported[1]!.headers['idempotency-key']).not.toEqual(
            imported[0]!.headers['idempotency-key'],
          );
          await expect(page.locator('[data-import-success]')).toContainText(
            '018f0000-0000-7000-8000-00000000af02',
          );
        }
        await expectNoA11yViolations(page, `authoring import ${locale} ${theme} ${width}`);
        await page.locator('[data-impex-export-tab]').click();
        await expect(page.locator('[data-export-selected]')).toBeDisabled();
        await expect(page.locator('[data-export-filtered]')).toBeDisabled();
        await selectJson(page);
        const opener = page.locator('[data-export-download]');
        await keyboardTo(page, opener);
        await page.keyboard.press('Enter');
        await expect(page.locator('[data-export-cancel]')).toBeFocused();
        await expect(page.getByRole('dialog')).toContainText('Storefront');
        expect(await calls(page, 'export')).toEqual([]);
        await page.keyboard.press('Escape');
        await expect(page.getByRole('dialog')).toHaveCount(0);
        await expect(opener).toBeFocused();
        await opener.click();
        await expect(page.locator('[data-export-cancel]')).toBeFocused();
        const downloaded = page.waitForEvent('download');
        await page.locator('[data-export-confirm]').click();
        expect((await downloaded).suggestedFilename()).toBe('authoring-export.json');
        const exported = await calls(page, 'export');
        expect(exported).toHaveLength(1);
        const params = new URL(exported[0]!.url, 'https://example.test').searchParams;
        expect(Object.fromEntries(params)).toEqual({
          format: 'json',
          include: 'nodes,items,cases',
        });
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(
          true,
        );
        await expectNoA11yViolations(page, `authoring import/export ${locale} ${theme} ${width}`);
      });
    }
  }

  for (const [variant, reason] of [
    ['mixed', 'invalid_text'],
    ['duplicate', 'duplicate_key'],
    ['prefix', 'code_conflict'],
    ['file', 'unsupported_format'],
    ['header', 'unsupported_format'],
    ['format', 'unsupported_format'],
  ] as const) {
    test(`import ${variant} refusal focuses its summary and never writes (${locale})`, async ({
      page,
    }) => {
      await page.setViewportSize({ width: 1280, height: 900 });
      await page.goto(
        withVariant(localePath(locale, base), `fixture.variant.importAuthoring=${variant}`),
      );
      await pick(page);
      await page.locator('[data-import-preview]').click();
      const summary = page.locator('#authoring-import-errors');
      await expect(summary).toBeVisible();
      await expect(summary).toBeFocused();
      await expect(summary).toContainText(reason);
      await expect(page.locator('[data-import-confirm]')).toBeDisabled();
      await expect(page.locator('[data-import-success]')).toHaveCount(0);
      const first = summary.getByRole('link').first();
      const id = (await first.getAttribute('href'))!;
      await first.click();
      await expect(page.locator(id)).toBeFocused();
      const requests = await calls(page, 'import');
      expect(requests).toHaveLength(1);
      expect(new URL(requests[0]!.url, 'https://example.test').searchParams.get('dry_run')).toBe(
        'true',
      );
      await expectNoA11yViolations(page, `authoring import ${variant} ${locale}`);
    });
  }

  test(`read-only permission refuses import without hiding export (${locale})`, async ({
    page,
  }) => {
    await page.goto(
      withVariant(localePath(locale, base), 'fixture.variant.listPermissions=limited'),
    );
    await expect(page.locator('[data-import-file]')).toHaveCount(0);
    await page.locator('[data-impex-export-tab]').click();
    await expect(page.locator('[data-export-download]')).toBeEnabled();
    expect(await calls(page, 'import')).toEqual([]);
  });

  test(`empty node and unavailable selection scopes never broaden to a project export (${locale})`, async ({
    page,
  }) => {
    await page.goto(localePath(locale, base + '?ids=selected-case'));
    await page.locator('[data-impex-export-tab]').click();
    await expect(page.locator('[data-export-unavailable]')).toBeVisible();
    await expect(page.locator('[data-export-download]')).toBeDisabled();
    expect(await calls(page, 'export')).toEqual([]);
  });

  test(`unchanged import is an honest no-op with no fabricated batch (${locale})`, async ({
    page,
  }) => {
    await page.goto(withVariant(localePath(locale, base), 'fixture.variant.importAuthoring=noop'));
    await pick(page);
    await page.locator('[data-import-preview]').click();
    await expect(page.locator('[data-import-confirm]')).toBeEnabled();
    await page.locator('[data-import-confirm]').click();
    await expect(page.locator('[data-import-success]')).toContainText(
      locale === 'vi' ? 'Không có gì để nhập' : 'Nothing to import',
    );
    await expect(page.locator('[data-import-success] app-copyable-identifier')).toHaveCount(0);
  });

  test(`export 413 explains the scope refusal without downloading a truncated file (${locale})`, async ({
    page,
  }) => {
    await page.goto(
      withVariant(localePath(locale, base), 'fixture.variant.exportAuthoring=too-large'),
    );
    await page.locator('[data-impex-export-tab]').click();
    await page.locator('[data-export-download]').click();
    await page.locator('[data-export-confirm]').click();
    await expect(page.locator('app-authoring-export-panel app-request-state')).toContainText(
      locale === 'vi' ? 'nhỏ hơn' : 'smaller node scope',
    );
    await expect(page.getByRole('dialog')).toHaveCount(0);
  });

  test(`XLSX export is announced unavailable and stays unselectable (${locale})`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto(localePath(locale, base));
    await page.locator('[data-impex-export-tab]').click();
    await expect(page.locator('[data-export-xlsx-reason]')).toBeVisible();
    await page.locator('#authoring-export-format').click();
    const xlsx = page.getByRole('option', { name: /XLSX/ });
    await expect(xlsx).toHaveAttribute('aria-disabled', 'true');
    await expect(xlsx).toHaveAttribute('aria-describedby', 'export-xlsx-reason');
    await xlsx.click({ force: true });
    await page.keyboard.press('Escape');
    await expect(page.locator('#authoring-export-format')).toContainText('CSV');
    expect(await calls(page, 'export')).toEqual([]);
  });
}
