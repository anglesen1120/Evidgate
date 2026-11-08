import { TestBed } from '@angular/core/testing';
import { convertToParamMap } from '@angular/router';
import { createIntent } from '../../../core/http/intent';
import {
  control,
  fillCreate,
  openCreate,
  submitCreate,
  writeFailure,
} from './api-token-dialog.test-support';
import { CREATED, deferred, mount, project, ROW } from './api-tokens.test-support';

describe('API token expiry and scope boundaries', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  it.each(['not-a-date', '2026-11-06T11:59', '2026-11-06T12:00'])(
    'refuses expiry %s without a request and accepts a future expiry',
    async (value) => {
      vi.useFakeTimers({ toFake: ['Date'], now: new Date('2026-11-06T12:00:00Z') });
      const h = mount();
      await h.settle();
      await openCreate(h);
      await fillCreate(h);
      const expiry = control<HTMLInputElement>('#api-create-expiry');
      expiry.value = value;
      expiry.dispatchEvent(new Event('input', { bubbles: true }));
      await h.settle();
      expect(control<HTMLButtonElement>('[data-api-create-submit]').disabled).toBe(true);
      submitCreate();
      await h.settle();
      expect(h.create).not.toHaveBeenCalled();
      expiry.value = '2026-11-06T12:01';
      expiry.dispatchEvent(new Event('input', { bubbles: true }));
      await h.settle();
      expect(control<HTMLButtonElement>('[data-api-create-submit]').disabled).toBe(false);
      submitCreate();
      await h.settle();
      expect(h.create).toHaveBeenCalledTimes(1);
    },
  );

  it('refuses an expiry above the server maximum and accepts the exact ceiling', async () => {
    vi.useFakeTimers({ toFake: ['Date'], now: new Date('2026-11-06T12:00:00Z') });
    const h = mount();
    await h.settle();
    await openCreate(h);
    await fillCreate(h);
    h.create.mockRejectedValueOnce(
      writeFailure({
        message: 'Expiry refused (maximum 2026-11-07T12:00:00Z).',
        details: [{ path: 'expires_at', reason: 'out_of_range' }],
      }),
    );
    submitCreate();
    await h.settle();
    const expiry = control<HTMLInputElement>('#api-create-expiry');
    expect(expiry.max).toBe('2026-11-07T12:00');
    expiry.value = '2026-11-07T12:01';
    expiry.dispatchEvent(new Event('input', { bubbles: true }));
    await h.settle();
    expect(control<HTMLButtonElement>('[data-api-create-submit]').disabled).toBe(true);
    submitCreate();
    await h.settle();
    expect(h.create).toHaveBeenCalledTimes(1);
    expiry.value = '2026-11-07T12:00';
    expiry.dispatchEvent(new Event('input', { bubbles: true }));
    await h.settle();
    expect(control<HTMLButtonElement>('[data-api-create-submit]').disabled).toBe(false);
    submitCreate();
    await h.settle();
    expect(h.create).toHaveBeenCalledTimes(2);
    expect(h.create).toHaveBeenLastCalledWith(
      'project-1',
      expect.objectContaining({ expires_at: '2026-11-07T12:00:00.000Z' }),
      expect.anything(),
    );
  });

  it('refreshes a pending create after an A to B to A round trip with URL readback', async () => {
    const h = mount(undefined, {
      projects: [project('project-1'), project('project-2', 'Other')],
      query: { project: 'project-1' },
    });
    h.navigate.mockImplementation(async (_commands, extras) => {
      await Promise.resolve();
      h.query.next(convertToParamMap(extras?.queryParams ?? {}));
      return true;
    });
    await h.settle();
    const pending = deferred<typeof CREATED>();
    h.create.mockReturnValueOnce(pending.promise);
    const request = h.store.create(
      { name: 'Reader', expires_at: '2027-01-01T00:00:00Z', permissions: ['run:read'] },
      createIntent(),
    );
    h.store.selectProject('project-2');
    await h.settle();
    expect(h.query.value.get('project')).toBe('project-2');
    h.store.selectProject('project-1');
    await h.settle();
    expect(h.query.value.get('project')).toBe('project-1');
    const reads = h.list.mock.calls.length;
    h.list.mockResolvedValue({ items: [{ ...ROW, id: 'created-row' }], next_cursor: null });
    pending.resolve(CREATED);
    await expect(request).resolves.toEqual(CREATED);
    await h.settle();
    expect(h.list).toHaveBeenCalledTimes(reads + 1);
    expect(h.store.rows().map((row) => row.id)).toEqual(['created-row']);
  });

  it('clears a deep-linked cursor on revoke conflict without losing the server refusal', async () => {
    const h = mount(undefined, { query: { project: 'project-1', cursor: 'page-2' } });
    h.navigate.mockImplementation(async (_commands, extras) => {
      await Promise.resolve();
      h.query.next(convertToParamMap(extras?.queryParams ?? {}));
      return true;
    });
    await h.settle();
    expect(h.list).toHaveBeenLastCalledWith('project-1', 'page-2', expect.any(AbortSignal));
    h.revoke.mockRejectedValueOnce(
      writeFailure({
        kind: 'conflict',
        status: 409,
        code: 'already_revoked',
        message: 'This token is already revoked.',
      }),
    );
    h.list.mockResolvedValue({ items: [{ ...ROW, status: 'revoked' }], next_cursor: null });
    await expect(h.store.revoke(ROW.id, createIntent())).resolves.toBe(false);
    await h.settle();
    expect(h.store.cursor()).toBeUndefined();
    expect(h.query.value.get('cursor')).toBeNull();
    expect(h.list).toHaveBeenLastCalledWith('project-1', undefined, expect.any(AbortSignal));
    expect(h.store.rows()[0]?.status).toBe('revoked');
    expect(h.store.revokeError()?.message).toBe('This token is already revoked.');
  });
});
