import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { AuthoringVersionListResponse } from '@evidgate/sdk';
import { SessionStore } from '../../../core/session/session.store';
import { ApiRequestError } from '../../../core/http/api-error';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow';
import { ImportExportDataService } from './import-export-data.service';
import { ImportStore } from './import.store';

const page = (id: string, next: string | null): AuthoringVersionListResponse => ({
  items: [
    {
      version_id: id,
      entity_kind: 'feature_node',
      entity_id: 'node',
      version_no: 1,
      batch_id: 'batch',
      change_kind: 'import',
      source: 'import',
      content_hash: 'sha256:' + 'a'.repeat(64),
      author: null,
      reason: null,
      ai_run_id: null,
      created_at: '2026-11-19T10:00:00Z',
    },
  ],
  next_cursor: next,
});
const offline = () =>
  new ApiRequestError({
    kind: 'network',
    status: 0,
    code: 'network',
    message: 'offline',
    details: [],
    requestId: null,
    retryable: true,
    retryAfterSeconds: null,
  });

describe('Import history refresh and reset boundaries', () => {
  const versions = vi.fn<ImportExportDataService['listVersions']>();
  let store: InstanceType<typeof ImportStore>;
  beforeEach(() => {
    versions
      .mockReset()
      .mockImplementation((_project, _signal, cursor) =>
        Promise.resolve(
          cursor === 'second'
            ? page('middle', 'third')
            : cursor === 'third'
              ? page('last', null)
              : page('first', 'second'),
        ),
      );
    TestBed.configureTestingModule({
      providers: [
        ImportStore,
        { provide: ImportExportDataService, useValue: { listVersions: versions } },
        {
          provide: SessionStore,
          useValue: {
            currentProject: signal({ id: 'project' }),
            has: () => true,
            onSessionEnd: () => () => undefined,
          },
        },
        { provide: NarrowMutationPolicy, useValue: { allowsMutation: () => true } },
      ],
    });
    store = TestBed.inject(ImportStore);
  });
  afterEach(() => TestBed.resetTestingModule());
  async function loaded(id: string, number: number) {
    await vi.waitFor(() => {
      expect(store.history.isLoading()).toBe(false);
      expect(store.historyDisplay()?.page?.items[0]?.version_id).toBe(id);
      expect(store.historyPage()).toBe(number);
    });
  }

  it('can retry the failed latest refresh after preserving the third-page history', async () => {
    store.selectFile(new File(['{}'], 'source.json'));
    await loaded('first', 1);
    store.nextHistory();
    await loaded('middle', 2);
    store.nextHistory();
    await loaded('last', 3);
    versions.mockRejectedValueOnce(offline());
    store.refreshHistory();
    await vi.waitFor(() => expect(store.historyDisplay()?.error?.kind).toBe('network'));
    expect(store.historyCursors()).toEqual([null, 'second', 'third']);
    await loaded('last', 3);
    store.retryHistory();
    await loaded('first', 1);
    expect(versions).toHaveBeenLastCalledWith('project', expect.any(AbortSignal), undefined, 20);
    expect(store.historyCursors()).toEqual([null]);
    store.nextHistory();
    await loaded('middle', 2);
    expect(versions).toHaveBeenLastCalledWith('project', expect.any(AbortSignal), 'second', 20);
  });

  it('does not expose or navigate the old-size history after a failed size change', async () => {
    store.selectFile(new File(['{}'], 'source.json'));
    await loaded('first', 1);
    store.nextHistory();
    await loaded('middle', 2);
    versions.mockRejectedValueOnce(offline());
    store.setHistoryPageSize(100);
    await vi.waitFor(() => expect(store.historyDisplay()?.error?.kind).toBe('network'));
    expect(store.historyDisplay()?.page).toBeNull();
    expect(store.historyCursors()).toEqual([null]);
    const calls = versions.mock.calls.length;
    store.previousHistory();
    store.nextHistory();
    expect(versions).toHaveBeenCalledTimes(calls);
    store.retryHistory();
    await loaded('first', 1);
    expect(versions).toHaveBeenLastCalledWith('project', expect.any(AbortSignal), undefined, 100);
  });
});
