import { TestBed } from '@angular/core/testing';
import { QuotaExceededRelay } from '../../../core/http/quota-exceeded-relay';
import { flush, mountRetention, refusal, STORED } from './retention-form.test-support';

/** The shape `newUuid()` (core/http/random-id.ts) produces for a pipeline-generated key. */
const UUID_V4 = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;

describe('RetentionStore', () => {
  afterEach(() => {
    document.cookie = 'XSRF-TOKEN=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/';
    vi.restoreAllMocks();
    TestBed.resetTestingModule();
  });

  it('uses generated GET and PUT operations with the existing XSRF pipeline', async () => {
    document.cookie = 'XSRF-TOKEN=retention-xsrf; path=/';
    const h = mountRetention();
    const get = await h.take('GET');
    expect(get.request.headers.has('X-XSRF-TOKEN')).toBe(false);
    flush(get);
    await h.settle();
    const saving = h.store.save({ artifact_days: 30, metadata_days: 90 });
    const put = await h.take('PUT');
    expect(put.request.headers.get('X-XSRF-TOKEN')).toBe('retention-xsrf');
    // The route requires no `Idempotency-Key` (a naturally idempotent PUT in
    // contracts/openapi.yaml); the shared interceptor stamps one on every untyped mutation, which the
    // API ignores. Retention goes through that same pipeline as every other write.
    expect(put.request.headers.get('Idempotency-Key')).toMatch(UUID_V4);
    expect(h.store.saving()).toBe(true);
    expect(await h.store.save(STORED)).toBe(false);
    flush(put, { artifact_days: 30, metadata_days: 90 });
    expect(await saving).toBe(true);
    await h.settle();
    expect(h.store.saved()).toBe(true);
    expect(h.store.policy()).toEqual({ artifact_days: 30, metadata_days: 90 });
    h.http.verify();
  });

  it('uses the PUT answer rather than optimistic request values', async () => {
    const h = mountRetention();
    await h.load();
    const saving = h.store.save({ artifact_days: 30, metadata_days: 100 });
    flush(await h.take('PUT'), { artifact_days: 30, metadata_days: 99 });
    await saving;
    await h.settle();
    expect(h.store.policy()?.metadata_days).toBe(99);
    expect(await (await (await h.harness()).metadata()).getProperty('value')).toBe('99');
  });

  it('refetches for a selected project and ignores a write answer for the previous scope', async () => {
    const h = mountRetention();
    await h.load();
    const saving = h.store.save({ artifact_days: 30, metadata_days: 90 });
    const put = await h.take('PUT');
    h.projects.set([{ id: 'p-2', name: 'Second project', org_id: 'o-1' }]);
    flush(await h.take('GET', 'p-2'), { artifact_days: 10, metadata_days: 50 });
    flush(put, { artifact_days: 30, metadata_days: 90 });
    expect(await saving).toBe(false);
    await h.settle();
    expect(h.store.policy()).toEqual({ artifact_days: 10, metadata_days: 50 });
    expect(h.store.saved()).toBe(false);
  });

  it('drops values on read-permission revocation and does not reuse the previous policy', async () => {
    const h = mountRetention();
    await h.load();
    h.permissions.set(new Set());
    await h.settle();
    expect(h.store.policy()).toBeNull();
    expect(await h.store.save(STORED)).toBe(false);
    h.http.expectNone((request) => request.method === 'PUT');
  });

  it('retains server refusal details without changing the loaded policy', async () => {
    const h = mountRetention();
    await h.load();
    const saving = h.store.save({ artifact_days: 150, metadata_days: 200 });
    flush(await h.take('PUT'), refusal(422), 422);
    expect(await saving).toBe(false);
    expect(h.store.writeError()?.details[1]?.reason).toBe('120');
    expect(h.store.policy()).toEqual(STORED);
    expect(h.store.saving()).toBe(false);
  });

  it('exposes a failed read and explicitly reloads after a network error', async () => {
    const h = mountRetention();
    (await h.take('GET')).error(new ProgressEvent('error'));
    await h.settle();
    expect(h.store.readError()?.kind).toBe('network');
    expect(h.root.querySelector('[data-retention-unavailable]')).not.toBeNull();
    h.store.reload();
    flush(await h.take('GET'));
    await h.settle();
    expect(h.store.policy()).toEqual(STORED);
  });

  it('allows an explicit retry after a transport-failed PUT and keeps the draft', async () => {
    const h = mountRetention();
    await h.load();
    const saving = h.store.save({ artifact_days: 30, metadata_days: 90 });
    (await h.take('PUT')).error(new ProgressEvent('error'));
    expect(await saving).toBe(false);
    expect(h.store.writeError()?.kind).toBe('network');
    const retry = h.store.save({ artifact_days: 30, metadata_days: 90 });
    flush(await h.take('PUT'), { artifact_days: 30, metadata_days: 90 });
    expect(await retry).toBe(true);
  });

  it('rethrows an unexpected write failure and stops reporting the attempt as saving', async () => {
    const h = mountRetention();
    await h.load();
    const defect = new Error('retention writer is not implemented');
    vi.spyOn(h.store.data, 'save').mockRejectedValueOnce(defect);
    await expect(h.store.save({ artifact_days: 30, metadata_days: 90 })).rejects.toBe(defect);
    expect(h.store.saving()).toBe(false);
    expect(h.store.writeFailed()).toBe(false);
    expect(h.store.policy()).toEqual(STORED);
    h.http.expectNone((request) => request.method === 'PUT');
  });

  it('uses the existing quota relay for a 429 quota refusal without retrying the write', async () => {
    const h = mountRetention();
    await h.load();
    const notify = vi.fn();
    const unregister = TestBed.inject(QuotaExceededRelay).register(notify);
    const saving = h.store.save({ artifact_days: 30, metadata_days: 90 });
    flush(
      await h.take('PUT'),
      {
        error: {
          code: 'quota_exceeded',
          message: 'Quota exceeded',
          request_id: 'request-retention',
          retryable: false,
        },
      },
      429,
    );
    expect(await saving).toBe(false);
    expect(notify).toHaveBeenCalledOnce();
    expect(h.store.writeError()?.kind).toBe('rate_limited');
    h.http.expectNone((request) => request.method === 'PUT');
    unregister();
  });
});
