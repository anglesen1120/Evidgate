import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { SessionStore } from '../../core/session/session.store';
import { NAV_ENTRIES, SETTINGS_GROUPS } from '../nav/nav-registry';
import type { NavDestinationView, NavEntry } from '../nav/nav.models';
import { isEntryActive } from '../nav/product-phase';
import { NavViewService } from '../nav/nav-view';
import { REVIEW_OPEN_ENTRIES } from '../nav/review-entries.review';
import { NotFoundPage } from '../pages/not-found.page';
import { buildPaletteSections } from './palette-index';
import { PaletteSource } from './palette-source';

const RUN_ID = '9f1c2d3e-4a5b-4c6d-8e7f-0a1b2c3d4e5f';

function activeEntry(scope: NavEntry['scope'], excludedPermission?: string): NavEntry {
  const entry = NAV_ENTRIES.find(
    (candidate) =>
      candidate.scope === scope &&
      isEntryActive(candidate, REVIEW_OPEN_ENTRIES) &&
      candidate.permission !== excludedPermission,
  );
  if (!entry) throw new Error(`no active ${scope} entry`);
  return entry;
}

type SessionFixture = {
  org: { id: string } | null;
  project: { id: string } | null;
  permissions: readonly string[];
  organizations?: readonly { id: string; name: string }[];
  projects?: readonly { id: string; name: string; org_id: string }[];
};

/**
 * The destinations the nav view lists while the review entries are open. Stands in for
 * `NavViewService`, because the Angular unit runner rejects `vi.mock` on relative paths. The
 * visibility rule matches `nav-view.ts`.
 */
function reviewDestinations(
  session: SessionFixture,
  granted: ReadonlySet<string>,
): NavDestinationView[] {
  const org = session.org;
  if (org === null) return [];
  const project = session.project;
  return NAV_ENTRIES.flatMap((entry): NavDestinationView[] => {
    if (!isEntryActive(entry, REVIEW_OPEN_ENTRIES) || !granted.has(entry.permission)) return [];
    const groupLabel = entry.settingsGroup
      ? (SETTINGS_GROUPS.find((group) => group.id === entry.settingsGroup)?.label ??
        entry.settingsGroup)
      : undefined;
    const base = { id: entry.id, label: entry.label, ...(groupLabel ? { groupLabel } : {}) };
    if (entry.scope === 'org') return [{ ...base, link: ['/orgs', org.id, entry.segment] }];
    if (project === null) return [];
    return [{ ...base, link: ['/orgs', org.id, 'projects', project.id, entry.segment] }];
  });
}

function provideSession(session: SessionFixture): void {
  const granted = new Set(session.permissions);
  TestBed.configureTestingModule({
    providers: [
      provideRouter([{ path: '**', component: NotFoundPage }]),
      {
        provide: SessionStore,
        useValue: {
          currentOrg: signal(session.org),
          currentProject: signal(session.project),
          organizations: signal(session.organizations ?? []),
          projects: signal(session.projects ?? []),
          has: (code: string) => granted.has(code),
        },
      },
      {
        provide: NavViewService,
        useValue: { destinations: signal(reviewDestinations(session, granted)) },
      },
    ],
  });
}

function setBaseURI(href: string): void {
  Object.defineProperty(document, 'baseURI', { configurable: true, get: () => href });
}

