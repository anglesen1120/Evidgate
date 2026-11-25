// The healing kill switch's session: one stored row per scope, answered the way the API answers, so
// a write is visible to the next read. The fixture models the org and project rows only; the system
// row is an operator write a tenant never makes. Only `ok` is modelled; any other variant falls
// through to the corpus.

import type { HealingSwitchResolutionView, HealingSwitchView } from '@evidgate/sdk';
import type { FixtureEntry } from './fixture-catalog-entries';

const TENANT_KEY = 'tenant';
const SEEDED_MEMBER = '018f0000-0000-7000-8000-0000000000b1';
const SEEDED_AT = '2026-11-20T09:00:00.000Z';
const MODES: readonly string[] = ['enabled', 'shadow', 'disabled'];
/** The most restrictive stored mode wins, as the resolution contract states. */
const RANK: Readonly<Record<HealingSwitchView['mode'], number>> = {
  disabled: 0,
  shadow: 1,
  enabled: 2,
};

interface SwitchWrite {
  readonly scope: 'tenant' | 'project';
  readonly projectId: string | null;
  readonly mode: HealingSwitchView['mode'];
  readonly reason: string | null;
}

function projectKey(projectId: string): string {
  return `project:${projectId}`;
}

function isMode(value: unknown): value is HealingSwitchView['mode'] {
  return typeof value === 'string' && MODES.includes(value);
}

/** The stored rows the session starts with: the org is enabled and no project has a row. */
export function seededHealingSwitches(): Map<string, HealingSwitchView> {
  return new Map<string, HealingSwitchView>([
    [
      TENANT_KEY,
      {
        scope: 'tenant',
        project_id: null,
        mode: 'enabled',
        reason: null,
        set_by: SEEDED_MEMBER,
        at: SEEDED_AT,
        version: 1,
      },
    ],
  ]);
}

/** A write the API accepts: a tenant body without `project_id`, or a project body with one. */
function switchWrite(body: unknown): SwitchWrite | undefined {
  const value: unknown = typeof body === 'string' ? JSON.parse(body) : body;
  if (typeof value !== 'object' || value === null || !('scope' in value) || !('mode' in value)) {
    return undefined;
  }
  const { scope, mode } = value;
  if (!isMode(mode)) return undefined;
  const projectId = 'project_id' in value ? value.project_id : undefined;
  const reason = 'reason' in value && typeof value.reason === 'string' ? value.reason : null;
  if (scope === 'tenant' && projectId === undefined) {
    return { scope, projectId: null, mode, reason };
  }
  if (scope === 'project' && typeof projectId === 'string') {
    return { scope, projectId, mode, reason };
  }
  return undefined;
}

/** The mode in force across the rows that exist; `shadow` when no scope has one. */
function resolvedMode(rows: readonly HealingSwitchView[]): HealingSwitchResolutionView['resolved'] {
  if (rows.length === 0) return 'shadow';
  return rows.reduce((most, row) => (RANK[row.mode] < RANK[most.mode] ? row : most)).mode;
}

function answer(operation: string, body: unknown): FixtureEntry {
  return {
    name: `healing-switch-${operation}-ok`,
    document: {
      operation_id: operation,
      variant: 'ok',
      status: 200,
      headers: {},
      latency_ms: 0,
      body,
    },
  };
}

/**
 * The entry answering a `GET` or `PUT` of the healing switch, or `undefined` so the corpus answers
 * as usual. `projectId` is the `project_id` the read names; a write names its project in the body.
 */
export function findHealingSwitchFixture(
  method: string,
  variant: string,
  projectId: string | null,
  body: unknown,
  rows: Map<string, HealingSwitchView>,
): FixtureEntry | undefined {
  if (variant !== 'ok') return undefined;
  if (method === 'GET') {
    const tenant = rows.get(TENANT_KEY) ?? null;
    const project = projectId === null ? null : (rows.get(projectKey(projectId)) ?? null);
    const view: HealingSwitchResolutionView = {
      system: null,
      tenant,
      project,
      resolved: resolvedMode(
        [tenant, project].filter((row): row is HealingSwitchView => row !== null),
      ),
      dependency_health: { llm: null, knowledge_store: null },
    };
    return answer('getHealingSwitch', view);
  }
  if (method !== 'PUT') return undefined;
  const write = switchWrite(body);
  if (write === undefined) return undefined;
  const key = write.projectId === null ? TENANT_KEY : projectKey(write.projectId);
  const row: HealingSwitchView = {
    scope: write.scope,
    project_id: write.projectId,
    mode: write.mode,
    reason: write.reason,
    set_by: SEEDED_MEMBER,
    at: new Date().toISOString(),
    version: (rows.get(key)?.version ?? 0) + 1,
  };
  rows.set(key, row);
  return answer('setHealingSwitch', row);
}
