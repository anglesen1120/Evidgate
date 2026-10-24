// 09.1.1-T4.1 (plan 09.1.1 §10 T4): the project overview page renders what the scope guards
// resolved — the name and slug of the project the URL names, straight from `SessionStore`. The
// store is faked here (its loading is `session.store.spec.ts`'s subject); what this spec pins is
// that the page derives nothing and guesses nothing.

import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { ProjectDto } from '@evidgate/sdk';

import { SessionStore } from '../../core/session/session.store';
import { ProjectOverviewPage } from './overview.page';

/** One project, as `fixtures/web-api/projects-list/input.json` serves it. */
const PROJECT: ProjectDto = {
  id: '018f0000-0000-7000-8000-000000000011',
  org_id: '018f0000-0000-7000-8000-000000000001',
  name: 'Storefront web',
  slug: 'storefront-web',
  status: 'active',
  default_environment_id: null,
  created_at: '2026-10-02T09:00:00Z',
  updated_at: '2026-10-18T11:20:00Z',
  version: 3,
  retention_days: null,
  monthly_run_quota: 5000,
  case_prefix: 'STOR',
};

/** Mount the page against a store whose current project is `project`. */
function renderWith(project: ProjectDto | null): HTMLElement {
  TestBed.configureTestingModule({
    imports: [ProjectOverviewPage],
    providers: [
      provideZonelessChangeDetection(),
      { provide: SessionStore, useValue: { currentProject: signal(project) } },
    ],
  });
  const fixture = TestBed.createComponent(ProjectOverviewPage);
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}

describe('ProjectOverviewPage', () => {
  it('shows the current project name and slug exactly as the server sent them', () => {
    const root = renderWith(PROJECT);

    expect(root.querySelector('h1')?.textContent?.trim()).toBe(PROJECT.name);
    expect(root.querySelector('.slug')?.textContent?.trim()).toBe(PROJECT.slug);
  });

  it('renders the empty state instead of a name when no project is current', () => {
    const root = renderWith(null);

    expect(root.querySelector('h1')?.textContent?.trim()).not.toBe('');
    expect(root.querySelector('h1')?.textContent).not.toContain(PROJECT.name);
    expect(root.querySelector('.slug')).toBeNull();
  });
});
