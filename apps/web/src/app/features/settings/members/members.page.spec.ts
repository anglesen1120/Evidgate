import { InteractivityChecker } from '@angular/cdk/a11y';
import { provideZonelessChangeDetection, signal, type WritableSignal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute } from '@angular/router';
import { SessionStore } from '../../../core/session/session.store';
import { MembersPage } from './members.page';
import { MembersStore } from './members.store';
import { FakeMembersStore, lastAdmin, role } from './members-page.test-fixtures';
import { routes } from './members.routes';

describe('Members page (09.7.1-T2)', () => {
  let store: FakeMembersStore;
  let granted: WritableSignal<boolean>;
  beforeEach(() => {
    store = new FakeMembersStore();
    granted = signal(true);
    TestBed.configureTestingModule({
      imports: [MembersPage],
      providers: [
        provideZonelessChangeDetection(),
        { provide: ActivatedRoute, useValue: { snapshot: { data: {} } } },
        {
          provide: SessionStore,
          useValue: {
            currentOrg: signal({ id: 'org1' }),
            has: (code: string) => code === 'membership:write' && granted(),
          },
        },
      ],
    });
    TestBed.overrideComponent(MembersPage, {
      set: { providers: [{ provide: MembersStore, useValue: store }] },
    });
  });
  afterEach(() => {
    TestBed.resetTestingModule();
    vi.restoreAllMocks();
  });
  async function render() {
    vi.spyOn(TestBed.inject(InteractivityChecker), 'isVisible').mockReturnValue(true);
    const fixture = TestBed.createComponent(MembersPage);
    fixture.detectChanges();
    await fixture.whenStable();
    return { fixture, root: document.body };
  }
  function button(root: ParentNode, selector: string): HTMLButtonElement {
    const value = root.querySelector<HTMLButtonElement>(selector);
    if (!value) throw new Error(`Missing button: ${selector}`);
    return value;
  }

  it('marks the standalone members route as the page-heading owner', () => {
    expect(
      routes.find((route) => route.component === MembersPage)?.data?.['pageHeadingLevel'],
    ).toBe(1);
  });

  it.each([1, 2])('uses heading level %s supplied by its route', async (level) => {
    TestBed.overrideProvider(ActivatedRoute, {
      useValue: { snapshot: { data: { pageHeadingLevel: level } } },
    });
    const { root } = await render();
    const heading = root.querySelector(`header h${level}`);
    expect(heading?.textContent?.trim()).toBe('Members and roles');
    expect(root.querySelector(`header h${level === 1 ? 2 : 1}`)).toBeNull();
  });
  it('renders ready members, pending status, role names and a semantic org-wide matrix', async () => {
    const { root } = await render();
    expect(root.querySelectorAll('[data-members-row]')).toHaveLength(3);
    expect(root.textContent).toContain('owner@example.test');
    expect(root.textContent).toContain('Pending invitation');
    expect(root.textContent).toContain(role.name);
    const matrix = root.querySelector('table[data-members-matrix]');
    expect(matrix?.querySelector('caption')?.textContent?.trim()).toBe(
      'Organization-wide role matrix',
    );
    expect(matrix?.querySelectorAll('th[scope="col"]')).toHaveLength(2);
    expect(matrix?.querySelector('[data-granted="true"]')?.textContent?.trim()).toBe('Granted');
    expect(root.querySelector('[data-members-status="removed"] [data-members-remove]')).toBeNull();
  });

  it('omits Invite, Edit and Remove from the DOM without membership:write; renders them with it', async () => {
    granted.set(false);
    const { fixture, root } = await render();
    expect(root.querySelector('[data-members-invite-open]')).toBeNull();
    expect(root.querySelector('[data-members-remove]')).toBeNull();
    expect(root.querySelector('[data-members-edit-open]')).toBeNull();
    expect(root.querySelector('app-edit-roles-dialog')).toBeNull();
    expect(root.querySelector<HTMLElement>('[data-members-dialog]')).toBeNull();
    granted.set(true);
    fixture.detectChanges();
    expect(root.querySelector('[data-members-invite-open]')).not.toBeNull();
    expect(root.querySelectorAll('[data-members-remove]')).toHaveLength(2);
    expect(root.querySelectorAll('[data-members-edit-open]')).toHaveLength(2);
    expect(root.querySelector('app-edit-roles-dialog')).not.toBeNull();
  });

  it('opens row role editing, announces success and closes editing when the organization switches', async () => {
    const { fixture, root } = await render();
    const opener = button(root, '[data-members-edit-open]');
    opener.click();
    fixture.detectChanges();
    await fixture.whenStable();
    const dialog = root.querySelector<HTMLElement>('[data-members-dialog="edit-roles"]')!;
    expect(dialog.isConnected && dialog.getAttribute('data-state') === 'open').toBe(true);
    const checkbox = dialog.querySelector<HTMLButtonElement>(
      '[data-members-role-option] button[role="checkbox"]',
    )!;
    checkbox.click();
    fixture.detectChanges();
    button(dialog, '[data-members-submit]').click();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(store.changeRoles).toHaveBeenCalledTimes(1);
    expect(dialog.isConnected && dialog.getAttribute('data-state') === 'open').toBe(false);
    expect(document.activeElement).toBe(opener);
    expect(root.textContent).toContain('Membership updated.');
    opener.click();
    fixture.detectChanges();
    await fixture.whenStable();
    expect(
      root.querySelector('[data-members-dialog="edit-roles"][data-state="open"]'),
    ).not.toBeNull();
    const session = TestBed.inject(SessionStore);
    (session.currentOrg as unknown as WritableSignal<{ id: string }>).set({ id: 'org2' });
    fixture.detectChanges();
    expect(dialog.isConnected && dialog.getAttribute('data-state') === 'open').toBe(false);
    expect(root.querySelector('[data-members-dialog][data-state="open"]')).toBeNull();
    expect(store.load).toHaveBeenLastCalledWith('org2');
  });

  it('closes the role dialog on an organization switch even while its save is in flight', async () => {
    let resolve!: (success: boolean) => void;
    const attempt = new Promise<boolean>((complete) => {
      resolve = complete;
    });
    store.changeRoles.mockImplementation(() => {
      store.editState.set({ phase: 'submitting', error: null });
      return attempt;
    });
    const { fixture, root } = await render();
    button(root, '[data-members-edit-open]').click();
    fixture.detectChanges();
    await fixture.whenStable();
    const dialog = root.querySelector<HTMLElement>('[data-members-dialog="edit-roles"]')!;
    const checkbox = dialog.querySelector<HTMLButtonElement>(
      '[data-members-role-option] button[role="checkbox"]',
    )!;
    checkbox.click();
    fixture.detectChanges();
    button(dialog, '[data-members-submit]').click();
    fixture.detectChanges();
    expect(dialog.isConnected && dialog.getAttribute('data-state') === 'open').toBe(true);

    // The scope switch wins over the in-flight attempt: the old org's member must not stay on screen
    // over the new org's list.
    const session = TestBed.inject(SessionStore);
    (session.currentOrg as unknown as WritableSignal<{ id: string }>).set({ id: 'org2' });
    fixture.detectChanges();
    expect(dialog.isConnected && dialog.getAttribute('data-state') === 'open').toBe(false);
    expect(root.querySelector('[data-members-dialog][data-state="open"]')).toBeNull();
    expect(store.load).toHaveBeenLastCalledWith('org2');

    // The abandoned attempt settles into nothing: one request for the old org, no reopened dialog.
    store.editState.set({ phase: 'failed', error: lastAdmin });
    resolve(false);
    await fixture.whenStable();
    fixture.detectChanges();
    expect(store.changeRoles).toHaveBeenCalledTimes(1);
    expect(dialog.isConnected && dialog.getAttribute('data-state') === 'open').toBe(false);
  });

  it('keeps role chips and the matrix on server data until the role mutation is confirmed', async () => {
    // This project targets ES2022, before Promise.withResolvers.
    let resolve!: (success: boolean) => void;
    const promise = new Promise<boolean>((complete) => {
      resolve = complete;
    });
    store.changeRoles.mockImplementation(() => {
      store.editState.set({ phase: 'submitting', error: null });
      return promise;
    });
    const { fixture, root } = await render();
    button(root, '[data-members-edit-open]').click();
    fixture.detectChanges();
    await fixture.whenStable();
    const dialog = root.querySelector<HTMLElement>('[data-members-dialog="edit-roles"]')!;
    const checkbox = dialog.querySelector<HTMLButtonElement>(
      '[data-members-role-option] button[role="checkbox"]',
    )!;
    checkbox.click();
    fixture.detectChanges();
    button(dialog, '[data-members-submit]').click();
    fixture.detectChanges();
    const row = root.querySelector('[data-membership-id="owner"]')!;
    expect(row.querySelector('.role-name')?.textContent?.trim()).toBe(role.name);
    expect(root.querySelector('[data-members-matrix] [data-granted="true"]')).not.toBeNull();
    store.members.update((members) =>
      members.map((value) => (value.id === 'owner' ? { ...value, roles: [] } : value)),
    );
    store.editState.set({ phase: 'done', error: null });
    resolve(true);
    await fixture.whenStable();
    fixture.detectChanges();
    expect(row.querySelector('.role-name')).toBeNull();
    expect(dialog.isConnected && dialog.getAttribute('data-state') === 'open').toBe(false);
  });

  it('renders the shared empty state rather than a matrix with invented rows', async () => {
    store.members.set([]);
    const { root } = await render();
    expect(root.querySelector('app-empty-pattern')).not.toBeNull();
    expect(root.textContent).toContain('No members yet');
    expect(root.querySelector('[data-members-row]')).toBeNull();
    expect(root.querySelector('[data-members-matrix]')).toBeNull();
  });

  it('shows the shared loading, forbidden, not-found and retry-after request patterns', async () => {
    store.phase.set('loading');
    const { fixture, root } = await render();
    expect(root.querySelector('app-loading-pattern')).not.toBeNull();
    for (const kind of ['forbidden', 'not_found', 'rate_limited', 'unavailable'] as const) {
      store.phase.set('error');
      store.loadError.set({ ...lastAdmin, kind, retryable: true, retryAfterSeconds: 2 });
      fixture.detectChanges();
      const selector =
        kind === 'forbidden'
          ? 'app-forbidden-pattern'
          : kind === 'not_found'
            ? 'app-not-found-pattern'
            : 'app-retry-after';
      expect(root.querySelector(selector)).not.toBeNull();
    }
  });

  it('announces a refused next page above the pagination and clears it when a page arrives', async () => {
    store.loadError.set(lastAdmin);
    store.hasMore.set(true);
    const { fixture, root } = await render();
    const alert = root.querySelector('[data-members-load-error]');
    expect(alert?.getAttribute('role')).toBe('alert');
    expect(alert?.querySelector('p')?.textContent?.trim()).toBe(lastAdmin.message);
    expect(alert?.querySelector('code')?.textContent?.trim()).toBe('last_admin');
    expect(root.querySelector('.members-content > [data-members-load-error]')).toBe(alert);
    // Next is the retry control: the same button asks the server again.
    const nextButton = button(root, '.pagination button:last-of-type');
    expect(nextButton.disabled).toBe(false);

    nextButton.click();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(store.loadMore).toHaveBeenCalledTimes(1);
    expect(root.querySelector('[data-members-load-error]')).toBeNull();
  });

  it('turns a contract violation while paging into a generic alert, never the thrown text', async () => {
    store.hasMore.set(true);
    store.loadMore.mockImplementation(() =>
      Promise.reject(new Error('the membership list envelope was malformed')),
    );
    const { fixture, root } = await render();
    const nextButton = button(root, '.pagination button:last-of-type');
    nextButton.focus();
    nextButton.click();
    await fixture.whenStable();
    fixture.detectChanges();

    const alert = root.querySelector('[data-members-load-error]');
    expect(alert?.getAttribute('role')).toBe('alert');
    expect(alert?.textContent).toContain('The next members page could not be loaded.');
    expect(alert?.querySelector('code')).toBeNull();
    expect(root.textContent).not.toContain('envelope was malformed');
    expect(button(root, '.pagination button:last-of-type').disabled).toBe(false);
    // Focus stays on the retry control beside the alert, not on a heading whose rows did not move.
    expect(document.activeElement).toBe(nextButton);
  });

  it('keeps the remove dialog open and renders last_admin verbatim as text with its code (AC2)', async () => {
    store.remove.mockImplementation(() => {
      store.removeState.set({ phase: 'failed', error: lastAdmin });
      return Promise.resolve(false);
    });
    const { fixture, root } = await render();
    button(root, '[data-members-remove]').click();
    fixture.detectChanges();
    await fixture.whenStable();
    const dialog = root.querySelector<HTMLElement>('[data-members-dialog="remove"]')!;
    button(dialog, '[data-members-submit]').click();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(dialog.isConnected && dialog.getAttribute('data-state') === 'open').toBe(true);
    expect(dialog.querySelector('[data-members-error] p')?.textContent).toBe(lastAdmin.message);
    expect(dialog.querySelector('[data-members-error-code]')?.textContent).toBe('last_admin');
    expect(dialog.querySelector('[data-members-error] strong')).toBeNull();
    expect(store.remove).toHaveBeenCalledTimes(1);
  });

  it('returns focus to the invoking row button on Escape; success focuses the list heading', async () => {
    const { fixture, root } = await render();
    const opener = button(root, '[data-members-remove]');
    opener.focus();
    opener.click();
    fixture.detectChanges();
    await fixture.whenStable();
    const dialog = root.querySelector<HTMLElement>('[data-members-dialog="remove"]')!;
    dialog.dispatchEvent(
      new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }),
    );
    fixture.detectChanges();
    expect(dialog.isConnected && dialog.getAttribute('data-state') === 'open').toBe(false);
    expect(document.activeElement).toBe(opener);
    opener.click();
    fixture.detectChanges();
    await fixture.whenStable();
    button(root, '[data-members-dialog="remove"] [data-members-submit]').click();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(dialog.isConnected && dialog.getAttribute('data-state') === 'open').toBe(false);
    expect(document.activeElement).toBe(root.querySelector('.page-header h2'));
  });

  it('previews focused roles, marks sensitive permissions and reports unavailable instead of guessing', async () => {
    const { fixture, root } = await render();
    button(root, '[data-members-invite-open]').click();
    fixture.detectChanges();
    await fixture.whenStable();
    expect(root.querySelector('[data-role-preview]')?.getAttribute('data-preview-kind')).toBe(
      'unavailable',
    );
    root
      .querySelector<HTMLButtonElement>('hlm-checkbox button[role="checkbox"]')!
      .dispatchEvent(new FocusEvent('focusin', { bubbles: true }));
    fixture.detectChanges();
    const preview = root.querySelector('[data-role-preview]');
    expect(preview?.getAttribute('data-preview-kind')).toBe('ready');
    expect(preview?.textContent).toContain('membership:read');
    expect(preview?.textContent).toContain('Sensitive');
    expect(preview?.textContent).toContain('The server decides actual access.');
    expect(preview?.textContent).toContain('live catalogue: 1');
  });

  it('validates email and reuses an invitation intent on retry; a closed dialog starts a new intent', async () => {
    store.invite.mockImplementation(() => {
      store.inviteState.set({ phase: 'failed', error: { ...lastAdmin, code: 'invite_exists' } });
      return Promise.resolve(null);
    });
    const { fixture, root } = await render();
    const opener = button(root, '[data-members-invite-open]');
    opener.click();
    fixture.detectChanges();
    await fixture.whenStable();
    const dialog = root.querySelector<HTMLElement>('[data-members-dialog="invite"]')!;
    const form = dialog.querySelector('form')!;
    form.dispatchEvent(new Event('submit', { cancelable: true }));
    expect(store.invite).not.toHaveBeenCalled();
    const email = dialog.querySelector<HTMLInputElement>('input[type="email"]')!;
    expect(email.maxLength).toBe(320);
    email.value = 'invite@example.test';
    email.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    const selectedRole = dialog.querySelector<HTMLButtonElement>(
      'hlm-checkbox button[role="checkbox"]',
    )!;
    selectedRole.click();
    fixture.detectChanges();
    form.dispatchEvent(new Event('submit', { cancelable: true }));
    await fixture.whenStable();
    fixture.detectChanges();
    expect(dialog.querySelector('[data-members-error-code]')?.textContent).toBe('invite_exists');
    expect(email.getAttribute('aria-describedby')).toBe('members-invite-error');
    expect(dialog.querySelector('#members-invite-error')?.getAttribute('role')).toBe('alert');
    expect(store.invite.mock.calls[0]?.slice(0, 2)).toEqual(['invite@example.test', [role.id]]);
    form.dispatchEvent(new Event('submit', { cancelable: true }));
    await fixture.whenStable();
    fixture.detectChanges();
    expect(store.invite.mock.calls[0]?.[2]).toBe(store.invite.mock.calls[1]?.[2]);
    dialog.dispatchEvent(
      new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }),
    );
    fixture.detectChanges();
    expect(document.activeElement).toBe(opener);
    opener.click();
    fixture.detectChanges();
    await fixture.whenStable();
    const nextEmail = root.querySelector<HTMLInputElement>('#members-invite-email');
    if (!nextEmail) throw new Error('Expected reopened invitation email');
    nextEmail.value = 'next@example.test';
    nextEmail.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    button(root, '[data-members-dialog="invite"] [data-members-submit]').click();
    await fixture.whenStable();
    expect(store.invite.mock.calls[2]?.[2]).not.toBe(store.invite.mock.calls[0]?.[2]);
  });
  it('reveals a successful invitation, then dismisses and restores the invoking button', async () => {
    const { fixture, root } = await render();
    const opener = button(root, '[data-members-invite-open]');
    opener.click();
    fixture.detectChanges();
    await fixture.whenStable();
    const dialog = root.querySelector<HTMLElement>('[data-members-dialog="invite"]')!;
    const email = dialog.querySelector<HTMLInputElement>('input[type="email"]')!;
    email.value = 'new@example.test';
    email.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    dialog.querySelector('form')!.dispatchEvent(new Event('submit', { cancelable: true }));
    await fixture.whenStable();
    fixture.detectChanges();
    expect(store.invite).toHaveBeenCalledTimes(1);
    expect(dialog.isConnected && dialog.getAttribute('data-state') === 'open').toBe(true);
    expect(dialog.querySelector<HTMLInputElement>('[data-testid="reveal-value"]')?.value).toBe(
      'page-create-once',
    );
    button(dialog, '[data-testid="reveal-dismiss"]').click();
    fixture.detectChanges();
    expect(dialog.isConnected && dialog.getAttribute('data-state') === 'open').toBe(false);
    expect(document.activeElement).toBe(opener);
    expect(root.textContent).toContain('Membership updated.');
  });

  it('honours Retry-After in a failed mutation while preserving the exact server error', async () => {
    const { fixture, root } = await render();
    button(root, '[data-members-remove]').click();
    fixture.detectChanges();
    store.removeState.set({
      phase: 'failed',
      error: { ...lastAdmin, kind: 'rate_limited', retryable: true, retryAfterSeconds: 60 },
    });
    fixture.detectChanges();
    await fixture.whenStable();
    const dialog = root.querySelector<HTMLElement>('[data-members-dialog="remove"]')!;
    expect(dialog.querySelector('app-retry-after')).not.toBeNull();
    expect(button(dialog, '[data-members-submit]').disabled).toBe(true);
    expect(dialog.querySelector('[data-members-error] p')?.textContent).toBe(lastAdmin.message);
    button(dialog, '[data-members-submit]').click();
    expect(store.remove).not.toHaveBeenCalled();
  });
  it.each([
    ['invite', 'org'],
    ['invite', 'another-dialog'],
    ['invite', 'destroy'],
    ['resend', 'org'],
    ['resend', 'another-dialog'],
    ['resend', 'destroy'],
  ] as const)('clears an open %s reveal on %s', async (source, exit) => {
    const { fixture, root } = await render();
    button(
      root,
      source === 'invite' ? '[data-members-invite-open]' : '[data-members-resend-open]',
    ).click();
    fixture.detectChanges();
    await fixture.whenStable();
    const dialog = root.querySelector<HTMLElement>(`[data-members-dialog="${source}"]`)!;
    if (source === 'invite') {
      const email = dialog.querySelector<HTMLInputElement>('input[type="email"]')!;
      email.value = 'new@example.test';
      email.dispatchEvent(new Event('input'));
    }
    button(dialog, '[data-members-submit]').click();
    await fixture.whenStable();
    fixture.detectChanges();
    const control = dialog.querySelector<HTMLInputElement>('[data-testid="reveal-value"]')!;
    const once = source === 'invite' ? 'page-create-once' : 'page-resend-once';
    expect(control.value).toBe(once);
    if (exit === 'org') {
      const session = TestBed.inject(SessionStore);
      (session.currentOrg as unknown as WritableSignal<{ id: string }>).set({ id: 'org2' });
    } else if (exit === 'another-dialog')
      button(
        root,
        source === 'invite' ? '[data-members-resend-open]' : '[data-members-invite-open]',
      ).click();
    else fixture.destroy();
    if (exit !== 'destroy') fixture.detectChanges();
    expect(control.value).toBe('');
    expect(root.outerHTML).not.toContain(once);
    expect(dialog.isConnected && dialog.getAttribute('data-state') === 'open').toBe(false);
  });

  it('wires pending Cancel invite to its own confirmation and closes on success', async () => {
    const { fixture, root } = await render();
    button(root, '[data-members-cancel-invite-open]').click();
    fixture.detectChanges();
    await fixture.whenStable();
    const dialog = root.querySelector<HTMLElement>('[data-members-dialog="cancel-invite"]')!;
    expect(dialog.isConnected && dialog.getAttribute('data-state') === 'open').toBe(true);
    button(dialog, '[data-members-submit]').click();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(store.cancel).toHaveBeenCalledWith('pending', expect.anything());
    expect(dialog.isConnected && dialog.getAttribute('data-state') === 'open').toBe(false);
  });
});
