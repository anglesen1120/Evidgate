import { TestBed } from '@angular/core/testing';
import { expiryInstant } from './api-token-create-dialog.component';
import {
  control,
  fillCreate,
  openCreate,
  submitCreate,
  writeFailure,
} from './api-token-dialog.test-support';
import { CREATED, mount, deferred, project } from './api-tokens.test-support';

describe('API token creation dialog', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
    vi.restoreAllMocks();
  });

  it('requires name, expiry and a permission; submits UTC and reveals only the create answer', async () => {
    const h = mount();
    await h.settle();
    await openCreate(h);
    expect(control<HTMLButtonElement>('[data-api-create-submit]').disabled).toBe(true);
    control<HTMLInputElement>('#api-create-name').value = 'Reader';
    control('#api-create-name').dispatchEvent(new Event('input', { bubbles: true }));
    await h.settle();
    expect(control<HTMLButtonElement>('[data-api-create-submit]').disabled).toBe(true);
    await fillCreate(h);
    submitCreate();
    await h.settle();
    expect(h.create).toHaveBeenCalledWith(
      'project-1',
      {
        name: 'Reader',
        permissions: ['run:read'],
        expires_at: new Date('2027-01-01T00:00Z').toISOString(),
      },
      expect.objectContaining({ idempotencyKey: expect.any(String) as string }),
    );
    expect(control<HTMLInputElement>('[data-testid="reveal-value"]').value).toBe(CREATED.token);
    // The portaled dialog is outside the page root, so the text leak is checked on the whole body.
    expect(document.body.textContent).not.toContain(CREATED.token);
  });

  it('offers only caller-held permissions after applying the canonical administrative deny list', async () => {
    const h = mount();
    h.permissions.set(
      new Set([
        'api_token:read',
        'api_token:write',
        'run:read',
        'membership:write',
        'billing:read',
        'project:write',
      ]),
    );
    await h.settle();
    await openCreate(h);
    const offered = Array.from(
      document.querySelectorAll('hlm-checkbox button[role="checkbox"]'),
    ).map((button) => button.id);
    expect(offered).toEqual(['api-permission-run:read']);
  });

  it.each(['required', 'out_of_range'])(
    'renders server expiry %s details without inventing a maximum',
    async (reason) => {
      const h = mount();
      await h.settle();
      await openCreate(h);
      await fillCreate(h);
      h.create.mockRejectedValueOnce(
        writeFailure({
          message: 'Expiry must be within the configured 17-day lifetime.',
          details: [{ path: 'expires_at', reason }],
        }),
      );
      submitCreate();
      await h.settle();
      expect(control('[data-api-create-dialog]').textContent).toContain('17-day lifetime');
      expect(control('[data-api-create-dialog]').textContent).toContain(reason);
      expect(control<HTMLInputElement>('#api-create-expiry').hasAttribute('max')).toBe(false);
      expect(document.querySelector('[data-testid="reveal-value"]')).toBeNull();
    },
  );

  it.each([
    ['forbidden', 403, 'permission_escalation'],
    ['validation', 422, 'forbidden_token_permission'],
  ] as const)(
    'keeps %s permission refusal and server details visible',
    async (kind, status, code) => {
      const h = mount();
      await h.settle();
      await openCreate(h);
      await fillCreate(h);
      h.create.mockRejectedValueOnce(
        writeFailure({
          kind,
          status,
          code,
          message: 'Cannot grant this permission.',
          details: [{ path: 'permissions[0]', reason: 'not_granted' }],
        }),
      );
      submitCreate();
      await h.settle();
      const text = control('[data-api-create-dialog]').textContent;
      expect(text).toContain('These permissions cannot be granted');
      expect(text).toContain(code);
      expect(text).toContain('permissions[0]');
      expect(text).toContain('write-request');
      expect(document.querySelector('[data-testid="reveal-value"]')).toBeNull();
    },
  );

  it('submits once on double press and retries a retryable refusal with the same intent and body', async () => {
    const h = mount();
    await h.settle();
    await openCreate(h);
    await fillCreate(h);
    const pending = deferred<typeof CREATED>();
    h.create.mockReturnValueOnce(pending.promise);
    submitCreate();
    submitCreate();
    expect(h.create).toHaveBeenCalledTimes(1);
    pending.reject(writeFailure({ kind: 'unavailable', status: 503, retryable: true }));
    await h.settle();
    expect(control<HTMLInputElement>('#api-create-name').disabled).toBe(true);
    submitCreate();
    await h.settle();
    expect(h.create).toHaveBeenCalledTimes(2);
    expect(h.create.mock.calls[1]).toEqual(h.create.mock.calls[0]);
  });

  it('does not replay an ambiguous transport outcome with a new intent', async () => {
    const h = mount();
    await h.settle();
    await openCreate(h);
    await fillCreate(h);
    h.create.mockRejectedValueOnce(writeFailure({ kind: 'network', status: 0, retryable: true }));
    submitCreate();
    await h.settle();
    expect(control('[data-api-create-dialog]').textContent).toContain('Check the token list');
    expect(control<HTMLButtonElement>('[data-api-create-submit]').disabled).toBe(true);
    submitCreate();
    expect(h.create).toHaveBeenCalledTimes(1);
  });

  it('re-reads the token list from its first page before closing on an ambiguous create', async () => {
    const h = mount();
    await h.settle();
    await openCreate(h);
    await fillCreate(h);
    h.create.mockRejectedValueOnce(writeFailure({ kind: 'timeout', status: 0, retryable: true }));
    submitCreate();
    await h.settle();
    expect(h.list).toHaveBeenCalledTimes(1);
    h.list.mockResolvedValue({ items: [], next_cursor: null });
    const inspect = Array.from(document.querySelectorAll('button')).find((button) =>
      button.textContent?.includes('Close and check token list'),
    );
    if (!inspect) throw new Error('Missing inspect action');
    inspect.click();
    await h.settle();
    // The read starts from the click handler, so the close cannot outrun a committed token.
    expect(h.list).toHaveBeenCalledTimes(2);
    expect(h.list).toHaveBeenLastCalledWith('project-1', undefined, expect.any(AbortSignal));
    expect(h.store.createOpen()).toBe(false);
  });

  it('keeps submit disabled for a missing or past expiry and never submits it', async () => {
    const h = mount();
    await h.settle();
    await openCreate(h);
    const name = control<HTMLInputElement>('#api-create-name');
    name.value = 'Reader';
    name.dispatchEvent(new Event('input', { bubbles: true }));
    control<HTMLButtonElement>('button[id="api-permission-run:read"]').click();
    await h.settle();
    expect(control<HTMLButtonElement>('[data-api-create-submit]').disabled).toBe(true);
    const expiry = control<HTMLInputElement>('#api-create-expiry');
    expiry.value = '2020-01-01T00:00';
    expiry.dispatchEvent(new Event('input', { bubbles: true }));
    await h.settle();
    expect(control<HTMLButtonElement>('[data-api-create-submit]').disabled).toBe(true);
    expect(() => submitCreate()).not.toThrow();
    await h.settle();
    expect(h.create).not.toHaveBeenCalled();
    expect(expiry.getAttribute('min')).toBeTruthy();
    expect(expiry.hasAttribute('max')).toBe(false);
  });

  it('reads an impossible expiry as absent instead of throwing', () => {
    expect(expiryInstant('')).toBeNull();
    expect(expiryInstant('2027-13-01T00:00')).toBeNull();
    expect(expiryInstant('2027-01-01T00:00')).toBe(new Date('2027-01-01T00:00Z').getTime());
  });

  it('keeps the refusal message when the selection moved while the create was pending', async () => {
    const h = mount();
    await h.settle();
    await openCreate(h);
    await fillCreate(h);
    const pending = deferred<typeof CREATED>();
    h.create.mockReturnValueOnce(pending.promise);
    submitCreate();
    h.projects.set([project('project-2', 'Other')]);
    pending.reject(
      writeFailure({
        message: 'Expiry must be in the future.',
        details: [{ path: 'expires_at', reason: 'past' }],
      }),
    );
    await h.settle();
    expect(control('[data-api-create-dialog]').textContent).toContain(
      'Expiry must be in the future.',
    );
    expect(control<HTMLButtonElement>('[data-api-create-submit]').disabled).toBe(true);
  });

  it('does not resubmit the expiry a 422 refused until a field changes', async () => {
    const h = mount();
    await h.settle();
    await openCreate(h);
    await fillCreate(h);
    h.create.mockRejectedValueOnce(
      writeFailure({
        message: 'Expiry must be in the future.',
        details: [{ path: 'expires_at', reason: 'past' }],
      }),
    );
    submitCreate();
    await h.settle();
    expect(control('[data-api-create-dialog]').textContent).toContain(
      'Expiry must be in the future.',
    );
    expect(control<HTMLButtonElement>('[data-api-create-submit]').disabled).toBe(true);
    submitCreate();
    await h.settle();
    expect(h.create).toHaveBeenCalledTimes(1);
    const name = control<HTMLInputElement>('#api-create-name');
    name.value = 'Renamed reader';
    name.dispatchEvent(new Event('input', { bubbles: true }));
    await h.settle();
    expect(control<HTMLButtonElement>('[data-api-create-submit]').disabled).toBe(false);
    submitCreate();
    await h.settle();
    expect(h.create).toHaveBeenCalledTimes(2);
  });
});