describe('PaletteSource', () => {
  const projectEntry = activeEntry('project');
  const hiddenProjectEntry = activeEntry('project', projectEntry.permission);

  afterEach(() => {
    Reflect.deleteProperty(document, 'baseURI');
  });

  it('reads the org and project ids from the session', () => {
    provideSession({
      org: { id: 'org-1' },
      project: { id: 'proj-1' },
      permissions: [projectEntry.permission],
    });

    const snapshot = TestBed.inject(PaletteSource).snapshot();

    expect(snapshot.orgId).toBe('org-1');
    expect(snapshot.projectId).toBe('proj-1');
  });

  it('passes the organizations and projects the session lists to the index', () => {
    provideSession({
      org: { id: 'org-1' },
      project: { id: 'proj-1' },
      permissions: [projectEntry.permission],
      organizations: [{ id: 'org-2', name: 'Two' }],
      projects: [{ id: 'proj-2', name: 'Two project', org_id: 'org-1' }],
    });

    const snapshot = TestBed.inject(PaletteSource).snapshot();

    expect(snapshot.organizations).toEqual([{ id: 'org-2', name: 'Two' }]);
    expect(snapshot.projects).toEqual([{ id: 'proj-2', name: 'Two project', org_id: 'org-1' }]);
  });

  it('reports null ids and no destinations when the session has no org or project', () => {
    provideSession({ org: null, project: null, permissions: [projectEntry.permission] });

    const snapshot = TestBed.inject(PaletteSource).snapshot();

    expect(snapshot.orgId).toBeNull();
    expect(snapshot.projectId).toBeNull();
    expect(snapshot.destinations).toEqual([]);
  });

  it('omits project-scoped destinations while no project is resolved', () => {
    provideSession({ org: { id: 'org-1' }, project: null, permissions: [projectEntry.permission] });

    const ids = TestBed.inject(PaletteSource)
      .snapshot()
      .destinations.map((destination) => destination.id);

    expect(ids).not.toContain(projectEntry.id);
  });

  it('lists the granted destinations and hides the ones the session lacks permission for', () => {
    provideSession({
      org: { id: 'org-1' },
      project: { id: 'proj-1' },
      permissions: [projectEntry.permission],
    });

    const ids = TestBed.inject(PaletteSource)
      .snapshot()
      .destinations.map((destination) => destination.id);

    expect(ids).toContain(projectEntry.id);
    expect(ids).not.toContain(hiddenProjectEntry.id);
  });

  it('opens a run from a UUID query for a scoped session with run:read', () => {
    provideSession({ org: { id: 'org-1' }, project: { id: 'proj-1' }, permissions: ['run:read'] });
    const input = TestBed.inject(PaletteSource).snapshot();

    const run = buildPaletteSections(input, RUN_ID)
      .flatMap((section) => section.items)
      .find((item) => item.id === `run:${RUN_ID}`);

    expect(run?.action).toEqual({
      kind: 'navigate',
      link: ['/orgs', 'org-1', 'projects', 'proj-1', 'runs', RUN_ID],
    });
  });

  it('hides the UUID command from a scoped user without run:read, even in the phase-open build', () => {
    provideSession({ org: { id: 'org-1' }, project: { id: 'proj-1' }, permissions: [] });
    expect(buildPaletteSections(TestBed.inject(PaletteSource).snapshot(), RUN_ID)).toEqual([]);
  });

  it('offers no run item when the project is missing', () => {
    provideSession({ org: { id: 'org-1' }, project: null, permissions: ['run:read'] });
    const input = TestBed.inject(PaletteSource).snapshot();

    const ids = buildPaletteSections(input, RUN_ID)
      .flatMap((section) => section.items)
      .map((item) => item.id);

    expect(ids).not.toContain(`run:${RUN_ID}`);
  });

  it('lowercases an uppercase run id before it becomes the link segment', () => {
    provideSession({ org: { id: 'org-1' }, project: { id: 'proj-1' }, permissions: ['run:read'] });
    const input = TestBed.inject(PaletteSource).snapshot();

    const ids = buildPaletteSections(input, RUN_ID.toUpperCase())
      .flatMap((section) => section.items)
      .map((item) => item.id);

    expect(ids).toContain(`run:${RUN_ID}`);
  });

  it('offers the other locale at the same route when the base path carries a locale segment', async () => {
    provideSession({ org: { id: 'org-1' }, project: { id: 'proj-1' }, permissions: [] });
    setBaseURI('http://localhost/vi/');
    await TestBed.inject(Router).navigateByUrl('/orgs/org-1/overview');

    const snapshot = TestBed.inject(PaletteSource).snapshot();

    expect(snapshot.localeHrefs).toEqual({ en: '/en/orgs/org-1/overview' });
  });

  it('offers no locale switch when the base path has no locale segment', () => {
    provideSession({ org: { id: 'org-1' }, project: { id: 'proj-1' }, permissions: [] });
    setBaseURI('http://localhost/');

    const snapshot = TestBed.inject(PaletteSource).snapshot();

    expect(snapshot.localeHrefs).toEqual({});
  });
});
