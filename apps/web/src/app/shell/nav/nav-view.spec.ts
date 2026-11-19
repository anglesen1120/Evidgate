// 09.1.1-T4.1 (plan 09.1.1 §10 T4.1, §5; CR-D-2 navigation rule; D-S68-Q1/Q4): the sidebar view.
// The spec runs the real `SessionStore` against fake identity reads, so it proves the three filters
// together — phase, server-declared permission and scope — and the URLs the items carry, rather
// than restating the registry.
//
// The permission set is built from the registry's own codes, so a new entry cannot be added without
// this suite exercising it.

import { TestBed } from '@angular/core/testing';
import type { OrganizationSummaryDto, ProjectDto } from '@evidgate/sdk';

import { IdentityDataService } from '../../core/data/identity-data.service';
import { EffectivePermissionsSource } from '../../core/session/effective-permissions.source';
import { SessionStore } from '../../core/session/session.store';
import type { NavGroupView } from './nav.models';
import { NAV_ENTRIES } from './nav-registry';
import { NavViewService } from './nav-view';

const ORG: OrganizationSummaryDto = {
  id: 'o-1',
  slug: 'acme-robotics',
  name: 'Acme Robotics',
  status: 'active',
};
const OTHER_ORG: OrganizationSummaryDto = {
  id: 'o-2',
  slug: 'lumen-labs',
  name: 'Lumen Labs',
  status: 'active',
};

function project(id: string, orgId: string): ProjectDto {
  return {
    id,
    org_id: orgId,
    name: `Project ${id}`,
    slug: `project-${id}`,
    status: 'active',
    default_environment_id: null,
    created_at: '2026-10-02T09:00:00Z',
    updated_at: '2026-10-18T11:20:00Z',
    version: 1,
    retention_days: null,
    monthly_run_quota: null,
    case_prefix: 'PROJ',
  };
}

const PROJECT = project('p-1', 'o-1');

/** Every code the registry can gate on — the whole sidebar's grant set. */
const ALL_CODES: ReadonlySet<string> = new Set(NAV_ENTRIES.map((entry) => entry.permission));

class FakeIdentityDataService {
  organizations: readonly OrganizationSummaryDto[] = [ORG, OTHER_ORG];
  projectsByOrg = new Map<string, readonly ProjectDto[]>([['o-1', [PROJECT]]]);

  listMyOrganizations(): Promise<readonly OrganizationSummaryDto[]> {
    return Promise.resolve(this.organizations);
  }

  listProjects(orgId: string): Promise<readonly ProjectDto[]> {
    return Promise.resolve(this.projectsByOrg.get(orgId) ?? []);
  }

  listPermissions(): Promise<never> {
    throw new Error('the nav view must not read the catalogue directly');
  }
}

class FakePermissionsSource {
  granted: ReadonlySet<string> = ALL_CODES;

  load(): Promise<ReadonlySet<string>> {
    return Promise.resolve(this.granted);
  }
}

/** The ids of one group's items, in render order. */
function idsOf(groups: readonly NavGroupView[], groupId: string): string[] {
  return groups.find((group) => group.id === groupId)?.items.map((item) => item.id) ?? [];
}

/** The link of one item, or `undefined` when it is not rendered. */
function linkOf(view: NavViewService, entryId: string): readonly string[] | undefined {
  for (const group of view.groups()) {
    const item = group.items.find((candidate) => candidate.id === entryId);
    if (item !== undefined) {
      return item.link;
    }
  }
  return undefined;
}

