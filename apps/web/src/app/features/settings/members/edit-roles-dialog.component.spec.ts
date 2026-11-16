import { InteractivityChecker } from '@angular/cdk/a11y';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import type { MembershipView, RoleView } from '@evidgate/sdk';
import type { ApiError } from '../../../core/http/api-error';
import { SessionStore } from '../../../core/session/session.store';
import { EditRolesDialogComponent } from './edit-roles-dialog.component';
import { MembersStore, type MutationState, type RoleChange } from './members.store';
import { MEMBERS_DESKTOP } from './members-page.test-fixtures';

const admin: RoleView = {
  id: 'admin',
  code: 'tenant_admin',
  name: 'Organization administrator',
  is_builtin: true,
  created_at: '2026-10-25T00:00:00Z',
  version: 1,
};
const viewer: RoleView = { ...admin, id: 'viewer', code: 'viewer', name: 'Viewer' };
const auditor: RoleView = { ...admin, id: 'auditor', code: 'auditor', name: 'Auditor' };
const membership: MembershipView = {
  id: 'member1',
  org_id: 'org1',
  invited_email: 'member@example.test',
  status: 'active',
  roles: [admin],
  created_at: '2026-10-25T00:00:00Z',
  updated_at: '2026-10-25T00:00:00Z',
  version: 1,
};
const lastAdmin: ApiError = {
  kind: 'conflict',
  status: 409,
  code: 'last_admin',
  message: 'Server refusal: <strong>keep the final administrator</strong>.',
  requestId: 'request1',
  retryable: false,
  retryAfterSeconds: null,
  details: [],
};
type Applied = Pick<RoleChange, 'kind' | 'roleId'>;

class FakeMembersStore {
  readonly roles = signal<readonly RoleView[]>([admin, viewer, auditor]);
  readonly members = signal<readonly MembershipView[]>([membership]);
  readonly editState = signal<MutationState>({ phase: 'idle', error: null });
  readonly editApplied = signal<readonly Applied[]>([]);
  readonly inviteState = signal<MutationState>({ phase: 'idle', error: null });
  readonly removeState = signal<MutationState>({ phase: 'idle', error: null });
  readonly memberById = (id: string) => this.members().find((value) => value.id === id);
  readonly changeRoles = vi.fn<(id: string, changes: readonly RoleChange[]) => Promise<boolean>>(
    () => Promise.resolve(true),
  );
  resetMutations(): void {
    this.editState.set({ phase: 'idle', error: null });
    this.editApplied.set([]);
  }
}

