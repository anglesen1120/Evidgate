import { isSignal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { getState } from '@ngrx/signals';
import { ApiRequestError } from '../../../core/http/api-error';
import { createIntent } from '../../../core/http/intent';
import { CREATED, mount, ROW, deferred } from './api-tokens.test-support';

const BODY = { name: 'Reader', permissions: ['run:read'], expires_at: '2027-01-01T00:00:00Z' };

describe('API token mutations without credential state', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
    vi.restoreAllMocks();
  });

  it('returns the create answer directly without retaining its credential in any signal', async () => {
    const h = mount();
    await h.settle();
    const intent = createIntent();
    await expect(h.store.create(BODY, intent)).resolves.toEqual(CREATED);
    expect(h.create).toHaveBeenCalledWith('project-1', BODY, intent);
    expect(JSON.stringify(getState(h.store))).not.toContain(CREATED.token);
    for (const [name, value] of Object.entries(h.store)) {
      expect(name).not.toMatch(/^(token|secret)$/i);
      // A signal holding `undefined` serialises to `undefined`, which has no `toContain`.
      if (isSignal(value)) expect(JSON.stringify(value() ?? null)).not.toContain(CREATED.token);
    }
    await h.settle();
    expect(h.list).toHaveBeenCalledTimes(2);
  });

  it('ignores a repeated create and a simultaneous revoke while one mutation is pending', async () => {
    const h = mount();
    await h.settle();
    const pending = deferred<typeof CREATED>();
    h.create.mockReturnValueOnce(pending.promise);
    const first = h.store.create(BODY, createIntent());
    await expect(h.store.create(BODY, createIntent())).resolves.toBeNull();
    await expect(h.store.revoke(ROW.id, createIntent())).resolves.toBe(false);
    expect(h.create).toHaveBeenCalledTimes(1);
    expect(h.revoke).not.toHaveBeenCalled();
    pending.resolve(CREATED);
    await first;
  });

  it('refuses both write operations for a read-only member', async () => {
    const h = mount();
    h.permissions.set(new Set(['api_token:read']));
    await h.settle();
    await expect(h.store.create(BODY, createIntent())).resolves.toBeNull();
    await expect(h.store.revoke(ROW.id, createIntent())).resolves.toBe(false);
    expect(h.create).not.toHaveBeenCalled();
    expect(h.revoke).not.toHaveBeenCalled();
  });

  it('returns a late create answer after project or write access changes so the dialog can reveal it', async () => {
    const h = mount();
    await h.settle();
    const pending = deferred<typeof CREATED>();
    h.create.mockReturnValueOnce(pending.promise);
    const request = h.store.create(BODY, createIntent());
    h.permissions.set(new Set(['api_token:read']));
    pending.resolve(CREATED);
    await expect(request).resolves.toEqual(CREATED);
    expect(h.store.creating()).toBe(false);
  });

  it('reads the first page again after a create so a new row is visible', async () => {
    const h = mount();
    h.list.mockResolvedValueOnce({ items: [ROW], next_cursor: 'next-page' });
    await h.settle();
    h.store.next();
    h.list.mockResolvedValueOnce({ items: [{ ...ROW, id: 'row-2' }], next_cursor: null });
    await h.settle();
    expect(h.list).toHaveBeenLastCalledWith('project-1', 'next-page', expect.any(AbortSignal));
    h.list.mockResolvedValueOnce({
      items: [{ ...ROW, id: 'created-row' }, ROW],
      next_cursor: null,
    });
    await expect(h.store.create(BODY, createIntent())).resolves.toEqual(CREATED);
    await h.settle();
    expect(h.store.cursor()).toBeUndefined();
    expect(h.list).toHaveBeenLastCalledWith('project-1', undefined, expect.any(AbortSignal));
    expect(h.store.rows().map((row) => row.id)).toEqual(['created-row', 'row-1']);
  });

  it('leaves the stale cursor behind on revoke so a successful revoke never shows a read error', async () => {
    const h = mount();
    const stale = new ApiRequestError({
      kind: 'validation',
      status: 422,
      code: 'invalid_cursor',
      message: 'The page cursor is not valid.',
      requestId: 'cursor-race',
      retryable: false,
      retryAfterSeconds: null,
      details: [{ path: 'cursor', reason: 'invalid' }],
    });
    h.list.mockImplementation((_project, cursor) =>
      cursor
        ? Promise.reject(stale)
        : Promise.resolve({ items: [{ ...ROW, status: 'revoked' }], next_cursor: null }),
    );
    h.list.mockResolvedValueOnce({ items: [ROW], next_cursor: 'next-page' });
    await h.settle();
    h.store.next();
    await h.settle();
    expect(h.store.phase()).toBe('error');
    await expect(h.store.revoke(ROW.id, createIntent())).resolves.toBe(true);
    await h.settle();
    expect(h.list).toHaveBeenLastCalledWith('project-1', undefined, expect.any(AbortSignal));
    expect(h.store.phase()).toBe('success');
  });

  it('never optimistically revokes a row, then refreshes authoritative metadata on success', async () => {
    const h = mount();
    await h.settle();
    const pending = deferred<void>();
    h.revoke.mockReturnValueOnce(pending.promise);
    const request = h.store.revoke(ROW.id, createIntent());
    await expect(h.store.revoke(ROW.id, createIntent())).resolves.toBe(false);
    expect(h.revoke).toHaveBeenCalledTimes(1);
    expect(h.store.rows()[0]?.status).toBe('active');
    h.list.mockResolvedValue({ items: [{ ...ROW, status: 'revoked' }], next_cursor: null });
    pending.resolve();
    await expect(request).resolves.toBe(true);
    await h.settle();
    expect(h.store.rows()[0]?.status).toBe('revoked');
  });

  it.each([
    ['not_found', 404],
    ['conflict', 409],
  ] as const)(
    'keeps an already-revoked %s refusal verbatim and refreshes the row',
    async (kind, status) => {
      const h = mount();
      await h.settle();
      const error = {
        kind,
        status,
        code: 'already_revoked',
        message: 'This token is already revoked.',
        requestId: 'revocation-race',
        retryable: false,
        retryAfterSeconds: null,
        details: [{ path: 'id', reason: 'already_revoked' }],
      };
      h.revoke.mockRejectedValueOnce(new ApiRequestError(error));
      h.list.mockResolvedValue({ items: [{ ...ROW, status: 'revoked' }], next_cursor: null });
      await expect(h.store.revoke(ROW.id, createIntent())).resolves.toBe(false);
      expect(h.store.revokeError()).toEqual(error);
      await h.settle();
      expect(h.store.rows()[0]?.status).toBe('revoked');
    },
  );
});
