import { computed, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute } from '@angular/router';
import { combineLatest, map } from 'rxjs';
import { REPAIR, REPLACEMENT, REVIEWED } from './repair-review.fixture';

const COMMON = [
  'default',
  'loading',
  'empty',
  'filtered',
  'error',
  '403',
  '404',
  '401',
  '429',
  '412',
  'reloaded',
  'proposed',
  'running',
  'missing',
  'negative',
  'stale',
  'expired',
  'approved',
  'applied',
  'rejected',
  'revoked',
  'unavailable',
] as const;
const SHADOW = [
  'nolineage',
  'restricted',
  'nodom',
  'observing',
  'merged',
  'closed',
  'ready',
  'off',
  'scan',
] as const;
const APPROVAL = [
  'featureoff',
  'disabled',
  'shadowonly',
  'conflict',
  'submitting',
  '422',
  'prepared',
] as const;
export type ReviewState = (typeof COMMON | typeof SHADOW | typeof APPROVAL)[number];
export type ReviewDestination =
  'failure-detail' | 'healing-queue' | 'shadow-detail' | 'healing-approval' | 'knowledge-lineage';

export function reviewContext(screen: 'shadow' | 'approval') {
  const route = inject(ActivatedRoute);
  const query = toSignal(route.queryParamMap, { requireSync: true });
  const scope = toSignal(
    combineLatest(route.pathFromRoot.map((r) => r.paramMap)).pipe(
      map((parts) => ({
        org: parts.find((p) => p.has('orgId'))?.get('orgId'),
        project: parts.find((p) => p.has('projectId'))?.get('projectId'),
      })),
    ),
    { requireSync: true },
  );
  const states: readonly ReviewState[] = [...COMMON, ...(screen === 'shadow' ? SHADOW : APPROVAL)];
  const state = computed((): ReviewState => {
    const ids = query().getAll('id');
    if (ids.length > 1 || (ids.length === 1 && ids[0] !== REPAIR.id)) return '404';
    const raw = query().get('state') ?? 'default';
    if (raw === 'denied') return '403';
    if (raw === 'success') return 'approved';
    if (raw === 'conflict' && screen === 'shadow') return '412';
    return states.find((s) => s === raw) ?? '404';
  });
  const revision = computed(() =>
    state() === 'reloaded' || state() === '412' ? REPLACEMENT : REVIEWED,
  );
  const validation = computed(() => {
    switch (state()) {
      case 'proposed':
      case 'running':
      case 'missing':
      case 'negative':
      case 'stale':
      case 'expired':
        return null;
      default:
        return revision().validation;
    }
  });
  const isolated = computed(() => ['loading', 'empty', '401', '403', '404'].includes(state()));
  const relatedState = computed(() => {
    const current = state();
    return current !== 'default' && COMMON.some((value) => value === current) ? current : null;
  });
  // Destroy a local decision draft when its scope or revision preview changes.
  const draftScope = computed(() => [JSON.stringify([scope().org, scope().project, state()])]);
  const link = (destination: ReviewDestination) => {
    const { org, project } = scope();
    return org && project ? ['/dev', destination, 'orgs', org, 'projects', project] : null;
  };
  return { state, revision, validation, isolated, relatedState, draftScope, link };
}
export type RepairReviewContext = ReturnType<typeof reviewContext>;
