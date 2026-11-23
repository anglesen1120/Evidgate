import { HttpRequest } from '@angular/common/http';
import roots from '../../../../../../fixtures/web-api/authoring-tree-roots/input.json';
import { AuthoringCasesFixtureScenario } from './fixture-authoring-cases';
import type { RunSummary, UsageSummaryMetricDto, WaiverDto, QuarantineEntry } from '@evidgate/sdk';
import permissions from '../../../../../../contracts/permissions.json';
import { findFixtureEntry } from './fixture-catalog';
import type { FixtureEntry } from './fixture-catalog-entries';

const ROUTES: readonly [string, RegExp][] = [
  ['listPermissions', /^\/permissions$/],
  ['listRuns', /^\/projects\/[^/]+\/runs$/],
  ['listProjectWaivers', /^\/projects\/[^/]+\/waivers$/],
  ['listQuarantineEntries', /^\/projects\/[^/]+\/quarantine$/],
  ['listIncidents', /^\/projects\/[^/]+\/incidents$/],
  ['listCiBindings', /^\/projects\/[^/]+\/ci-bindings$/],
  ['listAuthoringTree', /^\/projects\/[^/]+\/authoring\/tree$/],
  ['listManualCases', /^\/projects\/[^/]+\/authoring\/manual-cases$/],
  ['listAuthoringChanges', /^\/projects\/[^/]+\/authoring\/changes$/],
  ['getUsageSummary', /^\/orgs\/[^/]+\/usage\/summary$/],
  ['listAudit', /^\/orgs\/[^/]+\/audit$/],
  ['listProjects', /^\/orgs\/[^/]+\/projects$/],
  ['listMemberships', /^\/orgs\/[^/]+\/memberships$/],
];

export function dashboardFixture(
  method: string,
  path: string,
  variantFor: (operation: string) => string,
  requestUrl = '',
): FixtureEntry | undefined {
  if (method !== 'GET' || !variantFor('listPermissions').startsWith('dashboard')) return undefined;
  const operation = ROUTES.find(([, pattern]) => pattern.test(path))?.[0];
  if (!operation) return undefined;
  const variant = variantFor(operation);
  const document = {
    operation_id: operation,
    variant,
    headers: {},
    latency_ms: 0,
    status: 200,
    body: boundedBody(bodyFor(operation, path, variantFor('listPermissions')), requestUrl),
  };
  if (variant === 'dashboard-empty')
    document.body =
      operation === 'getUsageSummary'
        ? { metrics: [], period: '2026-11', project: null, next_cursor: null }
        : { items: [], next_cursor: null };
  if (variant === 'dashboard-error') {
    document.status = 503;
    document.body = {
      error: {
        code: 'dependency_busy',
        message: 'This section is temporarily unavailable.',
        request_id: 'dashboard-fixture',
        retryable: true,
        details: [],
      },
    };
  }
  return { name: `dashboard-${operation}-${variant}`, document };
}

function boundedBody(body: unknown, requestUrl: string): unknown {
  const limit = Number(new URL(requestUrl, 'https://fixtures.invalid').searchParams.get('limit'));
  if (
    limit <= 0 ||
    !Number.isSafeInteger(limit) ||
    body === null ||
    typeof body !== 'object' ||
    !('items' in body) ||
    !Array.isArray(body.items)
  )
    return body;
  return {
    ...body,
    items: body.items.slice(0, limit),
    next_cursor: body.items.length > limit ? 'dashboard-next' : null,
  };
}

