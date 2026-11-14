import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { MembershipView, RoleView } from '@evidgate/sdk';
import { MemberListComponent } from './member-list.component';
import { RoleMatrixComponent } from './role-matrix.component';

const ROLES: RoleView[] = ['CI producer', 'Test maintainer'].map((name, index) => ({
  id: `role-${index}`,
  code: `role_${index}`,
  name,
  is_builtin: true,
  created_at: '2026-11-04T00:00:00Z',
  version: 1,
}));
const MEMBER: MembershipView = {
  id: 'member-1',
  org_id: 'org-1',
  invited_email: 'quality.engineering@example.test',
  status: 'active',
  roles: ROLES.slice(0, 1),
  created_at: '2026-11-04T00:00:00Z',
  updated_at: '2026-11-04T00:00:00Z',
  version: 1,
};

function expectTableSemantics(root: HTMLElement): void {
  const roles = [
    ['table', 'table'],
    ['thead, tbody', 'rowgroup'],
    ['tr', 'row'],
    ['th[scope="col"]', 'columnheader'],
    ['th[scope="row"]', 'rowheader'],
    ['td', 'cell'],
  ] as const;
  for (const [selector, role] of roles) {
    const elements = root.querySelectorAll(selector);
    expect(elements.length).toBeGreaterThan(0);
    for (const element of elements) expect(element.getAttribute('role')).toBe(role);
  }
  for (const label of root.querySelectorAll('.record-label')) {
    expect(label.hasAttribute('aria-hidden')).toBe(false);
  }
}

describe('RoleMatrixComponent responsive records', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [RoleMatrixComponent, MemberListComponent],
      providers: [provideZonelessChangeDetection()],
    });
  });

  async function render() {
    const fixture = TestBed.createComponent(RoleMatrixComponent);
    fixture.componentRef.setInput('matrix', {
      columns: ROLES,
      rows: [{ membership: MEMBER, granted: new Set(['role-0']) }],
    });
    await fixture.whenStable();
    return { fixture, root: fixture.nativeElement as HTMLElement };
  }

  it('keeps server role names intact in headers and labelled narrow records', async () => {
    const { root } = await render();
    expectTableSemantics(root);
    expect(root.querySelector('table[hlmTable]')).not.toBeNull();
    expect(root.querySelector('[hlmTableContainer]')?.getAttribute('tabindex')).toBe('0');
    expect(root.querySelector('[hlmTableContainer]')?.getAttribute('aria-label')).toBe(
      'Organization-wide role matrix',
    );
    const cells = Array.from(root.querySelectorAll('tbody td'));
    expect(cells).toHaveLength(2);
    for (const [index, cell] of cells.entries()) {
      const label = cell.querySelector('.record-label');
      expect(label?.textContent).toBe(ROLES[index]?.name);
      expect(label?.hasAttribute('aria-hidden')).toBe(false);
      const headers = cell.getAttribute('headers')?.split(' ') ?? [];
      expect(headers).toHaveLength(2);
      expect(root.querySelector(`[id="${headers[0]}"]`)?.textContent).toContain(
        MEMBER.invited_email,
      );
      expect(root.querySelector(`[id="${headers[1]}"]`)?.textContent).toBe(ROLES[index]?.name);
    }
    expect(cells[0]?.textContent).toContain('Granted');
    expect(cells[1]?.textContent).toContain('Not granted');
    expect(cells[0]?.querySelector('[data-granted="true"]')?.textContent?.trim()).toBe('Granted');
    expect(cells[1]?.querySelector('[data-granted]')).toBeNull();
  });

  it('defaults mobile records to granted roles and explicitly expands the full matrix', async () => {
    const { fixture, root } = await render();
    const summary = root.querySelector('[data-members-granted-summary]');
    expect(summary?.textContent).toContain(MEMBER.invited_email);
    expect(summary?.textContent).toContain(ROLES[0]?.name);
    expect(summary?.textContent).not.toContain(ROLES[1]?.name);
    const button = root.querySelector<HTMLButtonElement>('button[aria-controls]');
    if (!button) throw new Error('Expected full-matrix disclosure');
    expect(button.getAttribute('aria-expanded')).toBe('false');
    expect(root.querySelector('#members-full-matrix')?.classList.contains('max-md:hidden')).toBe(
      true,
    );
    button.click();
    await fixture.whenStable();
    expect(button.getAttribute('aria-expanded')).toBe('true');
    expect(button.textContent).toContain('Show granted roles only');
    expect(root.querySelector('[data-members-granted-summary]')).toBeNull();
    expect(root.querySelector('#members-full-matrix')?.classList.contains('max-md:hidden')).toBe(
      false,
    );
    button.click();
    await fixture.whenStable();
    expect(button.getAttribute('aria-expanded')).toBe('false');
    expect(root.querySelector('[data-members-granted-summary]')).not.toBeNull();
  });

  it('names an empty grant set without inventing assigned roles', async () => {
    const { fixture, root } = await render();
    fixture.componentRef.setInput('matrix', {
      columns: ROLES,
      rows: [{ membership: MEMBER, granted: new Set<string>() }],
    });
    await fixture.whenStable();
    const summary = root.querySelector('[data-members-granted-summary]');
    expect(summary?.textContent).toContain('No roles assigned');
    for (const role of ROLES) expect(summary?.textContent).not.toContain(role.name);
  });
  it('keeps role catalogue unavailability distinct from an empty membership list', async () => {
    const { fixture, root } = await render();
    fixture.componentRef.setInput('matrix', { columns: [], rows: [] });
    await fixture.whenStable();
    expect(root.querySelector('[role="status"]')?.textContent).toContain(
      'Role catalogue unavailable',
    );
    expect(root.querySelector('table')).toBeNull();
    fixture.componentRef.setInput('matrix', { columns: ROLES, rows: [] });
    await fixture.whenStable();
    expect(root.querySelector('table')).not.toBeNull();
    expect(root.querySelector('[role="status"]')).toBeNull();
  });

  it('gives member-directory values corresponding headers and narrow record labels', async () => {
    const fixture = TestBed.createComponent(MemberListComponent);
    fixture.componentRef.setInput('members', [MEMBER]);
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    expectTableSemantics(root);
    expect(root.querySelector('.member-identity > span:last-child')?.textContent).toBe(
      MEMBER.invited_email,
    );
    expect(
      Array.from(root.querySelectorAll('tbody .record-label')).map((label) => label.textContent),
    ).toEqual(['Member', 'Status', 'Organization roles']);
    for (const cell of root.querySelectorAll('tbody td')) {
      const headers = cell.getAttribute('headers')?.split(' ') ?? [];
      expect(headers).toHaveLength(2);
      for (const id of headers) expect(root.querySelector(`[id="${id}"]`)).not.toBeNull();
    }
    expect(root.querySelector('.role-name')?.textContent).toBe(ROLES[0]?.name);
    expect(root.querySelector('.mutation')).toBeNull();
  });
});