describe('NavViewService (09.1.1-T4.1)', () => {
  let identity: FakeIdentityDataService;
  let source: FakePermissionsSource;
  let session: SessionStore;
  let view: NavViewService;

  beforeEach(() => {
    identity = new FakeIdentityDataService();
    source = new FakePermissionsSource();
    TestBed.configureTestingModule({
      providers: [
        { provide: IdentityDataService, useValue: identity },
        { provide: EffectivePermissionsSource, useValue: source },
      ],
    });
    session = TestBed.inject(SessionStore);
    view = TestBed.inject(NavViewService);
  });

  async function authenticatedWithProject(): Promise<void> {
    await session.bootstrap();
    await session.resolveScope(ORG.id, PROJECT.id);
  }

  it('renders nothing before an org is resolved', async () => {
    await session.bootstrap();

    expect(view.groups()).toEqual([]);
  });

  it('renders nothing for an empty permission set', async () => {
    source.granted = new Set<string>();
    await authenticatedWithProject();

    expect(view.groups()).toEqual([]);
  });

  it('renders only the G1 entries, in the two groups that hold them', async () => {
    await authenticatedWithProject();

    expect(view.groups().map((group) => group.id)).toEqual(['design', 'settings']);
    expect(idsOf(view.groups(), 'design')).toEqual([
      'authoring',
      'generation',
      'pending-changes',
      'overview',
      'onboarding',
    ]);
    expect(idsOf(view.groups(), 'settings')).toEqual([
      'settings',
      'ai-settings',
      'members',
      'quota',
      'audit',
    ]);
  });

  it('hides a later-phase entry even when its permission is granted', async () => {
    await authenticatedWithProject();

    const laterPhase = ['runs', 'scripts', 'catalog', 'mapping', 'secrets', 'security', 'support'];
    for (const id of laterPhase) {
      expect(linkOf(view, id)).toBeUndefined();
    }
    expect(view.groups().map((group) => group.id)).not.toContain('runs');
    expect(view.groups().map((group) => group.id)).not.toContain('learn');
    expect(view.groups().map((group) => group.id)).not.toContain('release');
  });

  it('hides an entry whose permission the set lacks', async () => {
    source.granted = new Set([...ALL_CODES].filter((code) => code !== 'authoring:write'));
    await authenticatedWithProject();

    expect(linkOf(view, 'pending-changes')).toBeUndefined();
    expect(idsOf(view.groups(), 'design')).toEqual([
      'authoring',
      'generation',
      'overview',
      'onboarding',
    ]);
  });

  it('drops a whole group when none of its entries survive', async () => {
    // One design-only code that gates no settings entry.
    source.granted = new Set(['authoring:generate']);
    await authenticatedWithProject();

    expect(view.groups().map((group) => group.id)).toEqual(['design']);
    expect(idsOf(view.groups(), 'design')).toEqual(['generation']);
  });

  it('gates the AI settings entry on authoring:read, not project:read', async () => {
    source.granted = new Set(['project:read']);
    await authenticatedWithProject();
    expect(linkOf(view, 'ai-settings')).toBeUndefined();
    expect(linkOf(view, 'overview')).toBeDefined();
  });

  it('shows the AI settings entry to a holder of authoring:read', async () => {
    source.granted = new Set(['authoring:read']);
    await authenticatedWithProject();

    expect(linkOf(view, 'ai-settings')).toBeDefined();
  });

  it('needs a project for a project-scope entry and only an org for an org-scope one', async () => {
    await session.bootstrap();
    await session.resolveScope(ORG.id, null);

    expect(idsOf(view.groups(), 'design')).toEqual(['onboarding']);
    expect(idsOf(view.groups(), 'settings')).toEqual(['settings', 'members', 'quota', 'audit']);
    expect(linkOf(view, 'ai-settings')).toBeUndefined();
  });

  it('links an org-scope entry under the org and a project-scope entry under its project', async () => {
    await authenticatedWithProject();

    expect(linkOf(view, 'onboarding')).toEqual(['/orgs', 'o-1', 'onboarding']);
    expect(linkOf(view, 'authoring')).toEqual(['/orgs', 'o-1', 'projects', 'p-1', 'authoring']);
    expect(linkOf(view, 'settings')).toEqual(['/orgs', 'o-1', 'settings']);
    expect(linkOf(view, 'ai-settings')).toEqual(['/orgs', 'o-1', 'projects', 'p-1', 'ai-settings']);
  });

  it('maps the quota entry to the /usage segment', async () => {
    await authenticatedWithProject();

    expect(linkOf(view, 'quota')).toEqual(['/orgs', 'o-1', 'usage']);
  });

  it('projects a separate short label without replacing the complete accessible label', async () => {
    await authenticatedWithProject();
    const quota = view
      .groups()
      .flatMap((group) => group.items)
      .find((item) => item.id === 'quota');
    const registryRow = NAV_ENTRIES.find((entry) => entry.id === 'quota');
    expect(quota?.label).toBe(registryRow?.label);
    expect(quota?.shortLabel).toBe('Usage & credits');
    expect(quota?.shortLabel).toBe(registryRow?.shortLabel);
    expect(quota?.link).toEqual(['/orgs', 'o-1', 'usage']);
  });

  it('renders the labels and icons the registry declares', async () => {
    await authenticatedWithProject();

    const authoring = view
      .groups()
      .flatMap((group) => group.items)
      .find((item) => item.id === 'authoring');
    const registryRow = NAV_ENTRIES.find((entry) => entry.id === 'authoring');

    expect(authoring?.label).toBe(registryRow?.label);
    expect(authoring?.icon).toBe(registryRow?.icon);
    expect(view.groups()[0]?.label).toBe('Test design');
  });

  it('recomputes when the resolved project changes', async () => {
    // Both projects are listed before the scope resolves, so the case holds whether the store
    // answers a same-org switch from its cache or re-reads the list.
    identity.projectsByOrg.set('o-1', [PROJECT, project('p-2', 'o-1')]);
    await authenticatedWithProject();
    expect(linkOf(view, 'authoring')).toEqual(['/orgs', 'o-1', 'projects', 'p-1', 'authoring']);

    await session.resolveScope(ORG.id, 'p-2');

    expect(linkOf(view, 'authoring')).toEqual(['/orgs', 'o-1', 'projects', 'p-2', 'authoring']);
  });

  it('recomputes to nothing when the scope is refused', async () => {
    await authenticatedWithProject();

    await session.resolveScope('o-foreign', null);

    expect(view.groups()).toEqual([]);
  });
});