function bodyFor(operation: string, path: string, grantsVariant: string): unknown {
  const project = path.split('/')[2] ?? '';
  switch (operation) {
    case 'listPermissions':
      return {
        items: permissions.permissions.filter(
          (permission) =>
            grantsVariant !== 'dashboard-limited' ||
            ['project:read', 'organization:read'].includes(permission.code),
        ),
        next_cursor: null,
      };
    case 'listRuns':
      return { items: runs(), next_cursor: null };
    case 'listProjectWaivers':
      return { items: waivers(project), next_cursor: null };
    case 'listQuarantineEntries':
      return { items: quarantines(project), next_cursor: null };
    case 'listIncidents':
      return {
        items: [
          { id: '018f0000-0000-7000-8000-00000000ab01' },
          { id: '018f0000-0000-7000-8000-00000000ab02' },
        ],
        next_cursor: null,
      };
    case 'getUsageSummary':
      return {
        period: '2026-11',
        project: null,
        metrics: [metric('results', 640, 1000), metric('ai_tokens', null, null)],
        next_cursor: null,
      };
    case 'listProjects':
      return findFixtureEntry('listProjects', 'ok')?.document.body;
    case 'listMemberships':
      return findFixtureEntry('listMemberships', 'ok')?.document.body;
    case 'listAudit':
      return findFixtureEntry('listAudit', 'ok')?.document.body;
    case 'listCiBindings':
      return findFixtureEntry('listCiBindings', 'onboarding-connected')?.document.body;
    case 'listAuthoringTree':
      return roots.body;
    case 'listManualCases':
      return new AuthoringCasesFixtureScenario().entry(new HttpRequest('GET', path), 'ok').document
        .body;
    case 'listAuthoringChanges':
      return { items: [], next_cursor: null };
    default:
      return { items: [], next_cursor: null };
  }
}

function runs(): readonly RunSummary[] {
  const decisions: readonly Exclude<RunSummary['decision'], undefined>[] = [
    'PASS',
    'PASS',
    'FAIL',
    'PASS',
    'INCONCLUSIVE',
    'PASS',
    'FAIL',
    'PASS',
    'PASS',
    'INCONCLUSIVE',
    null,
    null,
  ];
  return decisions.map((decision, index) => ({
    run_id: `018f0000-0000-7000-8000-${String(800 + index).padStart(12, '0')}`,
    state: index >= 10 ? 'collecting' : 'complete',
    created_at: `2026-11-${String(18 + Math.floor(index / 2)).padStart(2, '0')}T${index % 2 ? '16' : '09'}:00:00Z`,
    ref: {
      kind: 'branch',
      name: index % 3 ? 'main' : 'release/november-checkout',
      pr_number: null,
      head_sha: null,
      base_sha: null,
      trigger_event: 'push',
      is_fork: false,
    },
    unverified_ref: false,
    completeness: index >= 10 ? null : 'complete',
    parent_run_id: null,
    rerun_scope: null,
    decision,
  }));
}

function waivers(project: string): readonly WaiverDto[] {
  return [
    {
      id: '018f0000-0000-7000-8000-00000000ac01',
      project_id: project,
      scope: 'case',
      target_id: '018f0000-0000-7000-8000-00000000ac02',
      reason: 'Temporary environment exception',
      requested_by: '018f0000-0000-7000-8000-0000000000c1',
      approved_by: '018f0000-0000-7000-8000-0000000000c2',
      state: 'approved',
      expires_at: '2026-11-27T09:00:00Z',
      created_at: '2026-11-20T09:00:00Z',
      updated_at: '2026-11-20T09:00:00Z',
    },
  ];
}
function quarantines(project: string): readonly QuarantineEntry[] {
  return [
    {
      id: '018f0000-0000-7000-8000-00000000ad01',
      project_id: project,
      case_id: '018f0000-0000-7000-8000-00000000ac02',
      owner: '018f0000-0000-7000-8000-0000000000c1',
      reason: 'Investigating intermittent fixture dependency',
      state: 'active',
      expires_at: '2026-11-28T09:00:00Z',
      created_at: '2026-11-20T09:00:00Z',
      updated_at: '2026-11-20T09:00:00Z',
    },
  ];
}
function metric(
  name: UsageSummaryMetricDto['metric'],
  used: number | null,
  limit: number | null,
): UsageSummaryMetricDto {
  return {
    metric: name,
    scope: 'tenant',
    used,
    limit,
    warned_at: null,
    blocked_at: null,
    retry_after_seconds: null,
    unavailable_reason: used === null ? 'producer_not_available' : null,
    projects: [],
    projects_status: { blocked_count: 0, warned_count: 0 },
  };
}
