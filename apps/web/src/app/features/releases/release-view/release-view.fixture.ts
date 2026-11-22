import type {
  Completeness,
  DecisionValue,
  Outcome,
} from '../../../shared/ui/verdict/verdict-values';

export type ComponentKey = 'web' | 'api' | 'worker';
export type ComponentFilter = 'all' | ComponentKey;

export interface ReleaseBindingViewModel {
  readonly component: ComponentKey;
  readonly digest: string;
  readonly evaluation: {
    readonly run: string;
    readonly evidence: string;
    readonly freshness: 'fresh' | 'stale';
    readonly completeness: Completeness;
    readonly outcome: Outcome;
    readonly decision: DecisionValue;
  } | null;
}

interface ReleaseMetric {
  readonly title: string;
  readonly value: string;
  readonly note: string;
  readonly icon: 'lucideInfo' | 'lucideActivity' | 'lucideClock';
}

export interface ReleaseViewModel {
  readonly metrics: readonly ReleaseMetric[];
  readonly bindings: readonly ReleaseBindingViewModel[];
  readonly contexts: readonly { component: ComponentKey; title: string; description: string }[];
  readonly policy: string;
  readonly separateRun: {
    readonly reference: string;
    readonly completeness: Completeness;
    readonly outcome: Outcome;
    readonly decision: DecisionValue;
    readonly approver: string;
    readonly expires: string;
  };
}

export const RELEASE_VIEW: ReleaseViewModel = {
  metrics: [
    {
      title: $localize`:@@review.release.metric.assessment.title:Release assessment`,
      value: $localize`:@@review.release.metric.assessment.value:INCONCLUSIVE`,
      note: $localize`:@@review.release.metric.assessment.note:rel-2026.10.13 · rd-14`,
      icon: 'lucideInfo',
    },
    {
      title: $localize`:@@review.release.metric.bound.title:Bound runs`,
      value: $localize`:@@review.release.metric.bound.value:2 / 3`,
      note: $localize`:@@review.release.metric.bound.note:One component has no run`,
      icon: 'lucideActivity',
    },
    {
      title: $localize`:@@review.release.metric.freshness.title:Evidence freshness`,
      value: $localize`:@@review.release.metric.freshness.value:24 h`,
      note: $localize`:@@review.release.metric.freshness.note:Policy window · pr-12`,
      icon: 'lucideClock',
    },
    {
      title: $localize`:@@review.release.metric.late.title:Late-evidence window`,
      value: $localize`:@@review.release.metric.late.value:15 min`,
      note: $localize`:@@review.release.metric.late.note:Supplied by policy`,
      icon: 'lucideClock',
    },
  ],
  bindings: [
    {
      component: 'web',
      digest: 'sha256:a72c…09de',
      evaluation: {
        run: 'run-1844',
        evidence: 'ev-4',
        freshness: 'fresh',
        completeness: 'complete',
        outcome: 'all_passed',
        decision: 'PASS',
      },
    },
    {
      component: 'api',
      digest: 'sha256:b91f…17bc',
      evaluation: {
        run: 'run-1837',
        evidence: 'ev-2',
        freshness: 'stale',
        completeness: 'complete',
        outcome: 'all_passed',
        decision: 'PASS',
      },
    },
    { component: 'worker', digest: 'sha256:c44a…2a78', evaluation: null },
  ],
  contexts: [
    {
      component: 'worker',
      title: $localize`:@@review.release.context.worker.title:worker — No eligible run`,
      description: $localize`:@@review.release.context.worker.description:No bound run or evidence revision. A missing worker cannot contribute a pass.`,
    },
    {
      component: 'api',
      title: $localize`:@@review.release.context.api.title:api — Evidence predates build`,
      description: $localize`:@@review.release.context.api.description:run-1837 predates the recorded API digest. PASS remains pinned to ev-2.`,
    },
    {
      component: 'web',
      title: $localize`:@@review.release.context.web.title:web — Evidence matches recorded digest`,
      description: $localize`:@@review.release.context.web.description:The earlier run register showed ev-3. As of 2026-10-13 10:15 UTC, rd-14 pins run-1844 / ev-4 to the web digest. A later revision cannot move this binding.`,
    },
  ],
  policy: 'pr-12 · ph-114a…a38e',
  separateRun: {
    reference: 'run-1842 · ev-3',
    completeness: 'incomplete',
    outcome: 'has_failures',
    decision: 'FAIL',
    approver: 'An Phạm',
    expires: '2026-10-15T00:00Z',
  },
};

export function componentFilter(value: unknown): ComponentFilter {
  return value === 'web' || value === 'api' || value === 'worker' ? value : 'all';
}
