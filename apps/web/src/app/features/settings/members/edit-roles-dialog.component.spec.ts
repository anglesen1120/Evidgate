import { CdkTrapFocus } from '@angular/cdk/a11y';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import type { MembershipView, RoleView } from '@evidgate/sdk';
import type { ApiError } from '../../../core/http/api-error';
import { SessionStore } from '../../../core/session/session.store';
import { EditRolesDialogComponent } from './edit-roles-dialog.component';
import { MembersStore, type MutationState, type RoleChange } from './members.store';

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

const showModalDescriptor = Object.getOwnPropertyDescriptor(
  HTMLDialogElement.prototype,
  'showModal',
);
const closeDescriptor = Object.getOwnPropertyDescriptor(HTMLDialogElement.prototype, 'close');

describe('EditRolesDialogComponent (09.7.1 F6)', () => {
  let fixture: ComponentFixture<EditRolesDialogComponent>;
  let store: FakeMembersStore;
  let root: HTMLElement;
  let opener: HTMLButtonElement;
  const canWrite = signal(true);

  beforeEach(() => {
    canWrite.set(true);
    store = new FakeMembersStore();
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
      imports: [EditRolesDialogComponent],
      providers: [
        provideZonelessChangeDetection(),
        { provide: MembersStore, useValue: store },
        { provide: SessionStore, useValue: { has: () => canWrite() } },
      ],
    });
    fixture = TestBed.createComponent(EditRolesDialogComponent);
    root = fixture.nativeElement as HTMLElement;
    opener = document.createElement('button');
    document.body.append(opener);
    fixture.detectChanges();
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

  function open(): HTMLDialogElement {
    fixture.componentInstance.open(membership, opener);
    fixture.detectChanges();
    return root.querySelector<HTMLDialogElement>('dialog')!;
  }
  function option(id: string): HTMLInputElement {
    return root.querySelector<HTMLInputElement>(
      `[data-members-role-option][data-role-id="${id}"]`,
    )!;
  }
  function select(id: string, checked: boolean): void {
    const input = option(id);
    input.checked = checked;
    input.dispatchEvent(new Event('change'));
    fixture.detectChanges();
  }
  function save(): HTMLButtonElement {
    return root.querySelector<HTMLButtonElement>('[data-members-submit]')!;
  }
  async function submit(): Promise<void> {
    save().click();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  it('initializes checkboxes from current server roles and enables Save only for a diff', () => {
    store.members.set([{ ...membership, roles: [viewer] }]);
    open();
    expect(option(admin.id).checked).toBe(false);
    expect(option(viewer.id).checked).toBe(true);
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
    const dialog = open();
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
    expect(dialog.open).toBe(false);
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
    const dialog = open();
    select(admin.id, false);
    await submit();
    expect(dialog.open).toBe(true);
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
    const dialog = open();
    select(admin.id, false);
    await submit();
    await submit();
    const first = store.changeRoles.mock.calls[0]![1][0]!.intent;
    expect(store.changeRoles.mock.calls[1]![1][0]!.intent).toBe(first);
    dialog.dispatchEvent(new Event('cancel', { cancelable: true }));
    open();
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
    const dialog = open();
    select(viewer.id, true);
    select(admin.id, false);
    await submit();
    expect(dialog.open).toBe(true);
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
    expect(option(viewer.id).checked).toBe(true);
    expect(option(auditor.id).checked).toBe(true);
    expect(option(admin.id).checked).toBe(false);
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
    open();
    select(viewer.id, true);
    select(auditor.id, true);
    select(admin.id, false);
    await submit();
    expect(root.querySelector('[data-members-applied-item]')?.textContent).toContain(viewer.id);
    expect(root.querySelectorAll('[data-members-not-applied-item]')).toHaveLength(2);
  });

  it('traps focus, initially focuses the first checkbox, and returns focus on Escape', () => {
    const dialog = open();
    expect(document.activeElement).toBe(option(admin.id));
    expect(
      fixture.debugElement.query(By.directive(CdkTrapFocus)).injector.get(CdkTrapFocus).enabled,
    ).toBe(true);
    const cancel = new Event('cancel', { cancelable: true });
    dialog.dispatchEvent(cancel);
    fixture.detectChanges();
    expect(cancel.defaultPrevented).toBe(true);
    expect(dialog.open).toBe(false);
    expect(document.activeElement).toBe(opener);
  });

  it('disables saving and focuses Cancel when no roles are available', () => {
    store.roles.set([]);
    fixture.detectChanges();
    open();
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
    const dialog = open();
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
    expect(dialog.open).toBe(false);
    expect(store.members()[0]!.roles).toEqual([admin, viewer]);
  });

  it.each(['rate_limited', 'unavailable'] as const)(
    'honours Retry-After for %s without changing the mapped error',
    async (kind) => {
      const error: ApiError = { ...lastAdmin, kind, retryable: true, retryAfterSeconds: 60 };
      store.changeRoles.mockImplementation(() => {
        store.editState.set({ phase: 'failed', error });
        return Promise.resolve(false);
      });
      open();
      select(viewer.id, true);
      await submit();
      expect(root.querySelector('app-retry-after')).not.toBeNull();
      expect(save().disabled).toBe(true);
      expect(root.querySelector('[data-members-error] p')?.textContent).toBe(error.message);
      save().click();
      expect(store.changeRoles).toHaveBeenCalledTimes(1);
    },
  );

  it('refuses to open without write permission and disables Save if write permission is lost', () => {
    canWrite.set(false);
    const dialog = open();
    expect(dialog.open).toBe(false);
    canWrite.set(true);
    open();
    select(viewer.id, true);
    canWrite.set(false);
    fixture.detectChanges();
    expect(save().disabled).toBe(true);
    save().click();
    expect(store.changeRoles).not.toHaveBeenCalled();
  });
});
