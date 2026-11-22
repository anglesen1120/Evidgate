import type { Completeness, DecisionValue, Outcome } from '../../../shared/ui/verdict';

export interface QualityDayViewModel {
  readonly date: string;
  readonly observation: {
    readonly percent: number;
    readonly passed: number;
    readonly failed: number;
    readonly other: number;
    readonly complete: number;
    readonly summary: string;
  } | null;
}

export interface QualityCaseViewModel {
  readonly id: string;
  readonly name: string;
  readonly longName?: string;
  readonly flakePercent: number;
  readonly runs: number;
  readonly lastFlaked: string | null;
}

interface BindingViewModel {
  readonly label: string;
  readonly revision: string | null;
  readonly completeness: Completeness | null;
  readonly outcome: Outcome | null;
  readonly decision: DecisionValue | null;
  readonly context: string;
}

export interface QualityOverviewViewModel {
  readonly from: string;
  readonly to: string;
  readonly project: string;
  readonly branch: string;
  readonly asOf: string;
  readonly median: string;
  readonly upper: string;
  readonly latencySamples: string;
  readonly incompleteRate: string;
  readonly collection: string;
  readonly metrics: readonly {
    readonly key: 'rate' | 'p50' | 'incomplete' | 'p95';
    readonly icon: 'lucideActivity' | 'lucideClock' | 'lucideInfo';
    readonly title: string;
    readonly value: string;
    readonly description: string;
  }[];
  readonly days: readonly QualityDayViewModel[];
  readonly cases: readonly QualityCaseViewModel[];
  readonly bindings: readonly BindingViewModel[];
}

export const qualityFixture: QualityOverviewViewModel = {
  from: '2026-09-14',
  to: '2026-10-13',
  project: 'storefront-web',
  branch: 'develop',
  asOf: '2026-10-13T10:20Z',
  median: $localize`:@@review.quality.median-seconds:p50 · 42 seconds`,
  upper: $localize`:@@review.quality.upper-seconds:p95 · 118 seconds`,
  latencySamples: $localize`:@@review.quality.sample-note:120 samples · supplied by server`,
  incompleteRate: '10%',
  collection: $localize`:@@review.quality.collection:12 incomplete / 120 eligible terminal runs. Canceled runs excluded.`,
  metrics: [
    {
      key: 'rate',
      icon: 'lucideActivity',
      title: $localize`:@@review.quality.complete-rate:Complete-run pass rate`,
      value: '90%',
      description: $localize`:@@review.quality.rate-caption:18 of 20 · 12 Oct`,
    },
    {
      key: 'p50',
      icon: 'lucideClock',
      title: $localize`:@@review.quality.median:Decision latency · p50`,
      value: $localize`:@@review.quality.median-value:42 s`,
      description: $localize`:@@review.quality.evaluated:120 evaluated runs`,
    },
    {
      key: 'incomplete',
      icon: 'lucideInfo',
      title: $localize`:@@review.quality.incomplete:Incomplete rate`,
      value: '10%',
      description: $localize`:@@review.quality.eligible:12 of 120 eligible terminal runs`,
    },
    {
      key: 'p95',
      icon: 'lucideClock',
      title: $localize`:@@review.quality.percentile:Decision latency · p95`,
      value: $localize`:@@review.quality.percentile-value:118 s`,
      description: $localize`:@@review.quality.samples:Supplied percentile · 120 samples`,
    },
  ],
  days: [
    { date: '2026-10-10', observation: null },
    {
      date: '2026-10-11',
      observation: {
        percent: 80,
        passed: 16,
        failed: 3,
        other: 1,
        complete: 20,
        summary: $localize`:@@review.quality.outcome-11:16 passed · 3 failed · 1 other`,
      },
    },
    {
      date: '2026-10-12',
      observation: {
        percent: 90,
        passed: 18,
        failed: 2,
        other: 0,
        complete: 20,
        summary: $localize`:@@review.quality.outcome-12:18 passed · 2 failed · 0 other`,
      },
    },
  ],
  cases: [
    {
      id: 'case-address-8',
      name: $localize`:@@review.quality.address:Address formats postcode`,
      flakePercent: 12.5,
      runs: 24,
      lastFlaked: '2026-10-12T08:30Z',
    },
    {
      id: 'case-search-4',
      name: $localize`:@@review.quality.search:Search products by phrase`,
      longName: $localize`:@@review.quality.long-search:Localized storefront search for an exceptionally long multiword product phrase across language fallbacks`,
      flakePercent: 8.3,
      runs: 36,
      lastFlaked: null,
    },
  ],
  bindings: [
    {
      label: 'sr-27',
      revision: 'run-1842 / ev-3',
      completeness: 'incomplete',
      outcome: 'has_failures',
      decision: 'FAIL',
      context: $localize`:@@review.quality.suite-note:Suite revision · shard-3 missing; the original failure remains.`,
    },
    {
      label: 'web',
      revision: 'run-1844 / ev-4',
      completeness: 'complete',
      outcome: 'all_passed',
      decision: 'PASS',
      context: $localize`:@@review.quality.web-note:Release component · fresh evidence.`,
    },
    {
      label: 'api',
      revision: 'run-1837 / ev-2',
      completeness: 'complete',
      outcome: 'all_passed',
      decision: 'PASS',
      context: $localize`:@@review.quality.api-note:Release component · evidence predates the recorded build.`,
    },
    {
      label: 'worker',
      revision: null,
      completeness: null,
      outcome: null,
      decision: null,
      context: $localize`:@@review.quality.worker-note:Release component · no eligible run was bound.`,
    },
  ],
};
