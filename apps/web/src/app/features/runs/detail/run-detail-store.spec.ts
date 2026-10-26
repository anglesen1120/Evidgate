import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { RunStateDto } from '@evidgate/sdk';
import ready from '../../../../../../../fixtures/web-api/run-detail/input.json';
import { RunsDataService } from '../../../core/data/runs-data.service';
import { ApiRequestError, type ApiError } from '../../../core/http/api-error';
import { UNAVAILABLE_MANIFEST } from './manifest-summary';
import { RunDetailStore } from './run-detail.store';

// The fixture-contract spec validates this body against getRun's RunStateDto schema.
const run = ready.body as RunStateDto;
function refused(kind: 'not_found' | 'forbidden'): ApiRequestError {
  const error: ApiError = {
    kind,
    status: kind === 'not_found' ? 404 : 403,
    code: kind,
    message: 'Resource unavailable.',
    requestId: 'request-detail',
    retryable: false,
    details: [],
    retryAfterSeconds: null,
  };
  return new ApiRequestError(error);
}
function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason: unknown) => void;
  const promise = new Promise<T>((yes, no) => {
    resolve = yes;
    reject = no;
  });
  return { promise, resolve, reject };
}
describe('RunDetailStore', () => {
  const get = vi.fn<(id: string) => Promise<RunStateDto>>();
  beforeEach(() => {
    get.mockReset();
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        RunDetailStore,
        { provide: RunsDataService, useValue: { get } },
      ],
    });
  });
  afterEach(() => TestBed.resetTestingModule());
  it('maps only the stored response and an all-null manifest presentation model', async () => {
    get.mockResolvedValue(run);
    const store = TestBed.inject(RunDetailStore);
    expect(store.status()).toBe('idle');
    const loading = store.load(run.run_id);
    expect(store.status()).toBe('loading');
    expect(store.run()).toBeNull();
    await loading;
    expect(get).toHaveBeenCalledWith(run.run_id);
    expect(store.status()).toBe('ready');
    expect(store.run()).toEqual(run);
    expect(store.manifest()).toEqual(UNAVAILABLE_MANIFEST);
  });
  it('foreign and missing ids produce identical not-found state without retaining run data', async () => {
    const store = TestBed.inject(RunDetailStore);
    const states = [];
    for (const id of ['foreign-id', 'missing-id']) {
      get.mockResolvedValueOnce(run);
      await store.load(run.run_id);
      get.mockRejectedValueOnce(refused('not_found'));
      await store.load(id);
      states.push({ status: store.status(), run: store.run(), manifest: store.manifest() });
      expect(store.status()).toBe('not-found');
      expect(store.run()).toBeNull();
    }
    expect(states[0]).toEqual(states[1]);
  });
  it('403 yields forbidden, never not-found, and clears previously loaded data', async () => {
    const store = TestBed.inject(RunDetailStore);
    get.mockResolvedValueOnce(run);
    await store.load(run.run_id);
    get.mockRejectedValueOnce(refused('forbidden'));
    await store.load('denied');
    expect(store.status()).toBe('forbidden');
    expect(store.run()).toBeNull();
    expect(store.manifest()).toEqual(UNAVAILABLE_MANIFEST);
  });
  it.each(['success', 'failure'] as const)(
    'ignores stale %s after route identity changes',
    async (result) => {
      const old = deferred<RunStateDto>();
      get.mockReturnValueOnce(old.promise).mockResolvedValueOnce({ ...run, version: 8 });
      const store = TestBed.inject(RunDetailStore);
      const previous = store.load('old');
      await store.load('new');
      if (result === 'success') old.resolve(run);
      else old.reject(refused('not_found'));
      await previous;
      expect(store.status()).toBe('ready');
      expect(store.run()?.version).toBe(8);
      expect(store.loadError()).toBeNull();
    },
  );
  it('clears data synchronously when a new request starts and surfaces unexpected failures', async () => {
    get.mockResolvedValueOnce(run).mockRejectedValueOnce(new Error('private diagnostic'));
    const store = TestBed.inject(RunDetailStore);
    await store.load(run.run_id);
    const loading = store.load('next');
    expect(store.run()).toBeNull();
    expect(store.status()).toBe('loading');
    await loading;
    expect(store.status()).toBe('error');
    expect(store.loadError()?.kind).toBe('unexpected');
    expect(store.loadError()?.message).not.toContain('private diagnostic');
  });
});
