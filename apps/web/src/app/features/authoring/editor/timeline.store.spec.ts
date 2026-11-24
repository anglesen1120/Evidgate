import {
  ChangeDetectionStrategy,
  Component,
  provideZonelessChangeDetection,
  signal,
} from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { AuthoringVersionListItemDto } from '@evidgate/sdk';
import { AuthoringEditorDataService } from '../../../core/data/authoring-editor-data.service';
import { ApiRequestError } from '../../../core/http/api-error';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow';
import { EditorStore, type EditorScope } from './editor.store';
import { TimelineStore } from './timeline.store';

@Component({ template: '', changeDetection: ChangeDetectionStrategy.OnPush })
class TimelineHost {}

const row = (no: number): AuthoringVersionListItemDto => ({
  version_id: 'version-' + no,
  entity_id: 'case1',
  entity_kind: 'manual_test_case',
  version_no: no,
  batch_id: 'batch-' + no,
  source: 'manual',
  change_kind: 'update',
  content_hash: 'sha256:' + String(no).repeat(64),
  author: null,
  reason: 'Reason ' + no,
  ai_run_id: null,
  created_at: '2026-11-24T00:00:00Z',
});
const unavailable = new ApiRequestError({
  kind: 'unavailable',
  status: 503,
  code: 'unavailable',
  message: 'Unavailable',
  details: [],
  retryable: true,
  requestId: null,
  retryAfterSeconds: null,
});

