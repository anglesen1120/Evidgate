import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { AuthoringVersionListResponse } from '@evidgate/sdk';
import { SessionStore } from '../../../core/session/session.store';
import { ApiRequestError } from '../../../core/http/api-error';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow/narrow-mutation-policy';
import { ImportExportDataService } from './import-export-data.service';
import { ImportStore } from './import.store';

function page(id: string | null, next: string | null): AuthoringVersionListResponse {
  return {
    items:
      id === null
        ? []
        : [
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
  };
}

const failure = () =>
  new ApiRequestError({
    kind: 'unavailable',
    status: 503,
    code: 'unavailable',
    message: 'Unavailable',
    requestId: null,
    retryable: true,
    retryAfterSeconds: null,
    details: [],
  });

describe('Import history cursor pages', () => {
  const versions = vi.fn<ImportExportDataService['listVersions']>();
  let store: InstanceType<typeof ImportStore>;
  beforeEach(() => {
    versions.mockReset().mockResolvedValue(page(null, null));
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

  async function loaded(id: string | null, number: number): Promise<void> {
    await vi.waitFor(() => {
      expect(store.history.isLoading()).toBe(false);
      expect(store.historyPage()).toBe(number);
      expect(store.historyDisplay()?.page?.items.map((item) => item.version_id)).toEqual(
        id === null ? [] : [id],
      );
    });
  }

  it.each([null, 'only'])('keeps empty/one-page history at page one: %s', async (id) => {
    versions.mockResolvedValue(page(id, null));
    store.selectFile(new File(['{}'], 'source.json'));
    await loaded(id, 1);
    expect(versions).toHaveBeenLastCalledWith('project', expect.any(AbortSignal), undefined, 20);
    store.nextHistory();
    store.previousHistory();
    expect(versions).toHaveBeenCalledTimes(1);
  });

  it('visits first, middle, last, then Previous after Next with the original cursor', async () => {
    versions
      .mockResolvedValueOnce(page('first', 'second'))
      .mockResolvedValueOnce(page('middle', 'third'))
      .mockResolvedValueOnce(page('last', null))
      .mockResolvedValueOnce(page('middle', 'third'));
    store.selectFile(new File(['{}'], 'source.json'));
    await loaded('first', 1);
    store.nextHistory();
    await loaded('middle', 2);
    store.nextHistory();
    await loaded('last', 3);
    store.nextHistory();
    expect(versions).toHaveBeenCalledTimes(3);
    store.previousHistory();
    await loaded('middle', 2);
    expect(versions.mock.calls.map((call) => call[2])).toEqual([
      undefined,
      'second',
      'third',
      'second',
    ]);
  });

  it.each(['unavailable', 'timeout'] as const)(
    'retains the successful page and retries a failed forward cursor after %s',
    async (kind) => {
      const error = failure();
      versions
        .mockResolvedValueOnce(page('first', 'second'))
        .mockRejectedValueOnce(new ApiRequestError({ ...error.error, kind }))
        .mockResolvedValueOnce(page('second', null));
      store.selectFile(new File(['{}'], 'source.json'));
      await loaded('first', 1);
      store.nextHistory();
      await vi.waitFor(() => expect(store.historyDisplay()?.error?.kind).toBe(kind));
      await loaded('first', 1);
      store.retryHistory();
      await loaded('second', 2);
      expect(versions.mock.calls.map((call) => call[2])).toEqual([undefined, 'second', 'second']);
    },
  );

  it('keeps the earlier page while the next request is pending and refuses a repeated cursor', async () => {
    let resolve: (page: AuthoringVersionListResponse) => void = () => {
      throw new Error('Pending read has not started');
    };
    const pending = new Promise<AuthoringVersionListResponse>((finish) => {
      resolve = finish;
    });
    versions
      .mockResolvedValueOnce(page('first', 'second'))
      .mockReturnValueOnce(pending)
      .mockResolvedValueOnce(page('second', null));
    store.selectFile(new File(['{}'], 'source.json'));
    await loaded('first', 1);
    store.nextHistory();
    await vi.waitFor(() => expect(store.history.isLoading()).toBe(true));
    expect(store.historyDisplay()?.page?.items[0]?.version_id).toBe('first');
    expect(store.historyPage()).toBe(1);
    resolve(page('invalid', 'second'));
    await vi.waitFor(() => expect(store.historyDisplay()?.error?.code).toBe('repeated_cursor'));
    await loaded('first', 1);
    store.retryHistory();
    await loaded('second', 2);
    expect(store.historyCursors()).toEqual([null, 'second']);
  });

  it('keeps committed cursors after a failed refresh so Previous still requests page two', async () => {
    versions
      .mockResolvedValueOnce(page('first', 'second'))
      .mockResolvedValueOnce(page('middle', 'third'))
      .mockResolvedValueOnce(page('last', null))
      .mockRejectedValueOnce(new ApiRequestError({ ...failure().error, kind: 'timeout' }))
      .mockImplementation((_project, _signal, cursor) =>
        Promise.resolve(cursor === 'second' ? page('middle', 'third') : page('first', 'second')),
      );
    store.selectFile(new File(['{}'], 'source.json'));
    await loaded('first', 1);
    store.nextHistory();
    await loaded('middle', 2);
    store.nextHistory();
    await loaded('last', 3);
    store.refreshHistory();
    await vi.waitFor(() => expect(store.historyDisplay()?.error?.kind).toBe('timeout'));
    await loaded('last', 3);
    expect(store.historyCursors()).toEqual([null, 'second', 'third']);
    store.previousHistory();
    await loaded('middle', 2);
    expect(versions).toHaveBeenLastCalledWith('project', expect.any(AbortSignal), 'second', 20);
  });

  it('resets the cursor on each supported size and refreshes latest without changing size', async () => {
    versions.mockImplementation((_project, _signal, cursor) =>
      Promise.resolve(page('row', cursor ? null : 'next')),
    );
    store.selectFile(new File(['{}'], 'source.json'));
    await loaded('row', 1);
    store.nextHistory();
    await loaded('row', 2);
    for (const size of [50, 100, 20]) {
      store.setHistoryPageSize(size);
      await vi.waitFor(() =>
        expect(versions).toHaveBeenLastCalledWith(
          'project',
          expect.any(AbortSignal),
          undefined,
          size,
        ),
      );
      await loaded('row', 1);
    }
    store.nextHistory();
    await loaded('row', 2);
    store.refreshHistory();
    await loaded('row', 1);
    expect(versions).toHaveBeenLastCalledWith('project', expect.any(AbortSignal), undefined, 20);
    const calls = versions.mock.calls.length;
    store.setHistoryPageSize(25);
    expect(versions).toHaveBeenCalledTimes(calls);
    expect(store.historyPageSize()).toBe(20);
  });
});