describe('EditRolesDialogComponent (09.7.1 F6)', () => {
  let fixture: ComponentFixture<EditRolesDialogComponent>;
  let store: FakeMembersStore;
  let root: HTMLElement;
  let opener: HTMLButtonElement;
  const canWrite = signal(true);

  beforeEach(() => {
    canWrite.set(true);
    store = new FakeMembersStore();
    TestBed.configureTestingModule({
      imports: [EditRolesDialogComponent],
      providers: [
        provideZonelessChangeDetection(),
        MEMBERS_DESKTOP,
        { provide: MembersStore, useValue: store },
        { provide: SessionStore, useValue: { has: () => canWrite() } },
      ],
    });
    vi.spyOn(TestBed.inject(InteractivityChecker), 'isVisible').mockReturnValue(true);
    fixture = TestBed.createComponent(EditRolesDialogComponent);
    root = document.body;
    opener = document.createElement('button');
    document.body.append(opener);
    fixture.detectChanges();
  });
  afterEach(() => {
    TestBed.resetTestingModule();
    opener.remove();
    vi.restoreAllMocks();
  });

  async function open(): Promise<HTMLElement> {
    fixture.componentInstance.open(membership, opener);
    fixture.detectChanges();
    await fixture.whenStable();
    const content = root.querySelector<HTMLElement>('[data-members-dialog]');
    if (!content) throw new Error('Expected role dialog');
    return content;
  }
  function option(id: string): HTMLButtonElement {
    return root.querySelector<HTMLButtonElement>(
      `[data-members-role-option][data-role-id="${id}"] button[role="checkbox"]`,
    )!;
  }
  function select(id: string, checked: boolean): void {
    const input = option(id);
    if ((input.getAttribute('aria-checked') === 'true') !== checked) input.click();
    fixture.detectChanges();
  }
  function save(): HTMLButtonElement {
    return root.querySelector<HTMLButtonElement>('[data-members-submit]')!;
  }
  function cancelControl(): HTMLButtonElement {
    return root.querySelector<HTMLButtonElement>('footer button:not([data-members-submit])')!;
  }
  async function submit(): Promise<void> {
    save().click();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  it('initializes checkboxes from current server roles and enables Save only for a diff', async () => {
    store.members.set([{ ...membership, roles: [viewer] }]);
    await open();
    expect(option(admin.id).getAttribute('aria-checked') === 'true').toBe(false);
    expect(option(viewer.id).getAttribute('aria-checked') === 'true').toBe(true);
    expect(save().disabled).toBe(true);
    select(admin.id, true);
    expect(save().disabled).toBe(false);
    select(admin.id, false);
    expect(save().disabled).toBe(true);
    expect(root.querySelector('fieldset legend')?.textContent?.trim()).not.toBe('');
    expect(option(admin.id).closest('label')?.textContent).toContain(admin.name);
  });

  it('sends grants before revokes with distinct intents and announces confirmed success', async () => {
    const completed = vi.fn();
    fixture.componentInstance.completed.subscribe(completed);
    const dialog = await open();
    select(admin.id, false);
    select(viewer.id, true);
    await submit();
    const [id, changes] = store.changeRoles.mock.calls[0]!;
    expect(id).toBe(membership.id);
    expect(changes.map(({ kind, roleId }) => ({ kind, roleId }))).toEqual([
      { kind: 'grant', roleId: viewer.id },
      { kind: 'revoke', roleId: admin.id },
    ]);
    expect(changes[0]!.intent.idempotencyKey).not.toBe(changes[1]!.intent.idempotencyKey);
    expect(dialog.isConnected && dialog.getAttribute('data-state') === 'open').toBe(false);
    expect(completed).toHaveBeenCalledOnce();
    expect(document.activeElement).toBe(opener);
  });

  it.each([
    lastAdmin,
    {
      ...lastAdmin,
      status: 403,
      kind: 'forbidden',
      code: 'forbidden',
      message: 'Server: self-escalation <em>refused</em>.',
    },
    { ...lastAdmin, code: 'unknown_role', message: 'Server: unknown role.' },
    {
      ...lastAdmin,
      status: 404,
      kind: 'not_found',
      code: 'not_found',
      message: 'Server: role pair never granted.',
    },
  ] satisfies readonly ApiError[])('renders the $code refusal verbatim as text', async (error) => {
    store.changeRoles.mockImplementation(() => {
      store.editState.set({ phase: 'failed', error });
      return Promise.resolve(false);
    });
    const dialog = await open();
    select(admin.id, false);
    await submit();
    expect(dialog.isConnected && dialog.getAttribute('data-state') === 'open').toBe(true);
    expect(root.querySelector('[data-members-error] p')?.textContent).toBe(error.message);
    expect(root.querySelector('[data-members-error-code]')?.textContent).toBe(error.code);
    expect(root.querySelector('[data-members-error] strong, [data-members-error] em')).toBeNull();
    expect(root.querySelector('[data-members-last-admin-hint]') !== null).toBe(
      error.code === 'last_admin',
    );
    expect(root.querySelector('[data-members-applied-summary]')).toBeNull();
    expect(store.members()[0]!.roles).toEqual([admin]);
  });

  it('reuses the same operation intent after a refusal and clears it when closed', async () => {
    store.changeRoles.mockImplementation(() => {
      store.editState.set({ phase: 'failed', error: lastAdmin });
      return Promise.resolve(false);
    });
    const dialog = await open();
    select(admin.id, false);
    await submit();
    await submit();
    const first = store.changeRoles.mock.calls[0]![1][0]!.intent;
    expect(store.changeRoles.mock.calls[1]![1][0]!.intent).toBe(first);
    dialog.dispatchEvent(
      new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }),
    );
    fixture.detectChanges();
    await fixture.whenStable();
    await open();
    select(admin.id, false);
    await submit();
    expect(store.changeRoles.mock.calls[2]![1][0]!.intent.idempotencyKey).not.toBe(
      first.idempotencyKey,
    );
  });

  it('shows a confirmed grant and refused revoke separately, rebases the draft and never replays the confirmed key', async () => {
    store.changeRoles.mockImplementationOnce((_id, changes) => {
      store.editApplied.set([{ kind: 'grant', roleId: viewer.id }]);
      // The refetched server row also contains an independent concurrent role change.
      store.members.set([{ ...membership, roles: [admin, viewer, auditor] }]);
      store.editState.set({ phase: 'failed', error: lastAdmin });
      expect(changes).toHaveLength(2);
      return Promise.resolve(false);
    });
    const dialog = await open();
    select(viewer.id, true);
    select(admin.id, false);
    await submit();
    expect(dialog.isConnected && dialog.getAttribute('data-state') === 'open').toBe(true);
    expect(root.querySelector('[data-members-applied-summary]')).not.toBeNull();
    expect(root.querySelectorAll('[data-members-applied-item]')).toHaveLength(1);
    expect(root.querySelector('[data-members-applied-item]')?.textContent).toContain(viewer.name);
    expect(root.querySelectorAll('[data-members-not-applied-item]')).toHaveLength(1);
    expect(root.querySelector('[data-members-not-applied-item]')?.textContent).toContain(
      admin.name,
    );
    expect(root.querySelector('[data-members-error] p')?.textContent).toBe(lastAdmin.message);
    expect(root.querySelector('[data-members-error-code]')?.textContent).toBe('last_admin');
    expect(root.querySelector('[data-members-last-admin-hint]')).not.toBeNull();
    expect(option(viewer.id).getAttribute('aria-checked') === 'true').toBe(true);
    expect(option(auditor.id).getAttribute('aria-checked') === 'true').toBe(true);
    expect(option(admin.id).getAttribute('aria-checked') === 'true').toBe(false);
    await submit();
    const first = store.changeRoles.mock.calls[0]![1];
    const retry = store.changeRoles.mock.calls[1]![1];
    expect(retry).toHaveLength(1);
    expect(retry[0]!.kind).toBe('revoke');
    expect(retry[0]!.intent).toBe(first[1]!.intent);
    expect(retry.some((change) => change.intent === first[0]!.intent)).toBe(false);
  });

  it('includes every unattempted change after the refusal and falls back to a role id in the summary', async () => {
    store.changeRoles.mockImplementationOnce(() => {
      store.editApplied.set([{ kind: 'grant', roleId: viewer.id }]);
      store.members.set([{ ...membership, roles: [admin, viewer] }]);
      store.roles.set([admin, auditor]);
      store.editState.set({ phase: 'failed', error: lastAdmin });
      return Promise.resolve(false);
    });
    await open();
    select(viewer.id, true);
    select(auditor.id, true);
    select(admin.id, false);
    await submit();
    expect(root.querySelector('[data-members-applied-item]')?.textContent).toContain(viewer.id);
    expect(root.querySelectorAll('[data-members-not-applied-item]')).toHaveLength(2);
  });

  it('traps focus, initially focuses the first checkbox, and returns focus on Escape', async () => {
    const dialog = await open();
    expect(document.activeElement).toBe(option(admin.id));
    expect(root.querySelectorAll('.cdk-focus-trap-anchor')).toHaveLength(2);
    expect(dialog.closest('[role="dialog"]')?.getAttribute('aria-modal')).toBe('true');
    const cancel = new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true });
    dialog.dispatchEvent(cancel);
    fixture.detectChanges();
    expect(cancel.defaultPrevented).toBe(true);
    expect(dialog.isConnected && dialog.getAttribute('data-state') === 'open').toBe(false);
    expect(document.activeElement).toBe(opener);
  });

  it('disables saving and focuses Cancel when no roles are available', async () => {
    store.roles.set([]);
    fixture.detectChanges();
    await open();
    expect(root.querySelector('[data-members-role-option]')).toBeNull();
    expect(save().disabled).toBe(true);
    expect(document.activeElement).toBe(root.querySelector('footer button'));
    save().click();
    expect(store.changeRoles).not.toHaveBeenCalled();
  });

  it('keeps the list unchanged while submitting and disables draft changes and duplicate saves', async () => {
    // This project targets ES2022, before Promise.withResolvers.
    let resolve!: (success: boolean) => void;
    const promise = new Promise<boolean>((complete) => {
      resolve = complete;
    });
    store.changeRoles.mockImplementation(() => {
      store.editState.set({ phase: 'submitting', error: null });
      return promise;
    });
    const dialog = await open();
    select(viewer.id, true);
    save().click();
    fixture.detectChanges();
    expect(dialog.getAttribute('aria-busy')).toBe('true');
    expect(root.querySelector('fieldset')!.disabled).toBe(true);
    expect(save().disabled).toBe(true);
    expect(store.members()[0]!.roles).toEqual([admin]);
    save().click();
    expect(store.changeRoles).toHaveBeenCalledTimes(1);
    store.members.set([{ ...membership, roles: [admin, viewer] }]);
    store.editState.set({ phase: 'done', error: null });
    resolve(true);
    await fixture.whenStable();
    fixture.detectChanges();
    expect(dialog.isConnected && dialog.getAttribute('data-state') === 'open').toBe(false);
    expect(store.members()[0]!.roles).toEqual([admin, viewer]);
  });

  it('stays busy through the store’s refresh, so a second Save sends nothing and one outcome lands', async () => {
    // This project targets ES2022, before Promise.withResolvers.
    let resolve!: (success: boolean) => void;
    const attempt = new Promise<boolean>((complete) => {
      resolve = complete;
    });
    const completed = vi.fn();
    fixture.componentInstance.completed.subscribe(completed);
    store.changeRoles.mockImplementation(() => {
      // `submitting` covers the store's own refresh read, so busy() outlives the last request.
      store.editState.set({ phase: 'submitting', error: null });
      return attempt;
    });
    const dialog = await open();
    select(viewer.id, true);
    save().click();
    fixture.detectChanges();
    expect(store.changeRoles).toHaveBeenCalledTimes(1);

    // The store has not replaced the row yet, so `diff()` still reads the old one: a second click
    // here would re-send the confirmed grant under the Idempotency-Key the server already answered.
    expect(dialog.getAttribute('aria-busy')).toBe('true');
    save().click();
    fixture.detectChanges();
    expect(store.changeRoles).toHaveBeenCalledTimes(1);
    expect(save().disabled).toBe(true);

    store.members.set([{ ...membership, roles: [admin, viewer] }]);
    store.editState.set({ phase: 'done', error: null });
    resolve(true);
    await fixture.whenStable();
    fixture.detectChanges();

    expect(store.changeRoles).toHaveBeenCalledTimes(1);
    expect(completed).toHaveBeenCalledOnce();
    expect(dialog.isConnected && dialog.getAttribute('data-state') === 'open').toBe(false);
  });

  it('ignores Escape and Cancel while the save is in flight, and Escape closes once it settles', async () => {
    let resolve!: (success: boolean) => void;
    const attempt = new Promise<boolean>((complete) => {
      resolve = complete;
    });
    store.changeRoles.mockImplementation(() => {
      store.editState.set({ phase: 'submitting', error: null });
      return attempt;
    });
    const dialog = await open();
    select(viewer.id, true);
    save().click();
    fixture.detectChanges();

    // Cancel is inert and Escape is refused, so the attempt finishes inside its own dialog and no
    // second attempt can be opened over the row it is about to replace.
    expect(cancelControl().disabled).toBe(true);
    dialog.dispatchEvent(
      new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }),
    );
    fixture.detectChanges();
    expect(dialog.isConnected && dialog.getAttribute('data-state') === 'open').toBe(true);
    expect(store.editState().phase).toBe('submitting');
    expect(store.changeRoles).toHaveBeenCalledTimes(1);

    // A refusal settles the attempt: the dialog stays open on the server's error, and closing it is
    // possible again.
    store.editState.set({ phase: 'failed', error: lastAdmin });
    resolve(false);
    await fixture.whenStable();
    fixture.detectChanges();
    expect(dialog.isConnected && dialog.getAttribute('data-state') === 'open').toBe(true);
    expect(cancelControl().disabled).toBe(false);

    dialog.dispatchEvent(
      new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }),
    );
    fixture.detectChanges();
    expect(dialog.isConnected && dialog.getAttribute('data-state') === 'open').toBe(false);
    expect(document.activeElement).toBe(opener);
  });

  it('still closes when the page closes it while the save is in flight, and sends nothing more', async () => {
    let resolve!: (success: boolean) => void;
    const attempt = new Promise<boolean>((complete) => {
      resolve = complete;
    });
    store.changeRoles.mockImplementation(() => {
      store.editState.set({ phase: 'submitting', error: null });
      return attempt;
    });
    const dialog = await open();
    select(viewer.id, true);
    save().click();
    fixture.detectChanges();

    // An organization switch closes the dialog itself: showing the old org's member over the newly
    // loaded list is worse than dropping the attempt's view.
    fixture.componentInstance.close();
    fixture.detectChanges();
    expect(dialog.isConnected && dialog.getAttribute('data-state') === 'open').toBe(false);
    expect(document.activeElement).toBe(opener);

    // The abandoned attempt settles into nothing: no further request, no reopened dialog.
    store.editState.set({ phase: 'failed', error: lastAdmin });
    resolve(false);
    await fixture.whenStable();
    fixture.detectChanges();
    expect(store.changeRoles).toHaveBeenCalledTimes(1);
    expect(dialog.isConnected && dialog.getAttribute('data-state') === 'open').toBe(false);
  });

  it('moves focus to the first enabled role checkbox when a refusal re-enables the form', async () => {
    let resolve!: (success: boolean) => void;
    const attempt = new Promise<boolean>((complete) => {
      resolve = complete;
    });
    store.changeRoles.mockImplementation(() => {
      store.editState.set({ phase: 'submitting', error: null });
      return attempt;
    });
    const dialog = await open();
    select(admin.id, false);
    save().focus();
    save().click();
    fixture.detectChanges();
    // Save was the focused control and it is disabled for the request: focus is gone from it.
    expect(save().disabled).toBe(true);

    store.editState.set({ phase: 'failed', error: lastAdmin });
    resolve(false);
    await fixture.whenStable();
    fixture.detectChanges();

    expect(dialog.isConnected && dialog.getAttribute('data-state') === 'open').toBe(true);
    expect(document.activeElement).toBe(option(admin.id));
  });

  it.each(['rate_limited', 'unavailable'] as const)(
    'honours Retry-After for %s without changing the mapped error',
    async (kind) => {
      const error: ApiError = { ...lastAdmin, kind, retryable: true, retryAfterSeconds: 60 };
      store.changeRoles.mockImplementation(() => {
        store.editState.set({ phase: 'failed', error });
        return Promise.resolve(false);
      });
      await open();
      select(viewer.id, true);
      await submit();
      expect(root.querySelector('app-retry-after')).not.toBeNull();
      expect(save().disabled).toBe(true);
      expect(root.querySelector('[data-members-error] p')?.textContent).toBe(error.message);
      save().click();
      expect(store.changeRoles).toHaveBeenCalledTimes(1);
    },
  );

  it('refuses to open without write permission and disables Save if write permission is lost', async () => {
    canWrite.set(false);
    fixture.componentInstance.open(membership, opener);
    fixture.detectChanges();
    await fixture.whenStable();
    expect(root.querySelector('[data-members-dialog]')).toBeNull();
    canWrite.set(true);
    await open();
    select(viewer.id, true);
    canWrite.set(false);
    fixture.detectChanges();
    expect(save().disabled).toBe(true);
    save().click();
    expect(store.changeRoles).not.toHaveBeenCalled();
  });
});
