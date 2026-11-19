import { inflateRawSync } from 'node:zlib';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { HttpRequest } from '@angular/common/http';
import { ImportExportDataService } from './import-export-data.service';
import { AUTHORING_XLSX_MEDIA_TYPE } from '../../../core/data/authoring-import-export-gateway';
import { FIXTURE_BACKEND } from '../../../core/dev-fixtures/fixture-backend';
import {
  API_BASE_URL,
  setupFixtureBackend,
} from '../../../core/dev-fixtures/fixture-backend.test-support';
import {
  AUTHORING_XLSX_BASE64,
  FIXTURE_CATALOG_AUTHORING_IMPEX_ROWS,
} from '../../../core/dev-fixtures/fixture-catalog-authoring-impex-rows';
import { undeclaredResponseHeaders } from '../../../core/dev-fixtures/fixture-contract.corpus';
import {
  VALIDATOR,
  isRecord,
  resolveContractPointer,
} from '../../../core/dev-fixtures/fixture-contract.support';

const PROJECT = '018f0000-0000-7000-8000-000000000011';
const XLSX_NAMES = [
  'authoring-import-xlsx',
  'authoring-import-xlsx-confirmed',
  'authoring-import-xlsx-too-large',
  'authoring-import-xlsx-invalid',
  'authoring-export-xlsx',
  'authoring-export-xlsx-too-large',
  'authoring-export-xlsx-invalid',
];

function workbookEntries(): Map<string, string> {
  const archive = Buffer.from(AUTHORING_XLSX_BASE64, 'base64');
  const entries = new Map<string, string>();
  let offset = 0;
  while (archive.readUInt32LE(offset) === 0x04034b50) {
    const length = archive.readUInt32LE(offset + 18);
    const nameLength = archive.readUInt16LE(offset + 26);
    const extraLength = archive.readUInt16LE(offset + 28);
    const start = offset + 30 + nameLength + extraLength;
    const name = archive.subarray(offset + 30, offset + 30 + nameLength).toString();
    entries.set(name, inflateRawSync(archive.subarray(start, start + length)).toString());
    offset = start + length;
  }
  return entries;
}

