import { TestBed } from '@angular/core/testing';
import type {
  CiBindingListResponse,
  CiBindingView,
  IngestTokenListResponse,
  IngestTokenView,
} from '@evidgate/sdk';
import { vi } from 'vitest';
import { SecretsDataService } from '../../../core/data/secrets-data.service';
import { ApiRequestError } from '../../../core/http/api-error';
import { SecretsStore } from './secrets.store';
import tokenFixture from '../../../../../../../fixtures/web-api/ingest-tokens-list-onboarding-token/input.json';
import bindingFixture from '../../../../../../../fixtures/web-api/ci-bindings-list-settings/input.json';

const token = tokenFixture.body.items[0] as IngestTokenView;
const binding = bindingFixture.body.items[0] as CiBindingView;
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
    listCiBindings:
      vi.fn<(project: string, cursor?: string, limit?: number) => Promise<CiBindingListResponse>>(),
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
    data.listCiBindings.mockImplementation((_project, cursor) =>
      Promise.resolve({
        items: [{ ...binding, id: cursor ?? 'first-binding' }],
        next_cursor: cursor ? null : 'second-binding',
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

  it('resets size to the first server page while preserving independent binding state', async () => {
    await store.load('project');
    await store.loadCiBindings('project');
    await store.loadMore();
    await store.loadMoreCiBindings();
    await store.setPageSize(50);
    expect(store.page()).toBe(1);
    expect(store.pageSize()).toBe(50);
    expect(data.listIngestTokens).toHaveBeenLastCalledWith('project', undefined, 50);
    expect(store.ciBindingsPage()).toBe(2);
    await store.setCiBindingsPageSize(100);
    expect(store.ciBindingsPage()).toBe(1);
    expect(data.listCiBindings).toHaveBeenLastCalledWith('project', undefined, 100);
    expect(store.pageSize()).toBe(50);
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

  it('replaces binding pages, retains them on failure and retries without affecting tokens', async () => {
    await store.load('project');
    await store.loadCiBindings('project');
    data.listCiBindings.mockRejectedValueOnce(unavailable);
    await store.loadMoreCiBindings();
    expect(store.ciBindings().map((row) => row.id)).toEqual(['first-binding']);
    expect(store.ciBindingsPage()).toBe(1);
    await store.retryCiBindingsPage();
    expect(store.ciBindings().map((row) => row.id)).toEqual(['second-binding']);
    expect(store.ciBindingsPage()).toBe(2);
    await store.previousCiBindings();
    expect(store.ciBindingsPage()).toBe(1);
    expect(store.rows().map((row) => row.id)).toEqual(['first']);
  });

  it('does not fetch previous on first pages or next after empty and one-page results', async () => {
    data.listIngestTokens.mockResolvedValue({ items: [], next_cursor: null });
    data.listCiBindings.mockResolvedValue({ items: [binding], next_cursor: null });
    await store.load('project');
    await store.loadCiBindings('project');
    await store.previous();
    await store.previousCiBindings();
    await store.loadMore();
    await store.loadMoreCiBindings();
    expect(data.listIngestTokens).toHaveBeenCalledTimes(1);
    expect(data.listCiBindings).toHaveBeenCalledTimes(1);
    expect(store.rows()).toEqual([]);
    expect(store.hasMoreCiBindings()).toBe(false);
  });

  it('rejects unsupported page sizes without requests', async () => {
    await store.load('project');
    await store.loadCiBindings('project');
    await store.setPageSize(200);
    await store.setCiBindingsPageSize(25);
    expect(data.listIngestTokens).toHaveBeenCalledTimes(1);
    expect(data.listCiBindings).toHaveBeenCalledTimes(1);
  });
});
