import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { RoleScopePreviewComponent } from './role-scope-preview.component';
import type { RolePreview } from './role-preview';

describe('RoleScopePreviewComponent', () => {
  beforeEach(() =>
    TestBed.configureTestingModule({
      imports: [RoleScopePreviewComponent],
      providers: [provideZonelessChangeDetection()],
    }),
  );
  it.each(['custom-role', 'unknown-role', 'catalogue-unavailable'] as const)(
    'reports %s as unavailable without fabricating grants',
    (reason) => {
      const fixture = TestBed.createComponent(RoleScopePreviewComponent);
      fixture.componentRef.setInput('preview', {
        kind: 'unavailable',
        reason,
      } satisfies RolePreview);
      fixture.detectChanges();
      const root = fixture.nativeElement as HTMLElement;
      expect(root.querySelector('[data-role-preview]')?.getAttribute('data-preview-kind')).toBe(
        'unavailable',
      );
      expect(root.textContent).toContain('Preview unavailable for this role');
      expect(root.textContent).toContain('The server decides actual access.');
      expect(root.querySelector('li')).toBeNull();
    },
  );
  it('renders grouped live descriptions as text and flags sensitive grants with visible text', () => {
    const fixture = TestBed.createComponent(RoleScopePreviewComponent);
    fixture.componentRef.setInput('preview', {
      kind: 'ready',
      role: {
        id: 'role1',
        code: 'viewer',
        name: 'Viewer',
        is_builtin: true,
        created_at: '2026-10-25T00:00:00Z',
        version: 1,
      },
      omitted: 2,
      groups: [
        {
          resource: 'membership',
          permissions: [
            { code: 'membership:read', description: '<img src=x>Read members', sensitive: true },
          ],
        },
      ],
    } satisfies RolePreview);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('[data-role-preview]')?.getAttribute('data-preview-kind')).toBe(
      'ready',
    );
    expect(root.querySelector('h4')?.textContent).toBe('membership');
    expect(root.querySelector('li')?.textContent).toContain('<img src=x>Read members');
    expect(root.querySelector('img')).toBeNull();
    expect(root.querySelector('.sensitive')?.textContent).toContain('Sensitive');
    expect(root.textContent).toContain('live catalogue: 2');
  });
});
