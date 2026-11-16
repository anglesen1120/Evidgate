import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import type { MembershipView, RoleView } from '@evidgate/sdk';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow/narrow-mutation-policy';
import { activeMemberMenuTrigger, memberAction } from './member-actions.test-helpers';
import { MemberListComponent } from './member-list.component';
import { MEMBERS_DESKTOP } from './members-page.test-fixtures';

function member(status: string): MembershipView {
  return {
    id: `m-${status}`,
    org_id: 'org1',
    invited_email: `${status}@example.test`,
    status: status as MembershipView['status'],
    roles: [],
    created_at: '2026-10-25T00:00:00Z',
    updated_at: '2026-10-25T00:00:00Z',
    version: 1,
  };
}

function role(name: string, index: number): RoleView {
  return {
    id: `role-${index}`,
    code: `role_${index}`,
    name,
    is_builtin: true,
    created_at: '2026-10-25T00:00:00Z',
    version: 1,
  };
}

describe('MemberListComponent', () => {
  let fixture: ComponentFixture<MemberListComponent>;
  let root: HTMLElement;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [MemberListComponent],
      providers: [provideZonelessChangeDetection(), MEMBERS_DESKTOP],
    });
    fixture = TestBed.createComponent(MemberListComponent);
    root = fixture.nativeElement as HTMLElement;
  });
  afterEach(() => {
    TestBed.resetTestingModule();
    vi.restoreAllMocks();
  });

  async function render(rows: MembershipView[], writable = false): Promise<void> {
    fixture.componentRef.setInput('members', rows);
    fixture.componentRef.setInput('canWrite', writable);
    await fixture.whenStable();
  }

  it.each([
    ['pending', 'Invited'],
    ['active', 'Active'],
    ['removed', 'Removed'],
    ['revoked', 'Revoked'],
    ['expired', 'Expired'],
  ])('labels a %s membership from the closed status lookup', async (status, expected) => {
    await render([member(status)]);
    expect(root.querySelector('.status-pill__label')?.textContent?.trim()).toBe(expected);
  });

  it.each(['suspended', 'constructor', '__proto__', '<img src=x onerror=alert(1)>'])(
    'retains the unknown status %s as escaped text, never an inherited copy property',
    async (status) => {
      await render([member(status)]);
      expect(root.querySelector('.status-pill__label')?.textContent?.trim()).toBe(status);
      expect(root.querySelector('app-status-pill img')).toBeNull();
      expect(root.querySelector('[data-members-row]')?.getAttribute('data-members-status')).toBe(
        status,
      );
    },
  );

  it('omits the entire mutation column without membership:write', async () => {
    await render([member('active')]);
    expect(root.querySelectorAll('thead th')).toHaveLength(3);
    expect(root.querySelector('[data-members-actions]')).toBeNull();
    expect(root.querySelector('app-data-table')).not.toBeNull();
    expect(root.querySelector('app-data-table')?.textContent).toContain('Members and invitations');
    expect(
      root.querySelector('th[data-column-kind="email"]')?.getAttribute('data-column-priority'),
    ).toBe('primary');
  });

  it('uses role-based column budgets and one row menu instead of percentage widths and four actions', async () => {
    await render([member('pending')], true);
    expect(root.querySelector('table')?.classList.contains('table-fixed')).toBe(false);
    expect(root.querySelectorAll('thead th[data-column-role="status"]')).toHaveLength(1);
    expect(root.querySelectorAll('thead th[data-column-role="actions"]')).toHaveLength(1);
    expect(root.querySelectorAll('[data-members-actions]')).toHaveLength(1);
    const trigger = root.querySelector<HTMLButtonElement>('[data-members-actions]');
    expect(trigger?.hasAttribute('aria-label')).toBe(false);
    expect(trigger?.textContent).toContain('Actions');
    expect(trigger?.querySelector('.sr-only')?.textContent?.trim()).toBe('pending@example.test');
    expect(root.querySelector('[data-members-edit-open]')).toBeNull();
    expect(root.textContent).toContain('No roles assigned');
    memberAction(root, '[data-members-edit-open]');
    expect(document.body.querySelectorAll('[role="menuitem"]')).toHaveLength(4);
  });
  it.each(['active', 'pending'])(
    'keeps %s member menu labels unwrapped and start aligned',
    async (status) => {
      await render([member(status)], true);
      const menu = memberAction(root, '[data-members-remove]')?.closest('hlm-dropdown-menu');
      if (!menu) throw new Error('Expected member actions menu');
      expect(menu.classList.contains('min-w-max')).toBe(true);
      for (const layout of ['justify-start', 'text-start', 'whitespace-nowrap'])
        expect(menu.classList.contains(`[&_button]:${layout}`)).toBe(true);
      expect(menu.querySelectorAll('[role="menuitem"]')).toHaveLength(status === 'pending' ? 4 : 2);
      expect(menu.querySelector('[data-members-remove]')?.getAttribute('data-variant')).toBe(
        'destructive',
      );
    },
  );
  it.each([360, 376, 480, 767])(
    'renders complete read-only labelled records at a %spx narrow viewport',
    async (width) => {
      vi.spyOn(TestBed.inject(NarrowMutationPolicy), 'narrow').mockReturnValue(true);
      vi.spyOn(Element.prototype, 'clientWidth', 'get').mockImplementation(function (
        this: Element,
      ) {
        return this.tagName === 'APP-DATA-TABLE' ? width : 0;
      });
      const membership = member('pending');
      await render(
        [{ ...membership, roles: [role('A long assigned organization role', 0)] }],
        true,
      );
      const records = root.querySelector('app-data-table-records');
      expect(records).not.toBeNull();
      expect(root.querySelector('table')).toBeNull();
      expect(records?.textContent).toContain(membership.invited_email);
      expect(records?.textContent).toContain('Invited');
      expect(records?.textContent).toContain('A long assigned organization role');
      expect(
        [...root.querySelectorAll('.eg-table-fact dt')].map((label) => label.textContent),
      ).toEqual(['Member', 'Status', 'Organization roles']);
      expect(root.querySelector('[data-members-actions]')).toBeNull();
    },
  );

  it('keeps the row menu in stacked containers when the viewport still permits mutations', async () => {
    vi.spyOn(TestBed.inject(NarrowMutationPolicy), 'narrow').mockReturnValue(false);
    vi.spyOn(Element.prototype, 'clientWidth', 'get').mockImplementation(function (this: Element) {
      return this.tagName === 'APP-DATA-TABLE' ? 360 : 0;
    });
    await render([member('pending')], true);
    expect(root.querySelector('app-data-table-records')).not.toBeNull();
    expect(root.querySelectorAll('[data-members-actions]')).toHaveLength(1);
    expect(
      [...root.querySelectorAll('.eg-table-fact dt')].map((label) => label.textContent),
    ).toEqual(['Member', 'Status', 'Actions', 'Organization roles']);
    memberAction(root, '[data-members-edit-open]');
    expect(document.body.querySelectorAll('[role="menuitem"]')).toHaveLength(4);
  });

  it('caps the scan at two role badges and exposes the exact remaining count and complete names', async () => {
    const membership = member('active');
    const names = ['Long organization administrator role', 'Auditor', 'CI producer', 'Maintainer'];
    await render([{ ...membership, roles: names.map(role) }]);
    expect(root.querySelectorAll('.role-name')).toHaveLength(2);
    const disclosure = root.querySelector<HTMLButtonElement>('[data-members-more-roles]')!;
    expect(disclosure.textContent?.trim()).toBe('+2');
    expect(disclosure.getAttribute('aria-label')).toContain('2 more roles');
    expect(root.querySelector('.role-name')?.classList.contains('whitespace-normal')).toBe(false);
    disclosure.focus();
    disclosure.click();
    await fixture.whenStable();
    const dialog = document.body.querySelector('[role="dialog"]');
    expect(dialog).not.toBeNull();
    for (const name of names) expect(dialog?.textContent).toContain(name);
    const close = dialog?.querySelector<HTMLButtonElement>('button[hlmDialogClose]');
    if (!close) throw new Error('Expected full-role-list close action');
    close.click();
    await fixture.whenStable();
    expect(document.activeElement).toBe(disclosure);
  });

  it('never adds a +N disclosure for zero, one or two roles', async () => {
    for (const count of [0, 1, 2]) {
      await render([{ ...member('active'), roles: ['One', 'Two'].slice(0, count).map(role) }]);
      expect(root.querySelectorAll('.role-name')).toHaveLength(count);
      expect(root.querySelector('[data-members-more-roles]')).toBeNull();
    }
  });

  it('marks Remove as destructive and emits the persistent row-menu trigger for confirmation', async () => {
    const membership = member('active');
    const remove = vi.fn();
    fixture.componentInstance.remove.subscribe(remove);
    await render([membership], true);
    const action = memberAction(root, '[data-members-remove]')!;
    const opener = activeMemberMenuTrigger();
    expect(action.getAttribute('data-variant')).toBe('destructive');
    action.click();
    expect(remove).toHaveBeenCalledWith({ member: membership, opener });
    expect(opener.isConnected).toBe(true);
  });

  it.each(['active', 'pending'])('offers editing for a writable %s member', async (status) => {
    const membership = member(status);
    const editRoles = vi.fn();
    fixture.componentInstance.editRoles.subscribe(editRoles);
    await render([membership], true);
    const action = memberAction(root, '[data-members-edit-open]')!;
    const opener = activeMemberMenuTrigger();
    expect(action.hasAttribute('hlmDropdownMenuItem')).toBe(true);
    action.click();
    expect(editRoles).toHaveBeenCalledWith({ member: membership, opener });
  });

  it.each(['removed', 'revoked', 'expired'])(
    'never offers editing for a terminal %s member',
    async (status) => {
      await render([member(status)], true);
      const trigger = root.querySelector<HTMLButtonElement>('[data-members-actions]');
      trigger?.click();
      await fixture.whenStable();
      expect(document.body.querySelector('[data-members-edit-open]')).toBeNull();
    },
  );

  it('offers Resend and Cancel only for the writable pending invitation', async () => {
    const membership = member('pending');
    const resend = vi.fn();
    const cancel = vi.fn();
    fixture.componentInstance.resend.subscribe(resend);
    fixture.componentInstance.cancelInvite.subscribe(cancel);
    await render([membership, member('active')], true);
    const action = memberAction(root, '[data-members-resend-open]')!;
    const opener = activeMemberMenuTrigger();
    action.click();
    expect(resend).toHaveBeenCalledWith({ member: membership, opener });
    memberAction(root, '[data-members-cancel-invite-open]')!.click();
    expect(cancel).toHaveBeenCalledWith({ member: membership, opener });
    fixture.componentRef.setInput('canWrite', false);
    await fixture.whenStable();
    expect(root.querySelector('[data-members-actions]')).toBeNull();
    expect(document.body.querySelector('[data-members-resend-open]')).toBeNull();
    expect(document.body.querySelector('[data-members-cancel-invite-open]')).toBeNull();
  });
});
