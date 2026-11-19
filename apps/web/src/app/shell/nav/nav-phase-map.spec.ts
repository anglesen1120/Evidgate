// The journey regroup of the sidebar, pinned as one table: group, phase, scope, segment and
// permission of every entry. A moved, dropped, re-phased or re-gated entry turns this suite red, so
// "no route lost" is a compared set and not a count. The table follows
// `docs/design/web/authoring-nav-phase.md`, except that `ai-settings` is gated on `authoring:read`
// (the design text still names the earlier `project:read`).

import { NAV_ENTRIES, NAV_GROUPS } from './nav-registry';

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
});
