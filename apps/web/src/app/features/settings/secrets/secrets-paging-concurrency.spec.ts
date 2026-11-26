import { TestBed } from '@angular/core/testing';
import type { IngestTokenListResponse, IngestTokenView } from '@evidgate/sdk';
import { vi } from 'vitest';

import { SecretsDataService } from '../../../core/data/secrets-data.service';
import { ApiRequestError } from '../../../core/http/api-error';
import { SecretsStore } from './secrets.store';
import tokenFixture from '../../../../../../../fixtures/web-api/ingest-tokens-list-onboarding-token/input.json';

const token = tokenFixture.body.items[0] as IngestTokenView;
const unavailable = new ApiRequestError({
  kind: 'unavailable',
  status: 503,
  code: 'unavailable',
  message: 'Unavailable',
  requestId: 'concurrent-paging',
  retryable: true,
  retryAfterSeconds: null,
  details: [],
});

function deferred<T>() {
  let resolve: (value: T) => void = () => undefined;
  let reject: (reason: unknown) => void = () => undefined;
  const promise = new Promise<T>((accept, refuse) => {
    resolve = accept;
    reject = refuse;
  });
  return { promise, resolve, reject };
}

describe('Secrets paging independent request boundaries', () => {
  const data = {
    listIngestTokens: vi.fn<SecretsDataService['listIngestTokens']>(),
  };
  let store: SecretsStore;

  beforeEach(() => {
    vi.resetAllMocks();
    data.listIngestTokens.mockImplementation((project, cursor) =>
      Promise.resolve({
        items: [{ ...token, id: `${project}-${cursor ?? 'first'}` }],
        next_cursor: cursor ? null : 'token-next',
      }),
    );
    TestBed.configureTestingModule({
      providers: [SecretsStore, { provide: SecretsDataService, useValue: data }],
    });
    store = TestBed.inject(SecretsStore);
  });
  afterEach(() => TestBed.resetTestingModule());

  it('ignores a second Next while the token page request is pending', async () => {
    await store.load('project');
    const tokens = deferred<IngestTokenListResponse>();
    data.listIngestTokens.mockReturnValueOnce(tokens.promise);
    const tokenMove = store.loadMore();
    await store.loadMore();
    expect(data.listIngestTokens).toHaveBeenCalledTimes(2);
    expect(store.page()).toBe(1);
    expect(store.rows().map((row) => row.id)).toEqual(['project-first']);
    tokens.resolve({ items: [{ ...token, id: 'token-second' }], next_cursor: null });
    await tokenMove;
    expect(store.page()).toBe(2);
    expect(store.rows().map((row) => row.id)).toEqual(['token-second']);
  });

  it('discards a failed token page after a new project has loaded', async () => {
    await store.load('old');
    const stale = deferred<IngestTokenListResponse>();
    data.listIngestTokens.mockReturnValueOnce(stale.promise);
    const move = store.loadMore();
    await store.load('new');
    stale.reject(unavailable);
    await move;
    expect(store.projectId()).toBe('new');
    expect(store.rows().map((row) => row.id)).toEqual(['new-first']);
    expect(store.page()).toBe(1);
    expect(store.loadError()).toBeNull();
    expect(store.paging()).toBe(false);
    await store.retryPage();
    expect(data.listIngestTokens).toHaveBeenCalledTimes(3);
  });

  it('retries a failed first-page reset instead of stranding retained later-page rows', async () => {
    await store.load('project');
    await store.setPageSize(50);
    await store.loadMore();
    data.listIngestTokens.mockRejectedValueOnce(unavailable);
    await store.load('project');
    expect(store.rows().map((row) => row.id)).toEqual(['project-token-next']);
    expect(store.loadError()?.kind).toBe('unavailable');
    expect(store.page()).toBe(2);
    expect(store.pageSize()).toBe(50);
    expect(store.hasMore()).toBe(false);
    expect(store.phase()).toBe('ready');
    const reads = data.listIngestTokens.mock.calls.length;
    await store.retryPage();
    expect(data.listIngestTokens).toHaveBeenCalledTimes(reads + 1);
    expect(data.listIngestTokens).toHaveBeenLastCalledWith('project', undefined, 50);
    expect(store.rows().map((row) => row.id)).toEqual(['project-first']);
    expect(store.loadError()).toBeNull();
    expect(store.page()).toBe(1);
    expect(store.pageSize()).toBe(50);
    expect(store.hasMore()).toBe(true);
    await store.previous();
    await store.retryPage();
    expect(data.listIngestTokens).toHaveBeenCalledTimes(reads + 1);
  });

  it('retains opaque Previous history when a first-page filter reset fails', async () => {
    await store.load('project');
    data.listIngestTokens.mockResolvedValueOnce({
      items: [{ ...token, id: 'second' }],
      next_cursor: 'token-third',
    });
    await store.loadMore();
    await store.loadMore();
    data.listIngestTokens.mockRejectedValueOnce(unavailable);
    await store.load('project');
    expect(store.page()).toBe(3);
    expect(store.rows().map((row) => row.id)).toEqual(['project-token-third']);
    await store.previous();
    expect(data.listIngestTokens).toHaveBeenLastCalledWith('project', 'token-next', 20);
    expect(store.page()).toBe(2);
    expect(store.rows().map((row) => row.id)).toEqual(['project-token-next']);
  });

  it('commits a successful filter reset to page one only after its response arrives', async () => {
    await store.load('project');
    await store.setPageSize(50);
    await store.loadMore();
    const refresh = deferred<IngestTokenListResponse>();
    data.listIngestTokens.mockReturnValueOnce(refresh.promise);
    const reset = store.load('project');
    expect(store.rows().map((row) => row.id)).toEqual(['project-token-next']);
    expect(store.page()).toBe(2);
    expect(store.pageSize()).toBe(50);
    expect(data.listIngestTokens).toHaveBeenLastCalledWith('project', undefined, 50);
    refresh.resolve({ items: [{ ...token, id: 'reset-first' }], next_cursor: 'reset-next' });
    await reset;
    expect(store.rows().map((row) => row.id)).toEqual(['reset-first']);
    expect(store.page()).toBe(1);
    expect(store.pageSize()).toBe(50);
    expect(store.phase()).toBe('ready');
    const reads = data.listIngestTokens.mock.calls.length;
    await store.previous();
    expect(data.listIngestTokens).toHaveBeenCalledTimes(reads);
    await store.loadMore();
    expect(data.listIngestTokens).toHaveBeenLastCalledWith('project', 'reset-next', 50);
  });

  it.each(['success', 'failure'] as const)(
    'discards an older filter refresh %s after the size-50 replacement succeeds',
    async (outcome) => {
      await store.load('project');
      await store.loadMore();
      const refresh = deferred<IngestTokenListResponse>();
      const resize = deferred<IngestTokenListResponse>();
      data.listIngestTokens
        .mockReturnValueOnce(refresh.promise)
        .mockReturnValueOnce(resize.promise);
      const reset = store.load('project');
      const replacement = store.setPageSize(50);
      expect(data.listIngestTokens).toHaveBeenLastCalledWith('project', undefined, 50);
      resize.resolve({ items: [{ ...token, id: 'size-50' }], next_cursor: 'size-50-next' });
      await replacement;
      if (outcome === 'success') {
        refresh.resolve({ items: [{ ...token, id: 'stale-size-20' }], next_cursor: null });
      } else {
        refresh.reject(unavailable);
      }
      await reset;
      expect(store.rows().map((row) => row.id)).toEqual(['size-50']);
      expect(store.page()).toBe(1);
      expect(store.pageSize()).toBe(50);
      expect(store.hasMore()).toBe(true);
      expect(store.phase()).toBe('ready');
      expect(store.paging()).toBe(false);
      expect(store.loadError()).toBeNull();
      await store.retryPage();
      expect(data.listIngestTokens).toHaveBeenCalledTimes(4);
      await store.loadMore();
      expect(data.listIngestTokens).toHaveBeenLastCalledWith('project', 'size-50-next', 50);
    },
  );

  it('settles an untyped replacement failure after superseding a filter refresh and still rethrows', async () => {
    await store.load('project');
    await store.loadMore();
    const retained = store.rows();
    const refresh = deferred<IngestTokenListResponse>();
    const resize = deferred<IngestTokenListResponse>();
    data.listIngestTokens.mockReturnValueOnce(refresh.promise).mockReturnValueOnce(resize.promise);
    const reset = store.load('project');
    const replacement = store.setPageSize(50);
    resize.reject(new Error('replacement failed'));
    await expect(replacement).rejects.toThrow('replacement failed');
    expect(store.phase()).toBe('ready');
    expect(store.paging()).toBe(false);
    expect(store.rows()).toBe(retained);
    expect(store.page()).toBe(2);
    expect(store.pageSize()).toBe(20);
    expect(store.loadError()).toBeNull();
    refresh.resolve({ items: [{ ...token, id: 'stale-refresh' }], next_cursor: null });
    await reset;
    expect(store.phase()).toBe('ready');
    expect(store.rows()).toBe(retained);
    await store.retryPage();
    expect(data.listIngestTokens).toHaveBeenLastCalledWith('project', undefined, 50);
    expect(store.page()).toBe(1);
    expect(store.pageSize()).toBe(50);
    expect(store.rows().map((row) => row.id)).toEqual(['project-first']);
  });
});
