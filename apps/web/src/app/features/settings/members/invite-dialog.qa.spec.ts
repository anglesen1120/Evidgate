// QA (09.7.1-T2 independent gate): the invite dialog driven through the REAL MembersStore, with only
// MembersDataService and SessionStore replaced. The existing page spec replaces the whole store, so
// the dialog-to-store contract (one request per press, intent reuse, payload freeze) had no test.

import { isSignal, provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import type {
  InviteIssuedView,
  InviteMemberRequest,
  MembershipView,
  RoleView,
} from '@evidgate/sdk';
import { MembersDataService } from '../../../core/data/members-data.service';
import { ApiRequestError, type ApiErrorKind } from '../../../core/http/api-error';
import type { Intent } from '../../../core/http/intent';
import { SessionStore } from '../../../core/session/session.store';
import { InviteDialogComponent } from './invite-dialog.component';
import { MembersStore } from './members.store';
import { RevealSlot } from '../../../shared/ui/reveal/one-time-reveal.slot';

const ROLE_A: RoleView = {
  id: 'role-a',
  code: 'tenant_admin',
  name: 'Organization administrator',
  is_builtin: true,
  created_at: '2026-10-25T00:00:00Z',
  version: 1,
};
const ROLE_B: RoleView = {
  id: 'role-b',
  code: 'tenant_auditor',
  name: 'Auditor',
  is_builtin: true,
  created_at: '2026-10-25T00:00:00Z',
  version: 1,
};
const CREATED: MembershipView = {
  id: 'm-new',
  org_id: 'org1',
  invited_email: 'new@example.test',
  status: 'pending',
  roles: [],
  created_at: '2026-10-25T00:00:00Z',
  updated_at: '2026-10-25T00:00:00Z',
  version: 1,
};
const ISSUED: InviteIssuedView = { ...CREATED, invite_token: 'recognisable-invite-only-once' };

function refusal(
  kind: ApiErrorKind,
  status: number,
  code: string,
  message: string,
  retryable: boolean,
): ApiRequestError {
  return new ApiRequestError({
    kind,
    status,
    code,
    message,
    requestId: 'req-qa',
    retryable,
    details: [],
    retryAfterSeconds: null,
  });
}

/** Records every invite; `outcomes` answers the next invite (default: created). */
class FakeData {
  readonly granted = signal(true);
  readonly invites: Array<{ body: InviteMemberRequest; intent: Intent }> = [];
  readonly outcomes: Array<() => Promise<InviteIssuedView>> = [];
  readonly removals: Array<{ membershipId: string; intent: Intent }> = [];
  listMemberships() {
    return Promise.resolve({ items: [], next_cursor: null });
  }
  listRoles() {
    return Promise.resolve([ROLE_A, ROLE_B]);
  }
  listPermissions() {
    return Promise.resolve([]);
  }
  invite(_orgId: string, body: InviteMemberRequest, intent: Intent): Promise<InviteIssuedView> {
    this.invites.push({ body, intent });
    const next = this.outcomes.shift();
    return next ? next() : Promise.resolve(ISSUED);
  }
  remove(_orgId: string, membershipId: string, intent: Intent): Promise<void> {
    this.removals.push({ membershipId, intent });
    return Promise.resolve();
  }
}

const flush = () => new Promise<void>((resolve) => setTimeout(resolve, 0));
const showModalDescriptor = Object.getOwnPropertyDescriptor(
  HTMLDialogElement.prototype,
  'showModal',
);
const closeDescriptor = Object.getOwnPropertyDescriptor(HTMLDialogElement.prototype, 'close');

describe('InviteDialog through the real MembersStore (09.7.1-T2 QA)', () => {
  let data: FakeData;
  let store: MembersStore;
  let fixture: ComponentFixture<InviteDialogComponent>;
  let root: HTMLElement;
  let opener: HTMLButtonElement;
  let completed: number;

  beforeEach(async () => {
    data = new FakeData();
    Object.defineProperty(HTMLDialogElement.prototype, 'showModal', {
      configurable: true,
      value: function (this: HTMLDialogElement) {
        this.open = true;
      },
    });
    Object.defineProperty(HTMLDialogElement.prototype, 'close', {
      configurable: true,
      value: function (this: HTMLDialogElement) {
        this.open = false;
      },
    });
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        MembersStore,
        { provide: MembersDataService, useValue: data },
        {
          provide: SessionStore,
          useValue: { has: (code: string) => code === 'membership:write' && data.granted() },
        },
      ],
    });
    store = TestBed.inject(MembersStore);
    await store.load('org1');
    fixture = TestBed.createComponent(InviteDialogComponent);
    fixture.detectChanges();
    root = fixture.nativeElement as HTMLElement;
    opener = document.createElement('button');
    document.body.append(opener);
    completed = 0;
    fixture.componentInstance.completed.subscribe(() => completed++);
  });

  afterEach(() => {
    TestBed.resetTestingModule();
    opener.remove();
    if (showModalDescriptor)
      Object.defineProperty(HTMLDialogElement.prototype, 'showModal', showModalDescriptor);
    else Reflect.deleteProperty(HTMLDialogElement.prototype, 'showModal');
    if (closeDescriptor)
      Object.defineProperty(HTMLDialogElement.prototype, 'close', closeDescriptor);
    else Reflect.deleteProperty(HTMLDialogElement.prototype, 'close');
    vi.restoreAllMocks();
  });

  function input(): HTMLInputElement {
    const value = root.querySelector<HTMLInputElement>('#members-invite-email');
    if (!value) throw new Error('email input missing');
    return value;
  }
  function submitButton(): HTMLButtonElement {
    const value = root.querySelector<HTMLButtonElement>('[data-members-submit]');
    if (!value) throw new Error('submit button missing');
    return value;
  }
  function open(): void {
    fixture.componentInstance.open(opener);
    fixture.detectChanges();
  }
  function setEmail(value: string): void {
    input().value = value;
    input().dispatchEvent(new Event('input'));
  }
  function toggleRole(id: string, checked: boolean): void {
    const box = root.querySelector<HTMLInputElement>(`input[type="checkbox"][value="${id}"]`);
    if (!box) throw new Error(`role checkbox ${id} missing`);
    box.checked = checked;
    box.dispatchEvent(new Event('change'));
  }

  it('a second press while the first invitation is pending sends one request and one completion', async () => {
    let release!: () => void;
    data.outcomes.push(() => new Promise((resolve) => (release = () => resolve(ISSUED))));
    open();
    setEmail('new@example.test');
    submitButton().click();
    submitButton().click();
    await flush();
    expect(data.invites).toHaveLength(1);
    release();
    await flush();
    expect(data.invites).toHaveLength(1);
    expect(completed).toBe(1);
    expect(root.querySelector('dialog')?.open).toBe(true);
  });

  it('a retry after a 503 unavailable replays the same Idempotency-Key and the same body', async () => {
    data.outcomes.push(() =>
      Promise.reject(refusal('unavailable', 503, 'unavailable', 'Try again shortly.', true)),
    );
    open();
    setEmail('new@example.test');
    toggleRole('role-a', true);
    submitButton().click();
    await flush();
    fixture.detectChanges();
    expect(root.querySelector('[data-members-error-code]')?.textContent?.trim()).toBe(
      'unavailable',
    );
    submitButton().click();
    await flush();
    expect(data.invites).toHaveLength(2);
    expect(data.invites[1]?.intent).toBe(data.invites[0]?.intent);
    expect(data.invites[1]?.body).toEqual(data.invites[0]?.body);
    expect(completed).toBe(1);
  });

  it('closing after a failure and reopening starts a new Idempotency-Key', async () => {
    data.outcomes.push(() =>
      Promise.reject(refusal('unavailable', 503, 'unavailable', 'Try again shortly.', true)),
    );
    open();
    setEmail('new@example.test');
    submitButton().click();
    await flush();
    fixture.componentInstance.close();
    open();
    setEmail('new@example.test');
    submitButton().click();
    await flush();
    expect(data.invites).toHaveLength(2);
    expect(data.invites[1]?.intent).not.toBe(data.invites[0]?.intent);
  });

  it('after a refusal the address and role choices are frozen until the dialog closes', async () => {
    data.outcomes.push(() =>
      Promise.reject(
        refusal('conflict', 409, 'invite_exists', 'An invitation is already pending.', false),
      ),
    );
    open();
    setEmail('new@example.test');
    submitButton().click();
    await flush();
    fixture.detectChanges();
    expect(input().disabled).toBe(true);
    expect(
      root.querySelector<HTMLInputElement>('input[value="role-a"]')?.matches(':disabled'),
    ).toBe(true);
  });

  it('a 409 invite_exists renders the server message as text, never as markup', async () => {
    data.outcomes.push(() =>
      Promise.reject(
        refusal(
          'conflict',
          409,
          'invite_exists',
          'Already <b>pending</b> for this address.',
          false,
        ),
      ),
    );
    open();
    setEmail('new@example.test');
    submitButton().click();
    await flush();
    fixture.detectChanges();
    const message = root.querySelector('[data-members-error] p');
    expect(message?.textContent).toBe('Already <b>pending</b> for this address.');
    expect(root.querySelector('[data-members-error] b')).toBeNull();
    expect(root.querySelector('[data-members-error-code]')?.textContent?.trim()).toBe(
      'invite_exists',
    );
    expect(completed).toBe(0);
  });

  it('a role ticked and then unticked is not sent: the body is { email } only', async () => {
    open();
    setEmail('new@example.test');
    toggleRole('role-a', true);
    toggleRole('role-a', false);
    submitButton().click();
    await flush();
    expect(data.invites).toHaveLength(1);
    expect(data.invites[0]?.body).toEqual({ email: 'new@example.test' });
    expect('roles' in (data.invites[0]?.body ?? {})).toBe(false);
  });

  it('two ticked roles are sent in the order of the checkboxes, as ids', async () => {
    open();
    setEmail('new@example.test');
    toggleRole('role-b', true);
    toggleRole('role-a', true);
    submitButton().click();
    await flush();
    expect(data.invites[0]?.body.roles).toEqual(['role-b', 'role-a']);
  });

  it.each(['not-an-address', 'a@', '@example.test', ' '])(
    'the malformed address %j never reaches the store',
    async (value) => {
      open();
      setEmail(value);
      submitButton().click();
      await flush();
      expect(data.invites).toHaveLength(0);
      expect(completed).toBe(0);
    },
  );

  it('revoking membership:write while the dialog is open blocks the submit', async () => {
    open();
    setEmail('new@example.test');
    data.granted.set(false);
    fixture.detectChanges();
    expect(submitButton().disabled).toBe(true);
    submitButton().click();
    await flush();
    expect(data.invites).toHaveLength(0);
  });

  it('the dialog does not open while a removal is in flight', async () => {
    let release!: () => void;
    const pendingRemove = new Promise<void>((resolve) => (release = resolve));
    vi.spyOn(data, 'remove').mockReturnValue(pendingRemove);
    void store.remove('m-x', { key: 'k' } as unknown as Intent);
    await flush();
    open();
    const dialog = root.querySelector('dialog');
    expect(dialog?.open).toBe(false);
    release();
    await flush();
  });

  // A success is a fact about the list, not about this dialog (invite-dialog.component.ts:186-195):
  // closing mid-flight still announces the invitation through `completed`, which is what sets the
  // page's saved notice. The stale-generation guard now only decides who closes the dialog.
  it('closing while an invitation is in flight still announces its completion', async () => {
    let release!: () => void;
    data.outcomes.push(() => new Promise((resolve) => (release = () => resolve(ISSUED))));
    open();
    setEmail('new@example.test');
    submitButton().click();
    fixture.componentInstance.close();
    release();
    await flush();
    expect(completed).toBe(1);
  });
  it.each(['dismiss', 'escape', 'destroy'] as const)(
    'reveals once, then clears the slot and DOM on %s',
    async (exit) => {
      open();
      setEmail('new@example.test');
      submitButton().click();
      await fixture.whenStable();
      fixture.detectChanges();
      const control = root.querySelector<HTMLInputElement>('[data-testid="reveal-value"]')!;
      expect(control.value).toBe(ISSUED.invite_token);
      expect(root.querySelector('form')).toBeNull();
      expect(root.querySelector('[data-testid="reveal-copy"]')?.textContent).toContain('Copy');
      const writeText = vi.fn().mockResolvedValue(undefined);
      Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText } });
      root.querySelector<HTMLButtonElement>('[data-testid="reveal-copy"]')!.click();
      await fixture.whenStable();
      expect(writeText).toHaveBeenCalledWith(ISSUED.invite_token);
      const slot = fixture.debugElement.injector.get(RevealSlot);
      if (exit === 'destroy') fixture.destroy();
      else if (exit === 'escape')
        root.querySelector('dialog')!.dispatchEvent(new Event('cancel', { cancelable: true }));
      else root.querySelector<HTMLButtonElement>('[data-testid="reveal-dismiss"]')!.click();
      if (exit !== 'destroy') fixture.detectChanges();
      expect(slot.current()).toBeNull();
      expect(control.value).toBe('');
      expect(root.outerHTML).not.toContain(ISSUED.invite_token!);
      for (const value of Object.values(store)) {
        if (typeof value === 'function' && isSignal(value))
          expect(JSON.stringify(value())).not.toContain(ISSUED.invite_token!);
      }
    },
  );

  it('an idempotency replay offers Resend guidance without mounting an empty reveal', async () => {
    data.outcomes.push(() => Promise.resolve({ ...CREATED, invite_token: null }));
    open();
    setEmail('new@example.test');
    submitButton().click();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(root.querySelector('app-connect-step-reveal')).toBeNull();
    expect(root.querySelector('[data-members-invite-replay]')?.textContent).toContain('Use Resend');
    expect(root.querySelector('form')).toBeNull();
    expect(root.querySelector('dialog')?.open).toBe(true);
  });

  it('an EMPTY served invite_token takes the same replay path, never an empty reveal', async () => {
    // F2: `''` is absence; the store normalises it, so this dialog must offer Resend guidance.
    data.outcomes.push(() => Promise.resolve({ ...CREATED, invite_token: '' }));
    open();
    setEmail('new@example.test');
    submitButton().click();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(root.querySelector('app-connect-step-reveal')).toBeNull();
    expect(root.querySelector('[data-testid="reveal-value"]')).toBeNull();
    expect(root.querySelector('[data-members-invite-replay]')?.textContent).toContain('Use Resend');
    expect(root.querySelector('dialog')?.open).toBe(true);
  });

  it('a 422 validation refusal preserves the exact message and code', async () => {
    data.outcomes.push(() =>
      Promise.reject(refusal('validation', 422, 'invalid_email', 'Use a valid <email>.', false)),
    );
    open();
    setEmail('new@example.test');
    submitButton().click();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(root.querySelector('[data-members-error] p')?.textContent).toBe('Use a valid <email>.');
    expect(root.querySelector('[data-members-error-code]')?.textContent).toBe('invalid_email');
    expect(root.querySelector('app-connect-step-reveal')).toBeNull();
  });
});
