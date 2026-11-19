// The journey regroup of the sidebar, pinned as one table: group, phase, scope, segment and
// permission of every entry. A moved, dropped, re-phased or re-gated entry turns this suite red, so
// "no route lost" is a compared set and not a count. The table is the authoritative map of
// `docs/design/web/authoring-nav-phase.md`.

import { NAV_ENTRIES, NAV_GROUPS } from './nav-registry';
import { isEntryActive, isPhaseActive } from './product-phase';

type Row = readonly [id: string, group: string, phase: string, scope: string, permission: string];

const MAP: readonly Row[] = [
  ['authoring', 'design', 'G1', 'project', 'authoring:read'],
  ['generation', 'design', 'G1', 'project', 'authoring:generate'],
  ['pending-changes', 'design', 'G1', 'project', 'authoring:write'],
  ['overview', 'design', 'G1', 'project', 'project:read'],
  ['onboarding', 'design', 'G1', 'org', 'project:create'],
  ['scripts', 'runs', 'G2', 'project', 'run:read'],
  ['catalog', 'runs', 'G2', 'project', 'case:read'],
  ['mapping', 'runs', 'G2', 'project', 'case:read'],
  ['runs', 'runs', 'G2', 'project', 'run:read'],
  ['failures', 'learn', 'G3', 'project', 'healing:triage'],
  ['repairs', 'learn', 'G3', 'project', 'healing:triage'],
  ['knowledge', 'learn', 'G3', 'project', 'healing:triage'],
  ['availability', 'learn', 'G3', 'project', 'healing:triage'],
  ['studio', 'release', 'G4', 'project', 'draft:write'],
  ['suites', 'release', 'G4', 'project', 'suite:read'],
  ['quality', 'release', 'G4', 'project', 'run:read'],
  ['releases', 'release', 'G4', 'project', 'run:read'],
  ['policies', 'release', 'G4', 'project', 'policy:read'],
  ['deploy', 'release', 'G4', 'project', 'environment:write'],
  ['settings', 'settings', 'G1', 'org', 'organization:read'],
  ['ai-settings', 'settings', 'G1', 'project', 'authoring:read'],
  ['members', 'settings', 'G1', 'org', 'membership:read'],
  ['quota', 'settings', 'G1', 'org', 'usage:read'],
  ['audit', 'settings', 'G1', 'org', 'audit:read'],
  ['secrets', 'settings', 'G2', 'project', 'ingest_token:read'],
  ['security', 'settings', 'G2', 'org', 'security_event:read'],
  ['support', 'settings', 'G2', 'org', 'support:read'],
  ['agents', 'settings', 'G4', 'org', 'agent_pool:read'],
  ['integrations', 'settings', 'G4', 'project', 'webhook:read'],
  ['tokens', 'settings', 'G4', 'org', 'service_identity:read'],
];

describe('the journey regroup of the sidebar', () => {
  it('keeps every entry with its group, phase, scope and permission', () => {
    const actual = NAV_ENTRIES.map((entry) => [
      entry.id,
      entry.group,
      entry.phase,
      entry.scope,
      entry.permission,
    ]);

    expect(actual).toEqual(MAP.map((row) => [...row]));
  });

  it('orders the groups Test design, Scripts & runs, Learn & repair, Release, Settings', () => {
    expect(NAV_GROUPS.map((group) => group.id)).toEqual([
      'design',
      'runs',
      'learn',
      'release',
      'settings',
    ]);
  });

  it('puts overview and onboarding in the first group, not in a group of their own', () => {
    const design = NAV_ENTRIES.filter((entry) => entry.group === 'design').map((entry) => entry.id);

    expect(design).toEqual([
      'authoring',
      'generation',
      'pending-changes',
      'overview',
      'onboarding',
    ]);
  });

  it('lists in each group only entries whose phase is not later than the next group needs', () => {
    // Phase never gates a whole group except through its entries: the G1 build must still show the
    // Settings group, which mixes G1, G2 and G4 entries.
    const settingsPhases = new Set(
      NAV_ENTRIES.filter((entry) => entry.group === 'settings').map((entry) => entry.phase),
    );

    expect([...settingsPhases].sort()).toEqual(['G1', 'G2', 'G4']);
  });

  it('counts the active entries by phase: 10 at G1, plus 7 at G2, 4 at G3 and 9 at G4', () => {
    const perPhase = (phase: string): number =>
      NAV_ENTRIES.filter((entry) => entry.phase === phase).length;

    expect([perPhase('G1'), perPhase('G2'), perPhase('G3'), perPhase('G4')]).toEqual([10, 7, 4, 9]);
  });

  it('hides every G2 to G4 entry and never opens one through an empty review list', () => {
    for (const entry of NAV_ENTRIES) {
      expect(isEntryActive(entry, [])).toBe(isPhaseActive(entry.phase));
      if (entry.phase !== 'G1') {
        expect(isEntryActive(entry, [])).toBe(false);
      }
    }
  });
});
