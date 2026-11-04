// Keep the store isolated so these checks cover presentation, not project loading.

import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { ProjectDto } from '@evidgate/sdk';
import { existsSync, readFileSync } from 'node:fs';
import { resolve } from 'node:path';

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

  it.each([PROJECT, null])('labels the card section with its h1 for project %j', (project) => {
    const root = renderWith(project);
    const section = root.querySelector('section');
    const heading = root.querySelector('h1');

    expect(section?.getAttribute('data-slot')).toBe('card');
    expect(heading?.getAttribute('data-slot')).toBe('card-title');
    expect(heading?.tagName).toBe('H1');
    expect(heading?.id).toBeTruthy();
    expect(section?.getAttribute('aria-labelledby')).toBe(heading?.id);
    expect(root.querySelectorAll('h1')).toHaveLength(1);
  });

  it('renders the complete long unbroken project name', () => {
    const name = 'DựÁnStorefront'.repeat(30);
    const root = renderWith({ ...PROJECT, name });

    expect(root.querySelector('h1')?.textContent).toBe(name);
  });

  it('does not import a component stylesheet', () => {
    const relativePath = 'src/app/features/project/overview.page.ts';
    const paths = [resolve(relativePath), resolve('apps/web', relativePath)];
    const path = paths.find((candidate) => existsSync(candidate));
    if (!path) throw new Error('Cannot locate overview component source');

    const source = readFileSync(path, 'utf8');
    expect(source).not.toMatch(/\bstyleUrls?\s*:/);
    expect(source).not.toMatch(/\bimport\b[^;]*\.css/);
  });

  it('renders the empty state instead of a name when no project is current', () => {
    const root = renderWith(null);

    expect(root.querySelector('h1')?.textContent?.trim()).not.toBe('');
    expect(root.querySelector('h1')?.textContent).not.toContain(PROJECT.name);
    expect(root.querySelector('.slug')).toBeNull();
  });
});
