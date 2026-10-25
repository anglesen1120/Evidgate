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
});
