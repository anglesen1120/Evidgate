import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { CiBindingView } from '@evidgate/sdk';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { ConnectCiBindingTableComponent } from './connect-ci-binding-table.component';

provideLayoutTestStubs();

it('retains CI binding rows with one named progressbar when a table without a pager refetches', async () => {
  TestBed.configureTestingModule({
    imports: [ConnectCiBindingTableComponent],
    providers: [provideZonelessChangeDetection()],
  });
  const fixture = TestBed.createComponent(ConnectCiBindingTableComponent);
  const binding: CiBindingView = {
    id: 'binding-1',
    project_id: 'project-1',
    provider: 'github_actions',
    repository_id: 123,
    repository_full_name: 'acme/storefront',
    allowed_ref_patterns: ['refs/heads/main'],
    allowed_workflow_refs: [],
    status: 'active',
    activated_at: null,
    created_at: '2026-10-25T00:00:00Z',
    version: 1,
  };
  fixture.componentRef.setInput('bindings', [binding]);
  fixture.componentRef.setInput('truncated', false);
  fixture.componentRef.setInput('fetching', true);
  const root = fixture.nativeElement as HTMLElement;
  await vi.waitFor(() => {
    fixture.detectChanges();
    expect(root.querySelectorAll('[role="progressbar"]')).toHaveLength(1);
  });
  expect(root.querySelector('[role="progressbar"]')?.getAttribute('aria-label')).toBe('Loading…');
  expect(root.textContent).toContain(binding.repository_full_name);
  expect(root.querySelector('table')?.getAttribute('aria-busy')).toBe('true');
  fixture.componentRef.setInput('fetching', false);
  fixture.detectChanges();
  expect(root.querySelector('[role="progressbar"]')).toBeNull();
});
