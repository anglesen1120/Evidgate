// Keep the store isolated so these checks cover presentation, not project loading.

import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import type { ProjectDto } from '@evidgate/sdk';
import { existsSync, readFileSync } from 'node:fs';
import { resolve } from 'node:path';

import { SessionStore } from '../../core/session/session.store';
import { NavViewService } from '../../shell/nav/nav-view';
import type { NavItemView } from '../../shell/nav/nav.models';
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

const NEXT_STEPS: readonly NavItemView[] = [
  {
    id: 'authoring',
    label: 'Feature tree',
    icon: 'tree',
    link: ['/orgs', PROJECT.org_id, 'projects', PROJECT.id, 'authoring'],
  },
  {
    id: 'onboarding',
    label: 'Onboarding',
    icon: 'compass',
    link: ['/orgs', PROJECT.org_id, 'onboarding'],
  },
  {
    id: 'runs',
    label: 'Runs',
    icon: 'play',
    link: ['/orgs', PROJECT.org_id, 'projects', PROJECT.id, 'runs'],
  },
];

/** Mount the page against a store whose current project is `project`. */
function renderWith(project: ProjectDto | null, steps = NEXT_STEPS): HTMLElement {
  TestBed.configureTestingModule({
    imports: [ProjectOverviewPage],
    providers: [
      provideZonelessChangeDetection(),
      provideRouter([]),
      {
        provide: NavViewService,
        useValue: { groups: signal([{ id: 'design', label: 'Test design', items: steps }]) },
      },
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

  it('links to the existing permitted next steps without development-status prose or invented metrics', () => {
    const root = renderWith(PROJECT);
    const links = Array.from(root.querySelectorAll<HTMLAnchorElement>('nav a'));
    expect(links.map((link) => link.textContent?.trim())).toEqual([
      'Runs',
      'Onboarding',
      'Feature tree',
    ]);
    expect(links.map((link) => link.getAttribute('href'))).toEqual([
      `/orgs/${PROJECT.org_id}/projects/${PROJECT.id}/runs`,
      `/orgs/${PROJECT.org_id}/onboarding`,
      `/orgs/${PROJECT.org_id}/projects/${PROJECT.id}/authoring`,
    ]);
    expect(root.querySelector('nav')?.getAttribute('aria-labelledby')).toBe('project-next-steps');
    expect(root.textContent).not.toContain('arrive with their own stories');
    expect(root.textContent).not.toMatch(/\d+ (runs|releases)/);
  });

  it('does not offer destinations omitted by the shared permitted navigation view', () => {
    const root = renderWith(
      PROJECT,
      NEXT_STEPS.filter((item) => item.id === 'runs'),
    );
    expect(root.querySelectorAll('nav a')).toHaveLength(1);
    expect(root.querySelector('nav a')?.textContent?.trim()).toBe('Runs');
    expect(root.textContent).not.toContain('Onboarding');
    expect(root.textContent).not.toContain('Feature tree');
  });

  it('makes the first available next step primary when runs are not available', () => {
    const root = renderWith(
      PROJECT,
      NEXT_STEPS.filter((item) => item.id !== 'runs'),
    );
    const links = Array.from(root.querySelectorAll<HTMLAnchorElement>('nav a'));
    expect(links.map((link) => link.textContent?.trim())).toEqual(['Onboarding', 'Feature tree']);
    expect(links[0]?.classList.contains('spartan-button-variant-default')).toBe(true);
    expect(root.querySelectorAll('.spartan-button-variant-default')).toHaveLength(1);
  });

  it('keeps the truthful project summary when no next step is permitted', () => {
    const root = renderWith(PROJECT, []);
    expect(root.querySelector('h1')?.textContent).toBe(PROJECT.name);
    expect(root.querySelector('nav')).toBeNull();
    expect(root.textContent).not.toContain('Next steps');
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
    expect(root.querySelector('nav')).toBeNull();
  });
});
