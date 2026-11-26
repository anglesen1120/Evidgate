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
  requestId: 'paging',
  retryable: true,
  retryAfterSeconds: null,
  details: [],
});

describe('SecretsStore cursor pages', () => {
  const data = {
    listIngestTokens:
      vi.fn<
        (project: string, cursor?: string, limit?: number) => Promise<IngestTokenListResponse>
      >(),
  };
  let store: SecretsStore;

  beforeEach(() => {
    vi.resetAllMocks();
    data.listIngestTokens.mockImplementation((_project, cursor) =>
      Promise.resolve({
        items: [{ ...token, id: cursor ?? 'first' }],
        next_cursor: cursor === 'last' ? null : cursor ? 'last' : 'middle',
      }),
    );
    TestBed.configureTestingModule({
      providers: [SecretsStore, { provide: SecretsDataService, useValue: data }],
    });
    store = TestBed.inject(SecretsStore);
  });
  afterEach(() => TestBed.resetTestingModule());

  it('replaces first, middle and last token pages and follows Previous back to the original cursor', async () => {
    await store.load('project');
    expect(store.page()).toBe(1);
    expect(data.listIngestTokens).toHaveBeenLastCalledWith('project', undefined, 20);
    await store.loadMore();
    expect(store.rows().map((row) => row.id)).toEqual(['middle']);
    expect(store.page()).toBe(2);
    await store.loadMore();
    expect(store.rows().map((row) => row.id)).toEqual(['last']);
    expect(store.hasMore()).toBe(false);
    await store.previous();
    expect(store.page()).toBe(2);
    expect(store.rows().map((row) => row.id)).toEqual(['middle']);
    await store.previous();
    expect(store.page()).toBe(1);
    expect(data.listIngestTokens).toHaveBeenLastCalledWith('project', undefined, 20);
  });

  it('keeps failed later token pages visible and retries the same Previous cursor', async () => {
    await store.load('project');
    await store.loadMore();
    data.listIngestTokens.mockRejectedValueOnce(unavailable);
    await store.previous();
    expect(store.page()).toBe(2);
    expect(store.rows().map((row) => row.id)).toEqual(['middle']);
    expect(store.loadError()?.kind).toBe('unavailable');
    await store.retryPage();
    expect(store.page()).toBe(1);
    expect(store.loadError()).toBeNull();
  });

  it('resets to the first server page when the token page size changes', async () => {
    await store.load('project');
    await store.loadMore();
    await store.setPageSize(50);
    expect(store.page()).toBe(1);
    expect(store.pageSize()).toBe(50);
    expect(data.listIngestTokens).toHaveBeenLastCalledWith('project', undefined, 50);
  });

  it('keeps the current size and page on a failed size change and retries the requested size', async () => {
    await store.load('project');
    await store.loadMore();
    data.listIngestTokens.mockRejectedValueOnce(unavailable);
    await store.setPageSize(100);
    expect(store.page()).toBe(2);
    expect(store.pageSize()).toBe(20);
    await store.retryPage();
    expect(store.page()).toBe(1);
    expect(store.pageSize()).toBe(100);
  });

  it.each([
    { label: 'empty', items: [] as IngestTokenView[] },
    { label: 'one-page', items: [token] },
  ])('does not navigate past a single $label token result', async ({ items }) => {
    data.listIngestTokens.mockResolvedValue({ items, next_cursor: null });
    await store.load('project');
    await store.previous();
    await store.loadMore();
    expect(data.listIngestTokens).toHaveBeenCalledTimes(1);
    expect(store.rows()).toEqual(items);
    expect(store.hasMore()).toBe(false);
  });

  it('rejects unsupported page sizes without requests', async () => {
    await store.load('project');
    await store.setPageSize(200);
    expect(data.listIngestTokens).toHaveBeenCalledTimes(1);
  });
});
