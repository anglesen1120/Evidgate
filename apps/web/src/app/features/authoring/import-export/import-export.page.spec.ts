import { computed, signal } from '@angular/core';
import { ComponentHarness, TestKey } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import type { AuthoringVersionListResponse, ImportResponse } from '@evidgate/sdk';
import { of } from 'rxjs';
import type { MockInstance } from 'vitest';
import { SessionStore } from '../../../core/session/session.store';
import { ApiRequestError, type ApiError } from '../../../core/http/api-error';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow/narrow-mutation-policy';
import { ImportExportPage } from './import-export.page';
import { ImportExportDataService } from './import-export-data.service';
import { ImportStore } from './import.store';
import { ExportStore } from './export.store';
import { AUTHORING_READ, IMPEX_COPY } from './import-export.copy';
import { IMPORT_COLUMNS } from './local-file-preview';

provideLayoutTestStubs();
const parsed = IMPORT_COLUMNS.join(',') + '\nnode,n1,,,module,Safe title,,,,,,,0\n';
function file(text = parsed): File {
  const source = new File([text], 'preview.csv', { type: 'text/csv' });
  Object.defineProperty(source, 'text', { value: () => Promise.resolve(text) });
  return source;
}
const serverReport: ImportResponse = {
  batch_id: null,
  counts: { created: 1, updated: 0, restored: 0, unchanged: 0 },
  rows: [{ index: 1, key: 'n1', kind: 'node', outcome: 'create', code: null }],
};
const history: AuthoringVersionListResponse = {
  items: [
    {
      version_id: '018f0000-0000-7000-8000-00000000af11',
      entity_kind: 'feature_node',
      entity_id: '018f0000-0000-7000-8000-00000000af12',
      version_no: 3,
      batch_id: '018f0000-0000-7000-8000-00000000af13',
      change_kind: 'import',
      source: 'import',
      content_hash: 'sha256:' + 'a'.repeat(64),
      author: null,
      reason: null,
      ai_run_id: null,
      created_at: '2026-11-19T10:00:00Z',
    },
  ],
  next_cursor: null,
};
function refusal(details: ApiError['details'], kind: ApiError['kind'] = 'validation') {
  return new ApiRequestError({
    kind,
    status: kind === 'payload_too_large' ? 413 : 422,
    code: kind,
    message: 'PRIVATE REFUSED CELL',
    requestId: null,
    retryable: false,
    retryAfterSeconds: null,
    details,
  });
}

class ImportPageHarness extends ComponentHarness {
  static hostSelector = 'app-authoring-import-export-page';
  text() {
    return this.host().then((host) => host.text());
  }
  async count(selector: string) {
    return (await this.locatorForAll(selector)()).length;
  }
  async has(selector: string) {
    return (await this.locatorForOptional(selector)()) !== null;
  }
  async textAt(selector: string) {
    return (await this.locatorFor(selector)()).text();
  }
  async attr(selector: string, attribute: string) {
    return (await this.locatorFor(selector)()).getAttribute(attribute);
  }
  async click(selector: string) {
    await (await this.locatorFor(selector)()).click();
  }
  async key(selector: string, key: TestKey) {
    await (await this.locatorFor(selector)()).sendKeys(key);
  }
  async focus(selector: string) {
    await (await this.locatorFor(selector)()).focus();
  }
  async focused(selector: string) {
    return (await this.locatorFor(selector)()).isFocused();
  }
}

class ImportOptionHarness extends ComponentHarness {
  static hostSelector = '[role="option"]';
  text() {
    return this.host().then((host) => host.text());
  }
  async attr(name: string) {
    return (await this.host()).getAttribute(name);
  }
}