describe('TimelineStore paging', () => {
  const scope = signal<EditorScope | undefined>({ org: 'o1', project: 'p1', caseId: 'case1' });
  const versions = vi.fn();
  const version = vi.fn();
  beforeEach(() => {
    scope.set({ org: 'o1', project: 'p1', caseId: 'case1' });
    versions
      .mockReset()
      .mockImplementation((_project: string, _caseId: string, query: { cursor?: string }) =>
        Promise.resolve(
          query.cursor === 'last'
            ? { items: [row(1)], next_cursor: null }
            : query.cursor === 'older'
              ? { items: [row(2)], next_cursor: 'last' }
              : { items: [row(3)], next_cursor: 'older' },
        ),
      );
    version.mockReset().mockImplementation((_project: string, id: string) =>
      Promise.resolve({
        view: { ...row(Number(id.slice(-1))), content: { title: id } },
        etag: '"snapshot"',
      }),
    );
    TestBed.configureTestingModule({
      imports: [TimelineHost],
      providers: [
        provideZonelessChangeDetection(),
        TimelineStore,
        { provide: EditorStore, useValue: { scope } },
        { provide: AuthoringEditorDataService, useValue: { versions, version } },
        { provide: NarrowMutationPolicy, useValue: { allowsMutation: () => true } },
      ],
    });
  });
  afterEach(() => TestBed.resetTestingModule());
  async function mount() {
    const fixture = TestBed.createComponent(TimelineHost);
    const store = TestBed.inject(TimelineStore);
    await fixture.whenStable();
    return { fixture, store };
  }

  it.each([0, 1])('supports a terminal empty or single record page (%s)', async (count) => {
    versions.mockResolvedValue({
      items: Array.from({ length: count }, () => row(1)),
      next_cursor: null,
    });
    const { fixture, store } = await mount();
    expect(store.rows()).toHaveLength(count);
    expect(store.page()).toBe(1);
    expect(store.pageSize()).toBe(20);
    store.previousPage();
    store.nextPage();
    await fixture.whenStable();
    expect(versions).toHaveBeenCalledTimes(1);
    expect(versions).toHaveBeenLastCalledWith(
      'p1',
      'case1',
      { limit: 20 },
      expect.any(AbortSignal),
    );
  });

  it('replaces first, middle and last pages and returns Previous through opaque cursor history', async () => {
    const { fixture, store } = await mount();
    store.nextPage();
    await fixture.whenStable();
    expect(store.page()).toBe(2);
    expect(store.rows().map((entry) => entry.version_id)).toEqual(['version-2']);
    store.nextPage();
    await fixture.whenStable();
    expect(store.page()).toBe(3);
    expect(store.nextCursor()).toBeNull();
    expect(store.rows().map((entry) => entry.version_id)).toEqual(['version-1']);
    store.previousPage();
    await fixture.whenStable();
    expect(store.page()).toBe(2);
    expect(versions).toHaveBeenLastCalledWith(
      'p1',
      'case1',
      { limit: 20, cursor: 'older' },
      expect.any(AbortSignal),
    );
    store.previousPage();
    await fixture.whenStable();
    expect(store.rows().map((entry) => entry.version_id)).toEqual(['version-3']);
    expect(store.page()).toBe(1);
  });

  it.each([50, 100])(
    'resets cursor history at selected size %s and rejects unsupported sizes',
    async (size) => {
      const { fixture, store } = await mount();
      store.nextPage();
      await fixture.whenStable();
      store.setPageSize(size);
      await fixture.whenStable();
      expect(store.page()).toBe(1);
      expect(store.cursorPath()).toEqual([]);
      expect(versions).toHaveBeenLastCalledWith(
        'p1',
        'case1',
        { limit: size },
        expect.any(AbortSignal),
      );
      const calls = versions.mock.calls.length;
      store.setPageSize(25);
      store.setPageSize(size);
      await fixture.whenStable();
      expect(versions).toHaveBeenCalledTimes(calls);
    },
  );

  it('preserves a selected evidence snapshot across paging, comparison and failed-page retry', async () => {
    const { fixture, store } = await mount();
    store.selectVersion('version-3');
    await fixture.whenStable();
    versions.mockRejectedValueOnce(unavailable);
    store.nextPage();
    await fixture.whenStable();
    expect(store.page()).toBe(1);
    expect(store.rows().map((entry) => entry.version_id)).toEqual(['version-3']);
    expect(store.versionDetail()?.view.version_id).toBe('version-3');
    expect(store.history.error()).toBe(unavailable);
    store.history.reload();
    await fixture.whenStable();
    expect(store.page()).toBe(2);
    expect(store.selected()).toBe('version-3');
    expect(store.versionDetail()?.view.version_id).toBe('version-3');
    store.compareVersion('version-2');
    await fixture.whenStable();
    expect(store.comparedDetail()?.view.version_id).toBe('version-2');
    store.selectVersion('not-loaded');
    expect(store.selected()).toBe('version-3');
    store.selectVersion('version-2');
    await fixture.whenStable();
    expect(store.versionDetail()?.view.version_id).toBe('version-2');
    store.previousPage();
    await fixture.whenStable();
    expect(store.selected()).toBe('version-2');
  });

  it('refuses a repeated cursor without another request and recovers on refresh', async () => {
    versions.mockResolvedValue({ items: [row(3)], next_cursor: 'older' });
    const { fixture, store } = await mount();
    store.nextPage();
    await fixture.whenStable();
    expect(store.cursorCycle()).toBe(true);
    const calls = versions.mock.calls.length;
    store.nextPage();
    await fixture.whenStable();
    expect(versions).toHaveBeenCalledTimes(calls);
    store.refresh();
    await fixture.whenStable();
    expect(store.page()).toBe(1);
    expect(store.cursorCycle()).toBe(false);
  });

  it('clears pages and selection when the case scope changes', async () => {
    const { fixture, store } = await mount();
    store.selectVersion('version-3');
    store.setPageSize(50);
    await fixture.whenStable();
    store.nextPage();
    await fixture.whenStable();
    versions.mockResolvedValue({ items: [], next_cursor: null });
    scope.set({ org: 'o1', project: 'p1', caseId: 'case2' });
    await fixture.whenStable();
    expect(store.page()).toBe(1);
    expect(store.pageSize()).toBe(20);
    expect(store.rows()).toEqual([]);
    expect(store.selected()).toBe('');
    expect(versions).toHaveBeenLastCalledWith(
      'p1',
      'case2',
      { limit: 20 },
      expect.any(AbortSignal),
    );
  });
});
