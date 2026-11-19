import { BrowserPlatformLocation, PlatformLocation } from '@angular/common';
import { HttpHeaders } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
  type TestRequest,
} from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { API_BASE_PATH } from '../../../core/http/api-origin';
import { provideApi } from '../../../core/http/api-client';
import { ImportExportDataService } from './import-export-data.service';
import { BINARY_TRANSPORT_HEADER } from '../../../core/http/binary-transport';
import { TenantStore } from '../../../core/session/tenant.store';
import { IMPORT_BYTE_CAP } from './local-file-preview';

const report = {
  batch_id: null,
  counts: { created: 1, updated: 0, restored: 0, unchanged: 0 },
  rows: [{ index: 1, key: 'n1', kind: 'node', outcome: 'create', code: null }],
};
const BASE_URL = `${location.origin}${API_BASE_PATH}`;
const JSON_HEADERS = new HttpHeaders({ 'Content-Type': 'application/json' });

/** Exercises the configured SDK/interceptor chain with file uploads and downloads. */
describe('ImportExportDataService', () => {
  let http: HttpTestingController;
  let service: ImportExportDataService;
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        ImportExportDataService,
        provideApi(BASE_URL),
        provideHttpClientTesting(),
        { provide: PlatformLocation, useClass: BrowserPlatformLocation },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    service = TestBed.inject(ImportExportDataService);
  });
  afterEach(() => {
    try {
      http.verify();
    } finally {
      document.cookie = 'XSRF-TOKEN=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/';
      TestBed.resetTestingModule();
    }
  });

  // The SDK reads the request body before it hands the request to `HttpClient`, so the request
  // arrives an await later; `waitFor` polls instead of assuming one turn is enough.
  function takeRequest(path: string): Promise<TestRequest> {
    return vi.waitFor(() =>
      http.expectOne((candidate) => new URL(candidate.urlWithParams).pathname.endsWith(path)),
    );
  }

  it('sends the original CSV text with explicit dry_run=true and the preview intent key', async () => {
    const file = new File(['kind,key\r\nnode,n1'], 'source.csv');
    const call = service.importFile('project', file, 'csv', true, 'intent-one');
    const pending = await takeRequest('/authoring/import');
    expect(pending.request.headers.get('Content-Type')).toBe('text/csv');
    expect(pending.request.headers.get('Idempotency-Key')).toBe('intent-one');
    expect(new URL(pending.request.urlWithParams).searchParams.get('dry_run')).toBe('true');
    expect(pending.request.body).toBe(await file.text());
    pending.flush(JSON.stringify(report));
    await call;
  });

  it('refuses CSV bytes that are not valid UTF-8 before sending anything', async () => {
    const file = new File([new Uint8Array([0x6b, 0xe9, 0x79])], 'latin1.csv');
    await expect(service.importFile('project', file, 'csv', true, 'key')).rejects.toMatchObject({
      error: { kind: 'validation', code: 'invalid_utf8', status: 422 },
    });
    http.expectNone((request) => request.url.includes('/authoring/import'));
  });

  it('forwards the distinct caller-provided preview and commit keys with their dry_run intent', async () => {
    const file = new File(['{}'], 'source.json');
    for (const dryRun of [true, false]) {
      const key = dryRun ? 'preview-intent' : 'commit-intent';
      const call = service.importFile('project', file, 'json', dryRun, key);
      const pending = await takeRequest('/authoring/import');
      expect(new URL(pending.request.urlWithParams).searchParams.get('dry_run')).toBe(
        String(dryRun),
      );
      expect(pending.request.headers.get('Idempotency-Key')).toBe(key);
      pending.flush(JSON.stringify(report));
      await call;
    }
  });

  it('refuses an XLSX filename sent as text instead of decoding workbook bytes', async () => {
    await expect(
      service.importFile('project', new File(['PK'], 'file.xlsx'), 'csv', true, 'key'),
    ).rejects.toMatchObject({
      error: { kind: 'validation', code: 'format_mismatch', status: 422 },
    });
    http.expectNone((request) => request.url.includes('/authoring/import'));
  });

  it('sends original XLSX bytes for dry run and confirmation without a text decode', async () => {
    document.cookie = 'XSRF-TOKEN=xlsx-csrf-token; path=/';
    const org = '018f0000-0000-7000-8000-000000000001';
    TestBed.inject(TenantStore).setOrg(org);
    const file = new File([new Uint8Array([0x50, 0x4b, 0x03, 0x04, 0xff])], 'source.xlsx');
    const text = vi.spyOn(file, 'text');
    for (const dryRun of [true, false]) {
      const key = dryRun ? 'preview-xlsx' : 'confirm-xlsx';
      const call = service.importFile('project', file, 'xlsx', dryRun, key);
      const pending = await takeRequest('/authoring/import');
      const body: unknown = pending.request.body;
      expect(body).toBeInstanceOf(ArrayBuffer);
      if (!(body instanceof ArrayBuffer)) throw new Error('Expected workbook bytes');
      expect(new Uint8Array(body)).toEqual(new Uint8Array(await file.arrayBuffer()));
      expect(pending.request.headers.has(BINARY_TRANSPORT_HEADER)).toBe(false);
      expect(pending.request.headers.get('Content-Type')).toBe(
        'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
      );
      expect(pending.request.headers.get('Idempotency-Key')).toBe(key);
      expect(pending.request.headers.get('X-XSRF-TOKEN')).toBe('xlsx-csrf-token');
      expect(pending.request.headers.get('X-Request-Id')).toMatch(
        /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/,
      );
      expect(pending.request.headers.get('X-Evidgate-Org')).toBe(org);
      expect(Object.fromEntries(new URL(pending.request.urlWithParams).searchParams)).toEqual({
        format: 'xlsx',
        dry_run: String(dryRun),
      });
      pending.flush(JSON.stringify(report), { headers: JSON_HEADERS });
      expect(await call).toEqual(report);
    }
    expect(text).not.toHaveBeenCalled();
  });

  it('refuses XLSX above the byte cap locally and sends a workbook exactly at the cap', async () => {
    await expect(
      service.importFile(
        'project',
        new File([new Uint8Array(IMPORT_BYTE_CAP + 1)], 'big.xlsx'),
        'xlsx',
        true,
        'key',
      ),
    ).rejects.toMatchObject({
      error: { kind: 'validation', status: 422, code: 'xlsx_too_large' },
    });
    http.expectNone((request) => request.url.includes('/authoring/import'));
    const file = new File([new Uint8Array(IMPORT_BYTE_CAP)], 'limit.xlsx');
    const call = service.importFile('project', file, 'xlsx', true, 'key');
    const pending = await takeRequest('/authoring/import');
    const body: unknown = pending.request.body;
    expect(body instanceof ArrayBuffer ? body.byteLength : null).toBe(IMPORT_BYTE_CAP);
    pending.flush(JSON.stringify(report), { headers: JSON_HEADERS });
    await call;
  });

  it.each(['csv', 'json'] as const)(
    'refuses oversized %s before reading the file or sending an import request',
    async (format) => {
      const file = new File([new Uint8Array(IMPORT_BYTE_CAP + 1)], `large.${format}`);
      const bytes = vi.spyOn(file, 'arrayBuffer');
      const text = vi.spyOn(file, 'text');
      await expect(service.importFile('project', file, format, true, 'key')).rejects.toMatchObject({
        error: {
          kind: 'payload_too_large',
          status: 413,
          code: 'payload_too_large',
          details: [
            {
              path: '/file',
              reason: 'body_too_large',
              limit: IMPORT_BYTE_CAP,
              actual: IMPORT_BYTE_CAP + 1,
            },
          ],
        },
      });
      expect(bytes).not.toHaveBeenCalled();
      expect(text).not.toHaveBeenCalled();
      http.expectNone((request) => request.url.includes('/authoring/import'));
    },
  );

  it.each(['csv', 'json'] as const)(
    'sends %s exactly at the byte cap on the text transport',
    async (format) => {
      const content = (format === 'csv' ? 'kind,key\nnode,n1' : '{}').padEnd(IMPORT_BYTE_CAP, ' ');
      const file = new File([content], `limit.${format}`);
      const call = service.importFile('project', file, format, true, 'key');
      const pending = await takeRequest('/authoring/import');
      expect(pending.request.body).toBe(content);
      expect(pending.request.responseType).toBe('text');
      pending.flush(JSON.stringify(report), { headers: JSON_HEADERS });
      expect(await call).toEqual(report);
    },
  );

  it.each([
    [413, 'payload_too_large', '/file', 'body_too_large', 'payload_too_large'],
    [422, 'validation_error', '/header', 'unsupported_format', 'validation'],
    [422, 'validation_error', '/format', 'unsupported_format', 'validation'],
  ])(
    'maps an XLSX import %s refusal without treating it as a report',
    async (status, code, path, reason, kind) => {
      const call = service.importFile(
        'project',
        new File(['PK'], 'file.xlsx'),
        'xlsx',
        true,
        'key',
      );
      const pending = await takeRequest('/authoring/import');
      pending.flush(
        JSON.stringify({
          error: {
            code,
            message: 'Refused',
            retryable: false,
            request_id: '018f0000-0000-7000-8000-000000000077',
            details: [{ path, reason }],
          },
        }),
        { status, statusText: 'Refused', headers: JSON_HEADERS },
      );
      await expect(call).rejects.toMatchObject({
        error: { kind, status, details: [{ path, reason }] },
      });
    },
  );

  it('exports XLSX bytes with exactly the chosen filters and a validated constant filename', async () => {
    const call = service.exportFile('project', {
      format: 'xlsx',
      node_id: '018f0000-0000-7000-8000-000000000055',
      include: 'cases',
    });
    const pending = await takeRequest('/authoring/export');
    expect(pending.request.responseType).toBe('arraybuffer');
    expect(pending.request.headers.has(BINARY_TRANSPORT_HEADER)).toBe(false);
    expect(Object.fromEntries(new URL(pending.request.urlWithParams).searchParams)).toEqual({
      format: 'xlsx',
      node_id: '018f0000-0000-7000-8000-000000000055',
      include: 'cases',
    });
    const blob = new Blob([new Uint8Array([0x50, 0x4b, 3, 4, 0xff])], {
      type: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
    });
    pending.flush(await blob.arrayBuffer(), {
      headers: new HttpHeaders({
        'Content-Type': blob.type,
        'Content-Disposition': 'attachment; filename="authoring-export.xlsx"',
      }),
    });
    const result = await call;
    expect(new Uint8Array(await result.blob.arrayBuffer())).toEqual(
      new Uint8Array([0x50, 0x4b, 3, 4, 0xff]),
    );
    expect(result.filename).toBe('authoring-export.xlsx');
  });

  it.each(['csv', 'json'] as const)(
    'keeps %s exports on the existing text transport',
    async (format) => {
      const call = service.exportFile('project', { format });
      const pending = await takeRequest('/authoring/export');
      expect(pending.request.responseType).toBe('text');
      const content = format === 'csv' ? 'kind,key\nnode,n1' : '{"rows":[]}';
      pending.flush(content, {
        headers: new HttpHeaders({
          'Content-Type': format === 'csv' ? 'text/csv' : 'application/json',
          'Content-Disposition': `attachment; filename="authoring-export.${format}"`,
        }),
      });
      expect(await (await call).blob.text()).toBe(content);
    },
  );

  it('reads authorized project import history through the SDK without a fabricated actor or version', async () => {
    const call = service.listVersions('project');
    const pending = await takeRequest('/authoring/versions');
    expect(pending.request.method).toBe('GET');
    expect([...new URL(pending.request.urlWithParams).searchParams]).toEqual([
      ['source', 'import'],
      ['limit', '50'],
    ]);
    expect(pending.request.headers.has('Idempotency-Key')).toBe(false);
    pending.flush(JSON.stringify({ items: [], next_cursor: null }), { headers: JSON_HEADERS });
    expect(await call).toEqual({ items: [], next_cursor: null });
  });

  it('exports exactly the chosen format, node and included kinds and keeps the server filename', async () => {
    const call = service.exportFile('project', {
      format: 'csv',
      node_id: 'node',
      include: 'nodes,cases',
    });
    const pending = await takeRequest('/authoring/export');
    pending.flush('server-file', {
      status: 200,
      statusText: 'OK',
      headers: new HttpHeaders({
        'Content-Type': 'text/csv',
        'Content-Disposition': 'attachment; filename="authoring-export.csv"',
      }),
    });
    const result = await call;
    expect([...new URL(pending.request.urlWithParams).searchParams]).toEqual([
      ['format', 'csv'],
      ['node_id', 'node'],
      ['include', 'nodes,cases'],
    ]);
    expect(await result.blob.text()).toBe('server-file');
    expect(result.filename).toBe('authoring-export.csv');
  });

  it('does not invent a node filter for project export or construct a filename from caller data', async () => {
    const call = service.exportFile('project', { format: 'json', include: 'items' });
    const pending = await takeRequest('/authoring/export');
    pending.flush('{}', { status: 200, statusText: 'OK', headers: JSON_HEADERS });
    await expect(call).rejects.toThrow();
    expect([...new URL(pending.request.urlWithParams).searchParams]).toEqual([
      ['format', 'json'],
      ['include', 'items'],
    ]);
  });

  it('maps an export 413 envelope into the shared typed payload_too_large refusal', async () => {
    const call = service.exportFile('project', { format: 'json', include: 'cases' });
    const pending = await takeRequest('/authoring/export');
    pending.flush(
      JSON.stringify({
        error: {
          code: 'payload_too_large',
          message: 'Too large',
          retryable: false,
          request_id: '018f0000-0000-7000-8000-000000000077',
          details: [{ path: '/node_id', reason: 'body_too_large' }],
        },
      }),
      { status: 413, statusText: 'Refused', headers: JSON_HEADERS },
    );
    await expect(call).rejects.toMatchObject({
      error: {
        kind: 'payload_too_large',
        details: [{ path: '/node_id', reason: 'body_too_large' }],
      },
    });
  });
});
