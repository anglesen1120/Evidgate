// 09.7.1-T2 fix round F3: the status label is a closed switch over the five contract statuses,
// not `MEMBERS_COPY[member.status]` — an object indexed by a server string answers inherited
// members such as `constructor` or `toString` for a value the contract does not define. These
// specs pin the contract labels and both fallback shapes.
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import type { MembershipView } from '@evidgate/sdk';
import { MemberListComponent } from './member-list.component';

function member(status: MembershipView['status'] | (string & {})): MembershipView {
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

describe('MemberListComponent status labels (09.7.1-T2 F3)', () => {
  let fixture: ComponentFixture<MemberListComponent>;
  let root: HTMLElement;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [MemberListComponent],
      providers: [provideZonelessChangeDetection()],
    });
    fixture = TestBed.createComponent(MemberListComponent);
    root = fixture.nativeElement as HTMLElement;
  });
  afterEach(() => TestBed.resetTestingModule());

  function labelOf(membership: MembershipView): string {
    fixture.componentRef.setInput('members', [membership]);
    fixture.detectChanges();
    return root.querySelector('app-status-pill .status-pill__label')?.textContent?.trim() ?? '';
  }

  const CONTRACT_LABELS: ReadonlyArray<[MembershipView['status'], string]> = [
    ['pending', 'Pending invitation'],
    ['active', 'Active'],
    ['removed', 'Removed'],
    ['revoked', 'Revoked'],
    ['expired', 'Expired'],
  ];

  it.each(CONTRACT_LABELS)('labels a %s membership from the closed lookup', (status, expected) => {
    expect(labelOf(member(status))).toBe(expected);
  });

  it('shows an unexpected status as the server wrote it, as plain text', () => {
    expect(labelOf(member('suspended'))).toBe('suspended');
    expect(root.querySelector('.status-pill__label')?.textContent?.trim()).toBe('suspended');
  });

  it('shows a status named "constructor" as that text, not an object prototype member', () => {
    // `MEMBERS_COPY['constructor']` is `Object`'s constructor function: the old lookup rendered a
    // function source instead of the server's value.
    const label = labelOf(member('constructor'));
    expect(label).toBe('constructor');
    expect(label).not.toContain('function');
    expect(root.querySelector('[data-members-row]')?.getAttribute('data-members-status')).toBe(
      'constructor',
    );
  });

  it('keeps an unexpected status out of the DOM as markup: it is interpolated text only', () => {
    fixture.componentRef.setInput('members', [member('<img src=x onerror=alert(1)>')]);
    fixture.detectChanges();
    // The raw value reaches the pill only through interpolation; no element is created from it.
    expect(root.querySelector('app-status-pill img')).toBeNull();
    expect(root.querySelector('.status-pill__label')?.textContent?.trim()).toBe(
      '<img src=x onerror=alert(1)>',
    );
  });

  it('omits the write column entirely without membership:write', () => {
    fixture.componentRef.setInput('canWrite', false);
    fixture.componentRef.setInput('members', [member('active')]);
    fixture.detectChanges();
    expect(root.querySelectorAll('th[scope="col"]')).toHaveLength(3);
    expect(root.querySelector('[data-members-remove]')).toBeNull();
    expect(root.querySelector('[data-members-edit-open]')).toBeNull();
    expect(root.querySelector('table[hlmTable]')).not.toBeNull();
    const region = root.querySelector('[hlmTableContainer]');
    expect(region?.getAttribute('tabindex')).toBe('0');
    expect(region?.getAttribute('aria-label')).toBe('Members and invitations');
  });

  it('wraps roles and actions inside their cells and reserves room for status words', () => {
    const membership = member('pending');
    fixture.componentRef.setInput('canWrite', true);
    fixture.componentRef.setInput('members', [membership]);
    fixture.detectChanges();
    expect(root.querySelector('table')?.classList.contains('table-fixed')).toBe(true);
    expect(root.querySelector('#members-status')?.classList.contains('w-1/6')).toBe(true);
    const actions = root.querySelector('[data-members-edit-open]')?.parentElement;
    if (actions === null || actions === undefined) throw new Error('row actions not rendered');
    expect(actions.classList.contains('flex-wrap')).toBe(true);
    expect(actions.parentElement?.tagName).toBe('TD');
    expect(actions.querySelectorAll('button')).toHaveLength(4);
    for (const button of actions.querySelectorAll('button')) {
      expect(button.classList.contains('whitespace-normal')).toBe(true);
      expect(button.classList.contains('max-w-full')).toBe(true);
    }
    const roles = root.querySelector('td[headers$="members-roles"] > span:last-child');
    expect(roles?.classList.contains('flex-wrap')).toBe(true);
    expect(roles?.textContent?.trim()).toBe('No roles assigned');
  });

  it('separates the destructive remove trigger from editing and still emits a confirmation opener', () => {
    const membership = member('active');
    const remove = vi.fn();
    fixture.componentInstance.remove.subscribe(remove);
    fixture.componentRef.setInput('canWrite', true);
    fixture.componentRef.setInput('members', [membership]);
    fixture.detectChanges();
    const opener = root.querySelector<HTMLButtonElement>('[data-members-remove]');
    if (!opener) throw new Error('Expected remove trigger');
    expect(opener.classList.contains('spartan-button-variant-destructive')).toBe(true);
    expect(opener.classList.contains('ms-3')).toBe(true);
    expect(
      root
        .querySelector('[data-members-edit-open]')
        ?.classList.contains('spartan-button-variant-outline'),
    ).toBe(true);
    opener.click();
    expect(remove).toHaveBeenCalledWith({ member: membership, opener });
  });
  it.each(['active', 'pending'] as const)(
    'offers editing for a writable %s member and emits its opener',
    (status) => {
      const membership = member(status);
      const editRoles = vi.fn();
      fixture.componentInstance.editRoles.subscribe(editRoles);
      fixture.componentRef.setInput('canWrite', true);
      fixture.componentRef.setInput('members', [membership]);
      fixture.detectChanges();
      const opener = root.querySelector<HTMLButtonElement>('[data-members-edit-open]')!;
      expect(opener.closest('.mutation')).not.toBeNull();
      expect(opener.hasAttribute('hlmBtn')).toBe(true);
      expect(opener.getAttribute('type')).toBe('button');
      opener.click();
      expect(editRoles).toHaveBeenCalledWith({ member: membership, opener });
    },
  );

  it.each(['removed', 'revoked', 'expired'] as const)(
    'never offers editing for a terminal %s member',
    (status) => {
      fixture.componentRef.setInput('canWrite', true);
      fixture.componentRef.setInput('members', [member(status)]);
      fixture.detectChanges();
      expect(root.querySelector('[data-members-edit-open]')).toBeNull();
    },
  );
  it.each([false, true])('only offers pending-invite actions when writable=%s', (writable) => {
    fixture.componentRef.setInput('canWrite', writable);
    fixture.componentRef.setInput(
      'members',
      ['pending', 'active', 'revoked', 'expired', 'removed'].map(member),
    );
    fixture.detectChanges();
    expect(root.querySelectorAll('[data-members-resend-open]')).toHaveLength(writable ? 1 : 0);
    expect(root.querySelectorAll('[data-members-cancel-invite-open]')).toHaveLength(
      writable ? 1 : 0,
    );
    if (writable) {
      const resend = vi.fn();
      const cancel = vi.fn();
      fixture.componentInstance.resend.subscribe(resend);
      fixture.componentInstance.cancelInvite.subscribe(cancel);
      const resendOpener = root.querySelector<HTMLButtonElement>('[data-members-resend-open]')!;
      const cancelOpener = root.querySelector<HTMLButtonElement>(
        '[data-members-cancel-invite-open]',
      )!;
      resendOpener.click();
      cancelOpener.click();
      expect(resend).toHaveBeenCalledWith({ member: member('pending'), opener: resendOpener });
      expect(cancel).toHaveBeenCalledWith({ member: member('pending'), opener: cancelOpener });
    }
  });
});
