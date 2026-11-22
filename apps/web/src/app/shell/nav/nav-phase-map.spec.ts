import { NAV_ENTRIES, NAV_GROUPS } from './nav-registry';

type Row = readonly [id: string, group: string, phase: string, scope: string, permission: string];

const MAP: readonly Row[] = [
  ['authoring', 'authoring', 'G1', 'project', 'authoring:read'],
  ['generation', 'authoring', 'G1', 'project', 'authoring:generate'],
  ['pending-changes', 'authoring', 'G1', 'project', 'authoring:write'],
  ['overview', 'workspace', 'G1', 'project', 'project:read'],
  ['onboarding', 'workspace', 'G1', 'org', 'project:create'],
  ['scripts', 'authoring', 'G2', 'project', 'run:read'],
  ['catalog', 'workspace', 'G2', 'project', 'case:read'],
  ['mapping', 'workspace', 'G2', 'project', 'case:read'],
  ['runs', 'quality', 'G2', 'project', 'run:read'],
  ['failures', 'quality', 'G3', 'project', 'healing:triage'],
  ['repairs', 'healing', 'G3', 'project', 'healing:triage'],
  ['knowledge', 'healing', 'G3', 'project', 'healing:triage'],
  ['availability', 'healing', 'G3', 'project', 'healing:triage'],
  ['studio', 'authoring', 'G4', 'project', 'draft:write'],
  ['suites', 'authoring', 'G4', 'project', 'suite:read'],
  ['quality', 'quality', 'G4', 'project', 'run:read'],
  ['releases', 'quality', 'G4', 'project', 'run:read'],
  ['policies', 'administration', 'G4', 'project', 'policy:read'],
  ['deploy', 'administration', 'G4', 'project', 'environment:write'],
  ['settings', 'administration', 'G1', 'org', 'organization:read'],
  ['ai-settings', 'administration', 'G1', 'project', 'authoring:read'],
  ['members', 'administration', 'G1', 'org', 'membership:read'],
  ['quota', 'administration', 'G1', 'org', 'usage:read'],
  ['audit', 'administration', 'G1', 'org', 'audit:read'],
  ['secrets', 'administration', 'G2', 'project', 'ingest_token:read'],
  ['security', 'administration', 'G2', 'org', 'security_event:read'],
  ['support', 'administration', 'G2', 'org', 'support:read'],
  ['agents', 'administration', 'G4', 'org', 'agent_pool:read'],
  ['integrations', 'administration', 'G4', 'project', 'webhook:read'],
  ['tokens', 'administration', 'G4', 'org', 'service_identity:read'],
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

    expect(actual.sort()).toEqual(MAP.map((row) => [...row]).sort());
  });

  it('orders the five groups as the design manifest does', () => {
    expect(NAV_GROUPS.map((group) => group.id)).toEqual([
      'workspace',
      'quality',
      'healing',
      'authoring',
      'administration',
    ]);
  });
});