describe('Authoring XLSX fixture contracts and binary transport', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
    vi.restoreAllMocks();
  });

  it('validates every new logical report and refusal against its OpenAPI response schema', () => {
    const rows = FIXTURE_CATALOG_AUTHORING_IMPEX_ROWS.filter((row) =>
      XLSX_NAMES.includes(row.name),
    );
    expect(rows.map((row) => row.name)).toEqual(XLSX_NAMES);
    for (const row of rows) {
      expect(
        VALIDATOR.validateResponse(
          row.document.operation_id,
          row.document.status,
          row.document.body,
        ).errors,
        row.name,
      ).toEqual([]);
      expect(undeclaredResponseHeaders(row.document), row.name).toEqual([]);
    }
    const binary = resolveContractPointer(
      '#/paths/~1projects~1{p}~1authoring~1export/get/responses/200/content/application~1vnd.openxmlformats-officedocument.spreadsheetml.sheet/schema',
    );
    expect(binary).toEqual({ type: 'string', format: 'binary' });
    expect(Buffer.from(AUTHORING_XLSX_BASE64, 'base64').subarray(0, 2).toString()).toBe('PK');
  });

  it('contains the import headers and one complete node row in a real XLSX archive', () => {
    const entries = workbookEntries();
    expect([...entries.keys()]).toEqual([
      '[Content_Types].xml',
      '_rels/.rels',
      'xl/workbook.xml',
      'xl/_rels/workbook.xml.rels',
      'xl/worksheets/sheet1.xml',
    ]);
    const sheet = entries.get('xl/worksheets/sheet1.xml');
    expect(sheet).toContain('<t>kind</t>');
    expect(sheet).toContain('<t>title</t>');
    expect(sheet).toContain('<t>node</t>');
    expect(sheet).toContain('<t>Checkout</t>');
  });

  it('uses a dry-run report before the separate XLSX confirmation report', async () => {
    const { handled } = setupFixtureBackend();
    const data = TestBed.runInInjectionContext(() => new ImportExportDataService());
    const bytes = Uint8Array.from(atob(AUTHORING_XLSX_BASE64), (value) => value.charCodeAt(0));
    const source = new File([bytes], 'file.xlsx');
    const preview = await data.importFile(PROJECT, source, 'xlsx', true, 'preview-key');
    expect(preview.batch_id).toBeNull();
    const applied = await data.importFile(PROJECT, source, 'xlsx', false, 'confirm-key');
    expect(applied.batch_id).toBe('018f0000-0000-7000-8000-00000000af02');
    const imports = handled.filter((request) => request.url.includes('/authoring/import'));
    expect(imports).toHaveLength(2);
    for (const request of imports) {
      const body: unknown = request.body;
      expect(body).toBeInstanceOf(ArrayBuffer);
      if (!(body instanceof ArrayBuffer)) throw new Error('Expected workbook bytes');
      expect(new Uint8Array(body)).toEqual(bytes);
    }
  });

  it('serves a real workbook with XLSX type, fixed disposition and no-store through the SDK', async () => {
    setupFixtureBackend();
    const data = TestBed.runInInjectionContext(() => new ImportExportDataService());
    const result = await data.exportFile(PROJECT, { format: 'xlsx', include: 'nodes' });
    expect(result.filename).toBe('authoring-export.xlsx');
    expect(result.blob.type).toBe(AUTHORING_XLSX_MEDIA_TYPE);
    expect(new Uint8Array(await result.blob.arrayBuffer())).toEqual(
      Uint8Array.from(atob(AUTHORING_XLSX_BASE64), (value) => value.charCodeAt(0)),
    );
    const backend = TestBed.inject(FIXTURE_BACKEND);
    const event = await firstValueFrom(
      backend.handle(
        new HttpRequest('GET', `${API_BASE_URL}/projects/${PROJECT}/authoring/export?format=xlsx`, {
          responseType: 'blob',
        }),
      ),
    );
    if (!('headers' in event)) throw new Error('Expected a workbook response');
    expect(event.headers.get('Content-Disposition')).toBe(
      'attachment; filename="authoring-export.xlsx"',
    );
    expect(event.headers.get('Cache-Control')).toBe('no-store');
  });

  it.each([
    ['importAuthoring', 'xlsx-too-large', 413, '/file', 'body_too_large'],
    ['importAuthoring', 'xlsx-invalid', 422, '/file', 'unsupported_format'],
    ['exportAuthoring', 'xlsx-too-large', 413, '/node_id', 'body_too_large'],
    ['exportAuthoring', 'xlsx-invalid', 422, '/format', 'unsupported_format'],
  ])(
    'maps the %s %s refusal without offering a workbook',
    async (operation, variant, status, path, reason) => {
      const { toggles } = setupFixtureBackend();
      toggles.setVariant(operation, variant);
      if (operation === 'exportAuthoring') {
        const backend = TestBed.inject(FIXTURE_BACKEND);
        const response = backend.handle(
          new HttpRequest(
            'GET',
            `${API_BASE_URL}/projects/${PROJECT}/authoring/export?format=xlsx`,
            {
              responseType: 'arraybuffer',
            },
          ),
        );
        await expect(firstValueFrom(response)).rejects.toMatchObject({
          status,
          error: expect.any(ArrayBuffer) as unknown,
        });
      }
      const data = TestBed.runInInjectionContext(() => new ImportExportDataService());
      const request =
        operation === 'importAuthoring'
          ? data.importFile(PROJECT, new File(['PK'], 'file.xlsx'), 'xlsx', true, 'key')
          : data.exportFile(PROJECT, { format: 'xlsx' });
      await expect(request).rejects.toMatchObject({
        error: { status, details: [{ path, reason }] },
      });
      const fixture = FIXTURE_CATALOG_AUTHORING_IMPEX_ROWS.find(
        (row) => row.document.operation_id === operation && row.document.variant === variant,
      );
      expect(
        isRecord(fixture?.document.body) && isRecord(fixture.document.body['error'])
          ? fixture.document.body['error']['retryable']
          : undefined,
      ).toBe(false);
    },
  );
});
