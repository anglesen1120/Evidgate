import { TestBed } from '@angular/core/testing';
import type { RunStateDto } from '@evidgate/sdk';
import { patchState, signalStore, withMethods, withState } from '@ngrx/signals';
import ready from '../../../../../../../../fixtures/web-api/run-detail/input.json';
import { RunsDataService } from '../../../../core/data/runs-data.service';
import { ApiRequestError, type ApiError } from '../../../../core/http/api-error';
import { manifestFromRun, type ManifestSummary } from '../../detail/manifest-summary';
import type { RunDetailStatus } from '../../detail/run-detail.store';
import { deferred } from '../../detail/run-detail-store.fixtures';
import { withRunRefresh } from './with-run-refresh.feature';

const collecting: RunStateDto = { ...ready.body, state: 'collecting' } as RunStateDto;
const Store = signalStore(
  withState<{
    run: RunStateDto | null;
    manifest: ManifestSummary;
    status: RunDetailStatus;
    loadError: ApiError | null;
  }>({
    run: collecting,
    manifest: manifestFromRun(collecting),
    status: 'ready',
    loadError: null,
  }),
  withRunRefresh(),
  withMethods((store) => ({
    setRun(run: RunStateDto): void {
      patchState(store, { run });
    },
  })),
);

describe('run state refresh', () => {
  afterEach(() => TestBed.resetTestingModule());
  function setup() {
    const get = vi.fn<(id: string) => Promise<RunStateDto>>();
    TestBed.configureTestingModule({
      providers: [Store, { provide: RunsDataService, useValue: { get } }],
    });
    return { store: TestBed.inject(Store), get };
  }
  it('keeps a canceled terminal state when an older collecting read finishes later', async () => {
    const h = setup();
    const old = deferred<RunStateDto>();
    h.get
      .mockReturnValueOnce(old.promise)
      .mockResolvedValueOnce({ ...collecting, state: 'canceled', version: collecting.version + 1 });
    const stale = h.store.refreshRun(collecting.run_id);
    await h.store.refreshRun(collecting.run_id);
    old.resolve(collecting);
    await stale;
    expect(h.store.run()?.state).toBe('canceled');
    expect(h.store.run()?.version).toBe(collecting.version + 1);
  });
  it('does not replace a terminal state with collecting even at the same version', async () => {
    const h = setup();
    h.store.setRun({ ...collecting, state: 'canceled' });
    h.get.mockResolvedValue(collecting);
    await h.store.refreshRun(collecting.run_id);
    expect(h.store.run()?.state).toBe('canceled');
  });
  it('does not write a response into another run or into a destroyed store', async () => {
    const h = setup();
    const first = deferred<RunStateDto>();
    h.get.mockReturnValue(first.promise);
    const pending = h.store.refreshRun(collecting.run_id);
    h.store.setRun({ ...collecting, run_id: 'other-run' });
    first.resolve({ ...collecting, state: 'canceled' });
    await pending;
    expect(h.store.run()?.run_id).toBe('other-run');
    const next = deferred<RunStateDto>();
    h.get.mockReturnValue(next.promise);
    const destroyed = h.store.refreshRun('other-run');
    TestBed.resetTestingModule();
    next.resolve({ ...collecting, run_id: 'other-run', state: 'canceled' });
    await destroyed;
    expect(h.store.run()?.state).toBe('collecting');
  });
  it.each(['not_found', 'forbidden'] as const)(
    'clears the run on an authoritative %s refusal',
    async (kind) => {
      const h = setup();
      const failure = new ApiRequestError({
        kind,
        status: kind === 'not_found' ? 404 : 403,
        code: kind,
        message: kind,
        requestId: 'refresh-refused',
        retryable: false,
        details: [],
        retryAfterSeconds: null,
      });
      h.get.mockRejectedValue(failure);
      await expect(h.store.refreshRun(collecting.run_id)).rejects.toBe(failure);
      expect(h.store.run()).toBeNull();
      expect(h.store.status()).toBe(kind === 'not_found' ? 'not-found' : 'forbidden');
    },
  );
});
