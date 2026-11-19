import { FIXTURE_CATALOG_AUTHORING_IMPEX_ROWS } from './fixture-catalog-authoring-impex-rows';
import { declaredResponseHeaderNames, isRecord, VALIDATOR } from './fixture-contract.support';
import preview from '../../../../../../fixtures/web-api/authoring-import-preview/input.json';
import invalid from '../../../../../../fixtures/web-api/authoring-import-mixed/input.json';
import exported from '../../../../../../fixtures/web-api/authoring-export-json/input.json';
import { FixtureHttpBackend } from './fixture-backend';
import { FixtureToggles } from './fixture-toggles';
import { HttpRequest, HttpResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';

describe('authoring import/export fixture contract pins', () => {
  for (const row of FIXTURE_CATALOG_AUTHORING_IMPEX_ROWS) {
    it(`validates ${row.name} against its declared operation and status`, () => {
      const document = row.document;
      expect(
        VALIDATOR.validateResponse(document.operation_id, document.status, document.body).errors,
      ).toEqual([]);
      const declared = declaredResponseHeaderNames(document.operation_id, document.status).map(
        (name) => name.toLowerCase(),
      );
      expect(
        Object.keys(document.headers).filter((name) => !declared.includes(name.toLowerCase())),
      ).toEqual([]);
    });
  }
  it('rejects a non-UUID import batch and an error missing required retryable', () => {
    expect(
      VALIDATOR.validateResponse('importAuthoring', 200, {
        ...preview.body,
        batch_id: 'not-a-uuid',
      }).errors.length,
    ).toBeGreaterThan(0);
    expect(
      VALIDATOR.validateResponse('importAuthoring', 422, {
        error: { ...invalid.body.error, retryable: undefined },
      }).errors.length,
    ).toBeGreaterThan(0);
  });
  it('rejects an export row that breaks the standalone authoring import schema', () => {
    expect(
      VALIDATOR.validateResponse('exportAuthoring', 200, {
        ...exported.body,
        rows: [{ kind: 'node', key: 'n1' }],
      }).errors.length,
    ).toBeGreaterThan(0);
  });
  it('answers a dry-run preview without a batch and an explicit confirmation with a batch', async () => {
    TestBed.configureTestingModule({
      providers: [
        FixtureHttpBackend,
        { provide: FixtureToggles, useValue: { variantFor: () => 'ok', latencyFor: () => null } },
      ],
    });
    const backend = TestBed.inject(FixtureHttpBackend);
    const dryRun = await firstValueFrom(
      backend.handle(
        new HttpRequest(
          'POST',
          'https://example.test/v1/projects/018f0000-0000-7000-8000-000000000011/authoring/import?dry_run=true',
          new Blob(['file']),
        ),
      ),
    );
    const commit = await firstValueFrom(
      backend.handle(
        new HttpRequest(
          'POST',
          'https://example.test/v1/projects/018f0000-0000-7000-8000-000000000011/authoring/import?dry_run=false',
          new Blob(['file']),
        ),
      ),
    );
    expect(dryRun instanceof HttpResponse && dryRun.body).toEqual(preview.body);
    const committed = commit instanceof HttpResponse && isRecord(commit.body) ? commit.body : null;
    expect(committed).not.toBeNull();
    expect(committed?.['batch_id']).toMatch(/^[0-9a-f-]{36}$/);
    TestBed.resetTestingModule();
  });
});
