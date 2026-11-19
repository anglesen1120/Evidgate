import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import type { ImportResponse } from '@evidgate/sdk';
import { SessionStore } from '../../../core/session/session.store';
import { ApiRequestError, type ApiError } from '../../../core/http/api-error';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow/narrow-mutation-policy';
import { ImportExportDataService } from './import-export-data.service';
import { ImportStore } from './import.store';

const report: ImportResponse = {
  batch_id: null,
  counts: { created: 1, updated: 0, restored: 0, unchanged: 0 },
  rows: [{ index: 1, key: 'n1', kind: 'node', outcome: 'create', code: null }],
};
export function refusal(
  kind: ApiError['kind'] = 'validation',
  details: ApiError['details'] = [],
): ApiRequestError {
  return new ApiRequestError({
    kind,
    status: kind === 'validation' ? 422 : 503,
    code: kind,
    message: 'Server data must not be echoed',
    details,
    requestId: null,
    retryable: kind === 'unavailable',
    retryAfterSeconds: null,
  });
}

describe('ImportStore confirmation and refusal boundary', () => {
  let store: InstanceType<typeof ImportStore>;
  const upload = vi.fn<ImportExportDataService['importFile']>();
  let desktop = true;
  let permitted = true;
  const project = signal({ id: 'project', case_prefix: 'SHOP' });
  beforeEach(() => {
    upload.mockReset().mockResolvedValue(report);
    desktop = true;
    permitted = true;
    project.set({ id: 'project', case_prefix: 'SHOP' });
    TestBed.configureTestingModule({
      providers: [
        ImportStore,
        { provide: ImportExportDataService, useValue: { importFile: upload } },
        {
          provide: SessionStore,
          useValue: {
            currentProject: project,
            has: () => permitted,
            onSessionEnd: () => () => undefined,
          },
        },
        { provide: NarrowMutationPolicy, useValue: { allowsMutation: () => desktop } },
      ],
    });
    store = TestBed.inject(ImportStore);
  });
  afterEach(() => TestBed.resetTestingModule());

  it('selects and locally previews a file without any write, then dry-runs before explicit confirmation', async () => {
    store.selectFile(new File(['{}'], 'file.json'));
    expect(upload).not.toHaveBeenCalled();
    await store.confirm();
    expect(upload).not.toHaveBeenCalled();
    await store.preview();
    expect(upload).toHaveBeenCalledTimes(1);
    expect(upload.mock.calls[0]?.[3]).toBe(true);
    expect(store.canConfirm()).toBe(true);
    await store.confirm();
    expect(upload.mock.calls.map((call) => call[3])).toEqual([true, false]);
    expect(upload.mock.calls[0]?.[4]).toBe(upload.mock.calls[1]?.[4]);
  });

  it('refuses a file strictly greater than 5 MiB before any server request, but accepts the exact cap', async () => {
    store.selectFile(new File([new Uint8Array(5 * 1024 * 1024 + 1)], 'large.csv'));
    await store.preview();
    expect(upload).not.toHaveBeenCalled();
    expect(store.work().error?.kind).toBe('payload_too_large');
    store.selectFile(new File([new Uint8Array(5 * 1024 * 1024)], 'limit.csv'));
    await store.preview();
    expect(upload).toHaveBeenCalledTimes(1);
  });

  it('keeps all refusal details and exposes no valid/invalid totals after mixed 422 rows', async () => {
    store.selectFile(new File(['{}'], 'file.json'));
    upload.mockRejectedValueOnce(
      refusal('validation', [{ index: 2, path: '/code', reason: 'code_conflict' }]),
    );
    await store.preview();
    expect(store.work().error?.details).toEqual([
      { index: 2, path: '/code', reason: 'code_conflict' },
    ]);
    expect(store.work().report).toBeNull();
    expect(store.canConfirm()).toBe(false);
    await store.confirm();
    expect(upload).toHaveBeenCalledTimes(1);
  });

  it('retries preview and uncertain confirmed writes with the same idempotency key', async () => {
    store.selectFile(new File(['{}'], 'file.json'));
    upload.mockRejectedValueOnce(refusal('unavailable'));
    await store.preview();
    await store.preview();
    upload.mockRejectedValueOnce(refusal('unavailable'));
    await store.confirm();
    await store.retry();
    expect(new Set(upload.mock.calls.map((call) => call[4])).size).toBe(1);
    expect(upload.mock.calls.map((call) => call[3])).toEqual([true, true, false, false]);
  });

  it('honours Retry-After without automatic writes and rejects narrow or revoked confirmation', async () => {
    vi.useFakeTimers();
    store.selectFile(new File(['{}'], 'file.json'));
    const error = refusal('unavailable');
    upload.mockRejectedValueOnce(new ApiRequestError({ ...error.error, retryAfterSeconds: 30 }));
    await store.preview();
    await store.preview();
    expect(upload).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(30_000);
    expect(upload).toHaveBeenCalledTimes(1);
    await store.preview();
    desktop = false;
    await store.confirm();
    desktop = true;
    permitted = false;
    await store.confirm();
    expect(upload).toHaveBeenCalledTimes(2);
    vi.useRealTimers();
  });

  it('starts a fresh intent after file or format changes and never applies a stale project report', async () => {
    store.selectFile(new File(['{}'], 'file.json'));
    await store.preview();
    const first = store.work().key;
    store.setFormat('csv');
    expect(store.work().key).not.toBe(first);
    expect(store.canConfirm()).toBe(false);
    project.set({ id: 'other-project', case_prefix: 'OTHER' });
    expect(store.work().file).toBeNull();
    await store.confirm();
    expect(upload).toHaveBeenCalledTimes(1);
  });

  it('distinguishes a confirmed batch from a no-op without inventing a version', async () => {
    store.selectFile(new File(['{}'], 'file.json'));
    await store.preview();
    await store.confirm();
    expect(store.work().committed?.batch_id).toBeNull();
    expect(store.canConfirm()).toBe(false);
  });
});
