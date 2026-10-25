import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { MembershipView, RoleView } from '@evidgate/sdk';
import { RoleMatrixComponent } from './role-matrix.component';

const roles: RoleView[] = ['viewer', 'qa_engineer'].map((code) => ({
  id: code,
  code,
  name: code,
  is_builtin: true,
  created_at: '2026-10-25T00:00:00Z',
  version: 1,
}));
const membership: MembershipView = {
  id: 'member1',
  org_id: 'org1',
  invited_email: 'member@example.test',
  status: 'active',
  roles: [roles[0]!],
  created_at: '2026-10-25T00:00:00Z',
  updated_at: '2026-10-25T00:00:00Z',
  version: 1,
};

describe('RoleMatrixComponent', () => {
  beforeEach(() =>
    TestBed.configureTestingModule({
      imports: [RoleMatrixComponent],
      providers: [provideZonelessChangeDetection()],
    }),
  );
  it('distinguishes granted from ungranted by visible text and scoped native table headers', () => {
    const fixture = TestBed.createComponent(RoleMatrixComponent);
    fixture.componentRef.setInput('matrix', {
      columns: roles,
      rows: [{ membership, granted: new Set(['viewer']) }],
    });
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    const cells = root.querySelectorAll('tbody td');
    expect(cells[0]?.getAttribute('data-granted')).toBe('true');
    expect(cells[0]?.textContent?.trim()).toBe('Granted');
    expect(cells[1]?.hasAttribute('data-granted')).toBe(false);
    expect(cells[1]?.textContent?.trim()).toBe('Not granted');
    expect(root.querySelector('th[scope="row"]')?.textContent).toBe(membership.invited_email);
    expect(root.querySelectorAll('th[scope="col"]')).toHaveLength(3);
    expect(root.querySelector('caption')?.textContent).toContain('Organization-wide');
  });
  it('keeps unavailable roles explicit instead of rendering invented columns', () => {
    const fixture = TestBed.createComponent(RoleMatrixComponent);
    fixture.componentRef.setInput('matrix', {
      columns: [],
      rows: [{ membership, granted: new Set() }],
    });
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('table')).toBeNull();
    expect(root.textContent).toContain('Role catalogue unavailable');
  });
});