describe('ImportExportPage presentation and route heading contract', () => {
  const upload = vi.fn<ImportExportDataService['importFile']>();
  const versions =
    vi.fn<(project: string, signal?: AbortSignal) => Promise<AuthoringVersionListResponse>>();
  const desktop = signal(true);
  const readable = signal(true);
  let errors: MockInstance;
  beforeEach(() => {
    desktop.set(true);
    readable.set(true);
    upload.mockReset().mockResolvedValue(serverReport);
    versions.mockReset().mockResolvedValue(history);
    errors = vi.spyOn(console, 'error');
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        {
          provide: SessionStore,
          useValue: {
            currentProject: signal({ id: 'project', name: 'Storefront', case_prefix: 'SHOP' }),
            has: (permission: string) => permission !== AUTHORING_READ || readable(),
            onSessionEnd: () => () => undefined,
          },
        },
        {
          provide: NarrowMutationPolicy,
          useValue: {
            canMutate: desktop,
            narrow: computed(() => !desktop()),
            allowsMutation: () => desktop(),
          },
        },
        {
          provide: ActivatedRoute,
          useValue: {
            snapshot: { data: { pageHeadingLevel: 1 } },
            queryParamMap: of(convertToParamMap({})),
          },
        },
      ],
    });
    TestBed.overrideComponent(ImportExportPage, {
      set: {
        providers: [
          ImportStore,
          ExportStore,
          {
            provide: ImportExportDataService,
            useValue: { importFile: upload, exportFile: vi.fn(), listVersions: versions },
          },
        ],
      },
    });
  });
  afterEach(() => {
    try {
      TestBed.resetTestingModule();
      expect(errors).not.toHaveBeenCalled();
    } finally {
      errors.mockRestore();
    }
  });

  async function setup(source?: File) {
    const fixture = TestBed.createComponent(ImportExportPage);
    fixture.detectChanges();
    const store = fixture.debugElement.injector.get(ImportStore);
    if (source) store.selectFile(source);
    await fixture.whenStable();
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, ImportPageHarness);
    return { fixture, store, harness };
  }
  async function settle(fixture: ComponentFixture<ImportExportPage>) {
    fixture.detectChanges();
    await fixture.whenStable();
  }

  it('renders the route h1 and initial Brain layout without console errors or a confirmation region', async () => {
    const { harness } = await setup();
    expect(await harness.count('h1')).toBe(1);
    expect(await harness.has('[data-import-confirm-region]')).toBe(false);
    expect(await harness.has('[data-import-confirm]')).toBe(false);
    expect(await harness.text()).toContain('No file has been selected');
  });

  it('renders display-only local text without creating a version before explicit confirmation', async () => {
    const { fixture, store, harness } = await setup(file());
    expect(await harness.text()).toContain('From your file, not validated');
    expect(await harness.text()).toContain('Safe title');
    expect(upload).not.toHaveBeenCalled();
    await store.preview();
    await settle(fixture);
    expect(await harness.text()).toContain('n1');
    expect(await harness.has('[data-import-success]')).toBe(false);
  });

  it('retains duplicate keys and a third unreported row, with field-specific summary links and withheld cells', async () => {
    const source =
      IMPORT_COLUMNS.join(',') +
      '\nnode,n1,,,module,Safe title,,,,,,,0' +
      '\nnode,n1,,,module,Other title,,,,,,,1' +
      '\nnode,n3,,,module,Unreported title,,,,,,,2\n';
    const { harness } = await setup(file(source));
    upload.mockRejectedValueOnce(
      refusal([
        { index: 1, path: '/title', reason: 'invalid_text' },
        { index: 2, path: '/key', reason: 'duplicate_key' },
      ]),
    );
    await harness.click('[data-import-preview]');
    expect(await harness.text()).toContain('Remaining rows are unverified');
    expect(await harness.text()).not.toContain('Safe title');
    expect(await harness.text()).toContain('Other title');
    expect(await harness.text()).toContain('Unreported title');
    expect(await harness.has('#import-row-1-title')).toBe(true);
    expect(await harness.has('#import-row-2-key')).toBe(true);
    expect(await harness.textAt('#import-row-3-status')).toContain('Unverified');
    expect(await harness.attr('#import-row-1-title', 'aria-describedby')).toBe(
      'import-row-1-title-error',
    );
    expect(await harness.textAt('a[href="#import-row-1-title"]')).toContain('Title');
    await harness.click('a[href="#import-row-1-title"]');
    expect(await harness.focused('#import-row-1-title')).toBe(true);
    expect(await harness.textAt('[data-import-problems]')).toContain('2');
    expect(await harness.has('[data-import-outcome-totals]')).toBe(false);
    expect(await harness.has('[data-import-inspected]')).toBe(false);
    expect(await harness.attr('[data-import-confirm]', 'disabled')).not.toBeNull();
  });

  it('aligns CSV blank-record skips with the server index without moving a refusal to another row', async () => {
    const source =
      IMPORT_COLUMNS.join(',') +
      '\n\nnode,n1,,,module,First title,,,,,,,0\n\nnode,n2,,,module,Refused title,,,,,,,1\n';
    const { harness } = await setup(file(source));
    upload.mockRejectedValueOnce(refusal([{ index: 2, path: '/title', reason: 'invalid_text' }]));
    await harness.click('[data-import-preview]');
    expect(await harness.textAt('#import-row-1-title')).toContain('First title');
    expect(await harness.textAt('#import-row-2-title')).toContain(
      'Refused cell text is not displayed',
    );
    expect(await harness.text()).not.toContain('Refused title');
  });

  it('shows the validated scope, inspected rows and prospective totals before confirming a non-null batch', async () => {
    const source =
      parsed + 'node,n2,,,module,Second title,,,,,,,1\nnode,n3,,,module,Third title,,,,,,,2\n';
    const { harness } = await setup(file(source));
    await harness.click('[data-import-preview]');
    const scope = await harness.textAt('[data-import-confirm-region]');
    for (const value of [
      'preview.csv',
      'CSV',
      'Storefront',
      'SHOP',
      'Inspected rows',
      'Would create',
    ]) {
      expect(scope).toContain(value);
    }
    expect(await harness.textAt('#import-row-1-status')).toBe('Would create');
    expect(await harness.textAt('[data-import-inspected]')).toBe('1');
    upload.mockResolvedValueOnce({ ...serverReport, batch_id: history.items[0]?.batch_id ?? null });
    await harness.click('[data-import-confirm]');
    expect(await harness.textAt('[data-import-success]')).toContain('Import confirmed');
    expect(await harness.textAt('#import-row-1-status')).toBe('Created');
    expect(versions).toHaveBeenCalledTimes(2);
  });

  it('refreshes authorized project history after import, retaining stale history and preview if refresh fails', async () => {
    const { harness } = await setup(file());
    expect(await harness.textAt('[data-import-history]')).toContain('Authoring versions');
    expect(await harness.textAt('[data-import-history]')).toContain('Not supplied');
    expect(await harness.textAt('[data-import-history]')).toContain('import');
    await harness.click('[data-import-preview]');
    upload.mockResolvedValueOnce({ ...serverReport, batch_id: history.items[0]?.batch_id ?? null });
    versions.mockRejectedValueOnce(refusal([], 'unavailable'));
    await harness.click('[data-import-confirm]');
    expect(await harness.textAt('[data-import-history]')).toContain('stale');
    expect(await harness.textAt('[data-import-history]')).toContain('r3');
    expect(await harness.text()).toContain('Safe title');
    expect(await harness.has('[data-import-success]')).toBe(true);
  });

  it('shows independent initial history failure without claiming no versions exist', async () => {
    versions.mockRejectedValueOnce(refusal([], 'unavailable'));
    const { harness } = await setup(file());
    expect(await harness.textAt('[data-import-history]')).toContain(
      'Version history could not be loaded',
    );
    expect(await harness.textAt('[data-import-history]')).not.toContain('No import versions');
    expect(await harness.text()).toContain('Safe title');
  });

  it('never requests or presents version history without authoring read permission', async () => {
    readable.set(false);
    const { harness } = await setup(file());
    expect(versions).not.toHaveBeenCalled();
    expect(await harness.has('[data-import-history]')).toBe(false);
    expect(await harness.text()).toContain(IMPEX_COPY.denied);
    expect(await harness.text()).not.toContain('Safe title');
  });

  it('retains preview while history Retry-After blocks every explicit history request', async () => {
    const error = refusal([], 'unavailable');
    versions.mockRejectedValueOnce(new ApiRequestError({ ...error.error, retryAfterSeconds: 30 }));
    const { store, harness } = await setup(file());
    expect(await harness.attr('[data-history-refresh]', 'disabled')).not.toBeNull();
    store.refreshHistory();
    store.olderHistory();
    expect(versions).toHaveBeenCalledTimes(1);
    expect(await harness.text()).toContain('Safe title');
  });

  it('announces preview completion politely and marks the preview region itself busy while pending', async () => {
    let resolve: (value: ImportResponse) => void = () => {
      throw new Error('Preview is not pending');
    };
    upload.mockImplementationOnce(
      () =>
        new Promise<ImportResponse>((complete) => {
          resolve = complete;
        }),
    );
    const { fixture, store, harness } = await setup(file());
    const pending = store.preview();
    await settle(fixture);
    expect(await harness.attr('[data-import-preview-region]', 'aria-busy')).toBe('true');
    resolve(serverReport);
    await pending;
    await settle(fixture);
    expect(await harness.attr('[data-import-preview-region]', 'aria-busy')).toBe('false');
    expect(await harness.textAt('[data-import-preview-status]')).toContain('Preview complete');
    expect(await harness.attr('[data-import-preview-status]', 'aria-live')).toBe('polite');
  });

  it('hands removed mutation focus to the retained filename and format snapshot, but preserves inspection focus', async () => {
    const { fixture, harness } = await setup(file());
    await harness.focus('[data-import-file]');
    desktop.set(false);
    await settle(fixture);
    expect(await harness.has('[data-import-file]')).toBe(false);
    expect(await harness.focused('[data-import-snapshot]')).toBe(true);
    expect(await harness.textAt('[data-import-snapshot]')).toContain('preview.csv');
    expect(await harness.textAt('[data-import-snapshot]')).toContain('CSV');
    desktop.set(true);
    await settle(fixture);
    await harness.focus('[data-import-snapshot]');
    desktop.set(false);
    await settle(fixture);
    expect(await harness.focused('[data-import-snapshot]')).toBe(true);
  });

  it('hands removed mutation focus to the desktop-required notice when no file is selected', async () => {
    const { fixture, harness } = await setup();
    await harness.focus('[data-import-file]');
    desktop.set(false);
    await settle(fixture);
    expect(await harness.has('[data-import-file]')).toBe(false);
    expect(await harness.has('[data-import-snapshot]')).toBe(false);
    expect(await harness.focused('app-desktop-required-notice')).toBe(true);
  });

  it('previews XLSX through the server report without parsing workbook content', async () => {
    const source = new File(['PK'], 'workbook.xlsx');
    const text = vi.spyOn(source, 'text');
    const { fixture, store, harness } = await setup(source);
    expect(store.work().format).toBe('xlsx');
    expect(await harness.textAt('#authoring-import-format')).toBe('XLSX');
    expect(await harness.text()).toContain(IMPEX_COPY.xlsxPreview);
    expect(upload).not.toHaveBeenCalled();
    await harness.click('[data-import-preview]');
    await settle(fixture);
    expect(await harness.textAt('app-import-preview')).toContain('Would create');
    expect(await harness.attr('[data-import-confirm]', 'disabled')).toBeNull();
    expect(text).not.toHaveBeenCalled();
  });

  it('keeps 360px stacked field errors expanded above the virtual-row threshold and offers XLSX', async () => {
    const width = vi.spyOn(HTMLElement.prototype, 'clientWidth', 'get').mockReturnValue(360);
    try {
      const source =
        IMPORT_COLUMNS.join(',') +
        '\n' +
        Array.from(
          { length: 201 },
          (_, index) => `node,n${index + 1},,,module,Title ${index + 1},,,,,,,${index}\n`,
        ).join('');
      const { fixture, harness } = await setup(file(source));
      await harness.click('#authoring-import-format');
      const root = TestbedHarnessEnvironment.documentRootLoader(fixture);
      const options = await root.getAllHarnesses(ImportOptionHarness);
      let xlsx: ImportOptionHarness | undefined;
      for (const option of options) {
        if ((await option.text()) === 'XLSX') xlsx = option;
      }
      expect(xlsx).toBeDefined();
      expect(await xlsx?.attr('aria-disabled')).not.toBe('true');
      expect(await xlsx?.attr('aria-describedby')).toBeNull();
      await harness.key('#authoring-import-format', TestKey.ESCAPE);
      upload.mockRejectedValueOnce(refusal([{ index: 1, path: '/title', reason: 'invalid_text' }]));
      await harness.click('[data-import-preview]');
      desktop.set(false);
      await settle(fixture);
      expect(await harness.has('app-import-preview table')).toBe(false);
      expect(await harness.textAt('#import-row-1-title-error')).toContain('invalid_text');
      expect(await harness.attr('#import-row-1-title-error', 'class')).not.toContain('sr-only');
      expect(await harness.has('#import-row-1-title-error app-data-table-value')).toBe(false);
    } finally {
      width.mockRestore();
    }
  }, 20_000);

  it('shows supplied cap measurements and all thirteen header columns without error-envelope text', async () => {
    const { harness } = await setup(file());
    upload.mockRejectedValueOnce(
      refusal(
        [{ path: '/file', reason: 'row_limit_exceeded', limit: 5000, actual: 5001 }],
        'payload_too_large',
      ),
    );
    await harness.click('[data-import-preview]');
    expect(await harness.textAt('[data-import-cap]')).toContain('5,000');
    expect(await harness.textAt('[data-import-cap]')).toContain('5,001');
    expect(await harness.text()).not.toContain('PRIVATE REFUSED CELL');
    upload.mockRejectedValueOnce(refusal([{ path: '/header', reason: 'unsupported_format' }]));
    await harness.click('[data-import-preview]');
    const header = await harness.textAt('[data-import-header-guide]');
    for (const column of IMPORT_COLUMNS) expect(header).toContain(column);
  });
});
