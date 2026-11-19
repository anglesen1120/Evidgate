import { computed, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { SessionStore } from '../../../core/session/session.store';
import { ApiRequestError, type ApiError } from '../../../core/http/api-error';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow/narrow-mutation-policy';
import { ImportExportPage } from './import-export.page';
import { ImportExportDataService } from './import-export-data.service';
import { ImportStore } from './import.store';
import { ExportStore } from './export.store';
import { IMPORT_COLUMNS } from './local-file-preview';

const parsed = IMPORT_COLUMNS.join(',') + '\nnode,n1,,,module,Safe title,,,,,,,0\n';
function file(): File {
  const source = new File([parsed], 'preview.csv', { type: 'text/csv' });
  Object.defineProperty(source, 'text', { value: () => Promise.resolve(parsed) });
  return source;
}
const serverReport = {
  batch_id: null,
  counts: { created: 1, updated: 0, restored: 0, unchanged: 0 },
  rows: [{ index: 1, key: 'n1', kind: 'node', outcome: 'create', code: null }],
} as const;

describe('ImportExportPage presentation and route heading contract', () => {
  const upload = vi.fn().mockResolvedValue(serverReport);
  const desktop = signal(true);
  beforeEach(() => {
    desktop.set(true);
    upload.mockReset().mockResolvedValue(serverReport);
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        {
          provide: SessionStore,
          useValue: {
            currentProject: signal({ id: 'project', name: 'Storefront', case_prefix: 'SHOP' }),
            has: () => true,
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
            useValue: { importFile: upload, exportFile: vi.fn() },
          },
        ],
      },
    });
  });
  afterEach(() => TestBed.resetTestingModule());

  it('reads its h1 from actual ActivatedRoute data and renders display-only preview without creating a version', async () => {
    const fixture = TestBed.createComponent(ImportExportPage);
    const host = fixture.nativeElement as HTMLElement;
    fixture.detectChanges();
    expect(host.querySelectorAll('h1').length).toBe(1);
    const store = fixture.debugElement.injector.get(ImportStore);
    store.selectFile(file());
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(host.textContent).toContain('From your file, not validated');
    expect(host.textContent).toContain('Safe title');
    expect(upload).not.toHaveBeenCalled();
    await store.preview();
    fixture.detectChanges();
    expect(host.textContent).toContain('n1');
    expect(host.querySelector('[data-import-success]')).toBeNull();
  });

  it('retains duplicate rows, masks refused cell text and marks all unreported rows unverified after mixed 422', async () => {
    const fixture = TestBed.createComponent(ImportExportPage);
    const host = fixture.nativeElement as HTMLElement;
    fixture.detectChanges();
    const store = fixture.debugElement.injector.get(ImportStore);
    store.selectFile(file());
    fixture.detectChanges();
    await fixture.whenStable();
    const error: ApiError = {
      kind: 'validation',
      status: 422,
      code: 'validation_error',
      message: 'Safe title must never be echoed from the error',
      requestId: null,
      retryable: false,
      retryAfterSeconds: null,
      details: [{ index: 1, path: '/title', reason: 'invalid_text' }],
    };
    upload.mockRejectedValueOnce(new ApiRequestError(error));
    await store.preview();
    fixture.detectChanges();
    expect(host.textContent).toContain('Remaining rows are unverified');
    expect(host.textContent).not.toContain('Safe title');
    expect(host.textContent).toContain('Refused cell text is not displayed');
    expect(host.querySelector<HTMLButtonElement>('[data-import-confirm]')?.disabled).toBe(true);
  });

  it('removes import intake and confirmation at narrow widths while keeping export inspectable', () => {
    desktop.set(false);
    const fixture = TestBed.createComponent(ImportExportPage);
    const host = fixture.nativeElement as HTMLElement;
    fixture.detectChanges();
    expect(host.querySelector('[data-import-file]')).toBeNull();
    expect(host.querySelector('[data-import-confirm]')).toBeNull();
    expect(host.querySelector('app-authoring-export-panel')).not.toBeNull();
  });
});
