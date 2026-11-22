import type { StatusTone } from '../../../shared/ui/chips/status-pill.component';
import type { ReviewStatus } from './agent-pools-review.states';

interface MetricViewModel {
  readonly label: string;
  readonly value: number;
  readonly detail: string;
  readonly icon: 'lucideActivity' | 'lucideClock' | 'lucideInfo' | 'lucideCircle';
}
interface PoolEntryViewModel {
  readonly name: string;
  readonly description: string;
  readonly badge: string;
  readonly tone: StatusTone;
  readonly poolId: string | null;
}
interface AgentViewModel {
  readonly name: string;
  readonly detail: string;
  readonly health: string;
  readonly tone: StatusTone;
  readonly heartbeat: string;
  readonly lease: string;
  readonly job?: string;
  readonly epoch: number;
}
interface CapabilityViewModel {
  readonly name: string;
  readonly browser: string;
  readonly mobile: string;
}
export interface AgentPoolsViewModel {
  readonly state: ReviewStatus;
  readonly project: string;
  readonly capturedAt: string;
  readonly metrics: readonly MetricViewModel[];
  readonly directory: readonly PoolEntryViewModel[];
  readonly image: string;
  readonly protocol: string;
  readonly epoch: number;
  readonly capabilities: readonly CapabilityViewModel[];
  readonly agents: readonly AgentViewModel[];
}
const yes = $localize`:@@review.agent-pools.yes:Yes`;
const no = $localize`:@@review.agent-pools.no:No`;
export const agentPoolsFixture: AgentPoolsViewModel = {
  state: 'ready',
  project: 'storefront-web · SHOP · p-storefront',
  capturedAt: '2026-10-13T10:40:00Z',
  metrics: [
    {
      label: $localize`:@@review.agent-pools.metric.n0.label:Visible pools`,
      value: 2,
      detail: $localize`:@@review.agent-pools.metric.n0.detail:Project inventory`,
      icon: 'lucideCircle',
    },
    {
      label: $localize`:@@review.agent-pools.metric.n1.label:Browser agents`,
      value: 3,
      detail: 'browser-ci-east',
      icon: 'lucideActivity',
    },
    {
      label: $localize`:@@review.agent-pools.metric.n2.label:Current epoch`,
      value: 4,
      detail: $localize`:@@review.agent-pools.metric.n2.detail:job-71 · running`,
      icon: 'lucideClock',
    },
    {
      label: $localize`:@@review.agent-pools.metric.n3.label:Unmatched jobs`,
      value: 1,
      detail: 'job-72 · iOS / xctest',
      icon: 'lucideInfo',
    },
  ],
  directory: [
    {
      name: 'browser-ci-east',
      poolId: 'pool_01HX6D3K9M',
      description: 'Playwright 1.55 · linux/chromium',
      badge: $localize`:@@review.agent-pools.registered-badge:3 registered agents`,
      tone: 'info',
    },
    {
      name: 'mobile-lab',
      poolId: null,
      description: 'iOS / xctest · job-72',
      badge: $localize`:@@review.agent-pools.unmatched-badge:No matching pool`,
      tone: 'neutral',
    },
  ],
  image: 'sha256:5a73…90ce',
  protocol: 'v1',
  epoch: 4,
  capabilities: [
    {
      name: $localize`:@@review.agent-pools.engine:Engine`,
      browser: 'Playwright 1.55',
      mobile: $localize`:@@review.agent-pools.xct:xctest requested`,
    },
    {
      name: $localize`:@@review.agent-pools.platform:Platform`,
      browser: 'linux / chromium',
      mobile: no + ' · iOS',
    },
    {
      name: $localize`:@@review.agent-pools.protocol:Protocol`,
      browser: 'v1',
      mobile: $localize`:@@review.agent-pools.not-supplied:Not supplied`,
    },
    { name: $localize`:@@review.agent-pools.discovery:Discovery`, browser: yes, mobile: no },
    ...['step_events', 'retry', 'cancel'].map((name) => ({ name, browser: yes, mobile: no })),
  ],
  agents: [
    {
      name: 'web-linux-01',
      detail: 'Playwright 1.55 · linux/chromium',
      health: $localize`:@@review.agent-pools.healthy:Healthy`,
      tone: 'success',
      heartbeat: '2026-10-13T10:40:00Z',
      lease: $localize`:@@review.agent-pools.running:Running`,
      job: 'job-71',
      epoch: 4,
    },
    {
      name: 'web-linux-02',
      detail: $localize`:@@review.agent-pools.lost-detail:Server unhealthy · history retained`,
      health: $localize`:@@review.agent-pools.lost:Lost`,
      tone: 'danger',
      heartbeat: '2026-10-13T10:31:00Z',
      lease: $localize`:@@review.agent-pools.lost:Lost`,
      epoch: 3,
    },
    {
      name: 'web-linux-03',
      detail: $localize`:@@review.agent-pools.version:Runtime 1.54 · pinned 1.55`,
      health: $localize`:@@review.agent-pools.drift-label:Version drift`,
      tone: 'warning',
      heartbeat: '2026-10-13T10:37:12Z',
      lease: $localize`:@@review.agent-pools.finished:Finished`,
      epoch: 3,
    },
  ],
};
