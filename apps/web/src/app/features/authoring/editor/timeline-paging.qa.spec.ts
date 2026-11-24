import {
  ChangeDetectionStrategy,
  Component,
  provideZonelessChangeDetection,
  signal,
} from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { AuthoringEditorDataService } from '../../../core/data/authoring-editor-data.service';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow';
import { EditorStore, type EditorScope } from './editor.store';
import { TimelineStore } from './timeline.store';
import { CASE_ID, version } from './timeline.testing';

@Component({ template: '', changeDetection: ChangeDetectionStrategy.OnPush })
class TimelineQaHost {}

describe('Timeline paging selection boundaries', () => {
  const scope = signal<EditorScope | undefined>({
    org: 'org',
    project: 'project',
    caseId: CASE_ID,
  });
  const versions = vi.fn<AuthoringEditorDataService['versions']>();
  const detail = vi.fn<AuthoringEditorDataService['version']>();
  beforeEach(() => {
    scope.set({ org: 'org', project: 'project', caseId: CASE_ID });
    versions.mockReset().mockResolvedValue({ items: [version(3)], next_cursor: 'older' });
    detail
      .mockReset()
      .mockImplementation((_project, id) =>
        Promise.resolve({ view: version(Number(id.slice(-1))), etag: '"snapshot"' }),
      );
    TestBed.configureTestingModule({
      imports: [TimelineQaHost],
      providers: [
        provideZonelessChangeDetection(),
        TimelineStore,
        { provide: EditorStore, useValue: { scope } },
        { provide: AuthoringEditorDataService, useValue: { versions, version: detail } },
        { provide: NarrowMutationPolicy, useValue: { allowsMutation: () => true } },
      ],
    });
  });
  afterEach(() => TestBed.resetTestingModule());

  it('keeps both selected snapshots through a failed size reset and retries without refetching either detail', async () => {
    const fixture = TestBed.createComponent(TimelineQaHost);
    const store = TestBed.inject(TimelineStore);
    await fixture.whenStable();
    store.selectVersion('version-3');
    await fixture.whenStable();
    versions.mockResolvedValueOnce({ items: [version(2)], next_cursor: null });
    store.nextPage();
    await fixture.whenStable();
    store.compareVersion('version-2');
    await fixture.whenStable();
    expect(store.versionDetail()?.view.version_id).toBe('version-3');
    expect(store.comparedDetail()?.view.version_id).toBe('version-2');
    versions.mockRejectedValueOnce(new Error('offline'));
    store.setPageSize(50);
    await fixture.whenStable();
    expect(store.rows()).toEqual([]);
    expect(store.selected()).toBe('version-3');
    expect(store.compared()).toBe('version-2');
    expect(store.versionDetail()?.view.version_id).toBe('version-3');
    expect(store.comparedDetail()?.view.version_id).toBe('version-2');
    versions.mockResolvedValueOnce({ items: [version(1)], next_cursor: null });
    store.history.reload();
    await fixture.whenStable();
    expect(versions).toHaveBeenLastCalledWith(
      'project',
      CASE_ID,
      { limit: 50 },
      expect.any(AbortSignal),
    );
    expect(store.page()).toBe(1);
    expect(store.rows().map((row) => row.version_id)).toEqual(['version-1']);
    expect(detail).toHaveBeenCalledTimes(2);
    store.selectVersion('not-on-this-page');
    store.compareVersion('not-on-this-page');
    expect(store.selected()).toBe('version-3');
    expect(store.compared()).toBe('version-2');
  });

  it('rejects a fetched snapshot belonging to a different case before displaying its content', async () => {
    const fixture = TestBed.createComponent(TimelineQaHost);
    const store = TestBed.inject(TimelineStore);
    await fixture.whenStable();
    detail.mockResolvedValueOnce({
      view: { ...version(3), entity_id: 'other-case' },
      etag: '"foreign"',
    });
    store.selectVersion('version-3');
    await fixture.whenStable();
    expect(store.versionDetail()).toBeUndefined();
    expect(store.detail.error()).toMatchObject({ error: { kind: 'not_found', status: 404 } });
  });
});
