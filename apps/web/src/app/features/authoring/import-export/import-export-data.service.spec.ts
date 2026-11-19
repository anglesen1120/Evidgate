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

const report = {
  batch_id: null,
  counts: { created: 1, updated: 0, restored: 0, unchanged: 0 },
  rows: [{ index: 1, key: 'n1', kind: 'node', outcome: 'create', code: null }],
};
const BASE_URL = `${location.origin}${API_BASE_PATH}`;
const JSON_HEADERS = new HttpHeaders({ 'Content-Type': 'application/json' });

/**
 * Drives the production chain (`provideApi` → interceptors → SDK client → adapter) over Angular's
 * testing backend. The adapter forwards a textual body as the exact characters the caller sent,
 * so the recorded `HttpRequest.body` is where "the original bytes were sent" is asserted.
 */
describe('ImportExportDataService', () => {
  let http: HttpTestingController;
  let service: ImportExportDataService;
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [ImportExportDataService, provideApi(BASE_URL), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
    service = TestBed.inject(ImportExportDataService);
  });
  afterEach(() => {
    try {
      http.verify();
    } finally {
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

  it('sends the exact original file bytes with explicit dry_run=true and the intent key', async () => {
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

  it('sends dry_run=false only when the caller explicitly confirms with the same key', async () => {
    const file = new File(['{}'], 'source.json');
    for (const dryRun of [true, false]) {
      const call = service.importFile('project', file, 'json', dryRun, 'same-intent');
      const pending = await takeRequest('/authoring/import');
      expect(new URL(pending.request.urlWithParams).searchParams.get('dry_run')).toBe(
        String(dryRun),
      );
      expect(pending.request.headers.get('Idempotency-Key')).toBe('same-intent');
      pending.flush(JSON.stringify(report));
      await call;
    }
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
