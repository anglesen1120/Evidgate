// 09.1.1-T4.1 (plan 09.1.1 §10 T4.1; 00 §2, T51): the registry's invariants. The route table and
// the sidebar both read `NAV_ENTRIES`, so a duplicated id, a duplicated segment or a permission
// string that `contracts/permissions.json` does not define would surface as a broken navigation
// instead of a red suite. The catalogue is the single source of permission strings, so it is read
// here with `node:fs` — the same read-only, offline approach as
// `../core/dev-fixtures/fixture-contract.spec.ts` — and every code is checked twice: it exists, and
// it is `tenant_assignable` (a code a member can never hold would hide its entry forever, because
// the V1 catalogue source can only grant assignable codes, D-S68-Q1).
//
// The area table is checked against the filesystem too: `app.routes.ts` mounts
// `features/<area>/<area>.routes.ts` for every entry, so an area without that module is a route the
// shell cannot build.

import { existsSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';

import { NAV_ENTRIES, NAV_GROUPS } from './nav-registry';
import { ACTIVE_PHASE, isPhaseActive } from './product-phase';

/* eslint-disable evidgate/no-hand-written-wire-dto -- `contracts/permissions.json` is a contract
   document with snake_case keys (`tenant_assignable`), not an API payload; there is no SDK type for
   a catalogue *file*, and this spec deliberately declares what it reads instead of importing a
   generated shape. Same narrow exemption as `core/dev-fixtures/fixture-contract.spec.ts`. */
/** One catalogue row of `contracts/permissions.json`, structurally declared for this spec. */
interface CatalogueEntry {
  readonly code: string;
  readonly resource: string;
  readonly action: string;
  readonly tenant_assignable: boolean;
}

interface CatalogueDocument {
  readonly permissions: readonly CatalogueEntry[];
}
/* eslint-enable evidgate/no-hand-written-wire-dto */

/**
 * The repository root: `ng test` runs with `apps/web` as its working directory, but a native Vitest
 * run may start anywhere, so it is the nearest ancestor that holds `contracts/openapi.yaml`.
 */
function findRepositoryRoot(): string {
  let directory = process.cwd();
  for (;;) {
    if (existsSync(join(directory, 'contracts', 'openapi.yaml'))) {
      return directory;
    }
    const parent = dirname(directory);
    if (parent === directory) {
      throw new Error(
        `cannot locate the repository root (the directory holding contracts/openapi.yaml) from ${process.cwd()}`,
      );
    }
    directory = parent;
  }
}

const REPOSITORY_ROOT = findRepositoryRoot();
const CATALOGUE: CatalogueDocument = JSON.parse(
  readFileSync(join(REPOSITORY_ROOT, 'contracts', 'permissions.json'), 'utf8'),
) as CatalogueDocument;
const CATALOGUE_BY_CODE = new Map(CATALOGUE.permissions.map((entry) => [entry.code, entry]));

/** The `features/<area>/<area>.routes.ts` module `app.routes.ts` mounts for every entry. */
function areaRoutePath(area: string): string {
  return join(REPOSITORY_ROOT, 'apps', 'web', 'src', 'app', 'features', area, `${area}.routes.ts`);
}

describe('NAV_GROUPS (09.1.1-T4.1)', () => {
  it('holds the five journey groups in order, with unique ids', () => {
    expect(NAV_GROUPS.map((group) => group.id)).toEqual([
      'design',
      'runs',
      'learn',
      'release',
      'settings',
    ]);
  });

  it('labels every group (CR-D-2: the v2 group labels)', () => {
    for (const group of NAV_GROUPS) {
      expect(group.label.length).toBeGreaterThan(0);
      expect(group.label).toMatch(/[A-Za-z]/);
    }
    expect(NAV_GROUPS.map((group) => group.label)).toEqual([
      'Test design',
      'Scripts & runs',
      'Learn & repair',
      'Release',
      'Settings',
    ]);
  });
});

describe('NAV_ENTRIES (09.1.1-T4.1)', () => {
  it('lists every sidebar entry of the v2 mockup once', () => {
    // `docs/design/web-ui/output/assets/evidgate.js` ROUTES has 30 rows with `sidebar: true`.
    expect(NAV_ENTRIES).toHaveLength(30);
    expect(new Set(NAV_ENTRIES.map((entry) => entry.id)).size).toBe(30);
  });

  it('uses unique ids and unique URL segments', () => {
    const ids = NAV_ENTRIES.map((entry) => entry.id);
    const segments = NAV_ENTRIES.map((entry) => entry.segment);

    expect(new Set(ids).size).toBe(ids.length);
    expect(new Set(segments).size).toBe(segments.length);
  });

  it('keeps ids, segments and areas kebab-case, and areas match their route module', () => {
    const KEBAB = /^[a-z][a-z0-9]*(-[a-z0-9]+)*$/;
    for (const entry of NAV_ENTRIES) {
      expect(entry.id).toMatch(KEBAB);
      expect(entry.segment).toMatch(KEBAB);
      expect(entry.area).toMatch(KEBAB);
      expect(existsSync(areaRoutePath(entry.area))).toBe(true);
    }
  });

  it('names the one entry whose URL segment differs from its id', () => {
    const renamed = NAV_ENTRIES.filter((entry) => entry.segment !== entry.id);

    expect(renamed.map((entry) => `${entry.id}->${entry.segment}`)).toEqual(['quota->usage']);
  });

  it('assigns every entry to a declared group, with unique order values inside a group', () => {
    const groupIds = NAV_GROUPS.map((group) => group.id);
    for (const group of groupIds) {
      const members = NAV_ENTRIES.filter((entry) => entry.group === group);
      const orders = members.map((entry) => entry.order);
      expect(orders.length).toBeGreaterThan(0);
      expect(new Set(orders).size).toBe(orders.length);
    }
    for (const entry of NAV_ENTRIES) {
      expect(groupIds).toContain(entry.group);
    }
  });

  it('scopes an entry as org or project only', () => {
    for (const entry of NAV_ENTRIES) {
      expect(['org', 'project']).toContain(entry.scope);
    }
  });

  it('uses only permission codes the contract defines', () => {
    for (const entry of NAV_ENTRIES) {
      expect(CATALOGUE_BY_CODE.has(entry.permission)).toBe(true);
    }
  });

  it('uses only codes a member principal can hold, so the V1 catalogue can grant them', () => {
    for (const entry of NAV_ENTRIES) {
      expect(CATALOGUE_BY_CODE.get(entry.permission)?.tenant_assignable).toBe(true);
    }
  });

  it('spells every permission as a lower_snake resource:action pair', () => {
    for (const entry of NAV_ENTRIES) {
      expect(entry.permission).toMatch(/^[a-z][a-z0-9_]*:[a-z][a-z0-9_]*$/);
      const [resource, action] = entry.permission.split(':');
      expect(CATALOGUE_BY_CODE.get(entry.permission)?.resource).toBe(resource);
      expect(CATALOGUE_BY_CODE.get(entry.permission)?.action).toBe(action);
    }
  });

  it('labels every entry with server-side copy the catalog can translate', () => {
    for (const entry of NAV_ENTRIES) {
      expect(entry.label.length).toBeGreaterThan(0);
      expect(entry.label).toMatch(/[A-Za-z]/);
    }
  });

  it('shows ten entries in the G1 build and starts no later-phase group', () => {
    const visible = NAV_ENTRIES.filter((entry) => isPhaseActive(entry.phase, ACTIVE_PHASE));

    expect(ACTIVE_PHASE).toBe('G1');
    expect(visible).toHaveLength(10);
    expect([...new Set(visible.map((entry) => entry.group))]).toEqual(['design', 'settings']);
    expect(visible.map((entry) => entry.id)).toEqual([
      'authoring',
      'generation',
      'pending-changes',
      'overview',
      'onboarding',
      'settings',
      'ai-settings',
      'members',
      'quota',
      'audit',
    ]);
  });
});
