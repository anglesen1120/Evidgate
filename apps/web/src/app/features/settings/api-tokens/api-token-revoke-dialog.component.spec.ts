import { TestBed } from '@angular/core/testing';
import { control, writeFailure } from './api-token-dialog.test-support';
import { mount, ROW, deferred } from './api-tokens.test-support';

describe('API token revocation confirmation', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
    vi.restoreAllMocks();
  });

  it('opens with metadata, cancels on Escape, and restores the row action focus without a request', async () => {
    const h = mount();
    await h.settle();
    const opener = h.root.querySelector<HTMLButtonElement>('[data-api-revoke]')!;
    opener.focus();
    opener.click();
    await h.settle();
    const dialog = control('[data-api-revoke-dialog]');
    expect(dialog.textContent).toContain(ROW.name);
    expect(dialog.textContent).toContain(ROW.prefix);
    dialog.dispatchEvent(
      new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }),
    );
    await h.settle();
    expect(document.querySelector('[data-api-revoke-dialog]')).toBeNull();
    expect(h.revoke).not.toHaveBeenCalled();
    expect(document.activeElement).toBe(opener);
  });

  it('confirms once on double click, freezes cancel while pending, and displays only refreshed row state', async () => {
    const h = mount();
    await h.settle();
    h.root.querySelector<HTMLButtonElement>('[data-api-revoke]')!.click();
    await h.settle();
    const pending = deferred<void>();
    h.revoke.mockReturnValueOnce(pending.promise);
    const confirm = control<HTMLButtonElement>('[data-api-revoke-submit]');
    confirm.click();
    confirm.click();
    h.fixture.detectChanges();
    expect(h.revoke).toHaveBeenCalledTimes(1);
    expect(h.revoke).toHaveBeenCalledWith(
      ROW.id,
      expect.objectContaining({ idempotencyKey: expect.any(String) as string }),
    );
    expect(control<HTMLButtonElement>('[data-api-revoke-cancel]').disabled).toBe(true);
    expect(h.store.rows()[0]?.status).toBe('active');
    h.list.mockResolvedValue({ items: [{ ...ROW, status: 'revoked' }], next_cursor: null });
    pending.resolve();
    await h.settle();
    expect(document.querySelector('[data-api-revoke-dialog]')).toBeNull();
    expect(h.root.querySelector('[data-api-token-row]')?.getAttribute('data-state')).toBe(
      'revoked',
    );
    expect(h.root.querySelector('[data-api-revoke]')).toBeNull();
  });

  it.each([
    ['not_found', 404],
    ['conflict', 409],
  ] as const)(
    'keeps an already-revoked %s response visible without an optimistic success',
    async (kind, status) => {
      const h = mount();
      await h.settle();
      h.root.querySelector<HTMLButtonElement>('[data-api-revoke]')!.click();
      await h.settle();
      h.revoke.mockRejectedValueOnce(
        writeFailure({
          kind,
          status,
          code: 'already_revoked',
          message: 'The token was already revoked.',
          details: [{ path: 'id', reason: 'revoked' }],
        }),
      );
      control<HTMLButtonElement>('[data-api-revoke-submit]').click();
      await h.settle();
      const text = control('[data-api-revoke-dialog]').textContent;
      expect(text).toContain('The token was already revoked.');
      expect(text).toContain('already_revoked');
      expect(text).toContain('write-request');
      expect(control<HTMLButtonElement>('[data-api-revoke-submit]').disabled).toBe(true);
      expect(h.root.textContent).not.toContain('Token revoked.');
    },
  );

  it('reuses the same revoke intent after a retryable response', async () => {
    const h = mount();
    await h.settle();
    h.root.querySelector<HTMLButtonElement>('[data-api-revoke]')!.click();
    await h.settle();
    h.revoke.mockRejectedValueOnce(
      writeFailure({ kind: 'unavailable', status: 503, retryable: true }),
    );
    control<HTMLButtonElement>('[data-api-revoke-submit]').click();
    await h.settle();
    control<HTMLButtonElement>('[data-api-revoke-submit]').click();
    await h.settle();
    expect(h.revoke).toHaveBeenCalledTimes(2);
    expect(h.revoke.mock.calls[1]).toEqual(h.revoke.mock.calls[0]);
  });

  it('removes create and revoke controls for a read-only member', async () => {
    const h = mount();
    h.permissions.set(new Set(['api_token:read']));
    await h.settle();
    expect(h.root.querySelector('[data-api-create]')).toBeNull();
    expect(h.root.querySelector('[data-api-revoke]')).toBeNull();
    h.store.openCreate();
    await h.settle();
    expect(document.querySelector('[data-api-create-dialog]')).toBeNull();
  });
});
