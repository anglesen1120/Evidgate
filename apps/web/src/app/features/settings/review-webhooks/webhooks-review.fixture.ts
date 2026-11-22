import type { StatusTone } from '../../../shared/ui/chips/status-pill.component';
import type { ReviewStatus } from './webhooks-review.states';

interface MetricViewModel {
  readonly label: string;
  readonly value: number;
  readonly detail: string;
  readonly icon: 'lucideActivity' | 'lucideClock' | 'lucideInfo' | 'lucideCircle';
}
interface EndpointViewModel {
  readonly id: string;
  readonly name: string;
  readonly host: string;
  readonly destination: string;
  readonly status: string;
  readonly tone: StatusTone;
  readonly events: readonly string[];
  readonly signingReference: string | null;
  readonly overlapEnd: string | null;
}
interface DeliveryViewModel {
  readonly id: string;
  readonly endpointId: string;
  readonly at: string;
  readonly event: string;
  readonly label: string;
  readonly tone: StatusTone;
  readonly attempts: number;
  readonly next: string | null;
  readonly response: string;
}
export interface WebhooksViewModel {
  readonly state: ReviewStatus;
  readonly project: string;
  readonly capturedAt: string;
  readonly metrics: readonly MetricViewModel[];
  readonly endpoints: readonly EndpointViewModel[];
  readonly deliveries: readonly DeliveryViewModel[];
}
export const webhooksFixture: WebhooksViewModel = {
  state: 'ready',
  project: 'storefront-web · SHOP · p-storefront',
  capturedAt: '2026-10-13T10:42:00Z',
  metrics: [
    {
      label: $localize`:@@review.webhooks.metric.n0.label:Endpoints`,
      value: 2,
      detail: $localize`:@@review.webhooks.metric.n0.detail:Project destinations`,
      icon: 'lucideCircle',
    },
    {
      label: $localize`:@@review.webhooks.metric.n1.label:Active endpoints`,
      value: 1,
      detail: 'wh-19 · HTTPS',
      icon: 'lucideActivity',
    },
    {
      label: $localize`:@@review.webhooks.metric.n2.label:Recorded deliveries`,
      value: 2,
      detail: $localize`:@@review.webhooks.metric.n2.detail:Release checks · wh-19`,
      icon: 'lucideClock',
    },
    {
      label: $localize`:@@review.webhooks.metric.n3.label:Failed deliveries`,
      value: 1,
      detail: 'dl-91 · HTTP 503',
      icon: 'lucideInfo',
    },
  ],
  endpoints: [
    {
      id: 'wh-19',
      name: $localize`:@@review.webhooks.release:Release checks`,
      host: 'ci.example.test',
      destination: 'https://ci.example.test/hooks/evidgate',
      status: $localize`:@@review.webhooks.active:Active`,
      tone: 'success',
      events: ['run.state_changed', 'evaluation.created'],
      signingReference: 'release-checks-signing',
      overlapEnd: '2026-10-14T10:00:00Z',
    },
    {
      id: 'wh-20',
      name: $localize`:@@review.webhooks.audit:Audit sink`,
      host: 'audit.example.test',
      destination: 'https://audit.example.test/evidgate',
      status: $localize`:@@review.webhooks.disabled:Disabled`,
      tone: 'neutral',
      events: [],
      signingReference: null,
      overlapEnd: null,
    },
  ],
  deliveries: [
    {
      id: 'dl-91',
      endpointId: 'wh-19',
      at: '2026-10-13T10:42:00Z',
      event: 'evaluation.created',
      label: $localize`:@@review.webhooks.failed:Failed`,
      tone: 'danger',
      attempts: 3,
      next: '2026-10-13T10:50:00Z',
      response: '503',
    },
    {
      id: 'dl-90',
      endpointId: 'wh-19',
      at: '2026-10-13T10:35:00Z',
      event: 'run.state_changed',
      label: $localize`:@@review.webhooks.delivered:Delivered`,
      tone: 'success',
      attempts: 1,
      next: null,
      response: '2xx',
    },
  ],
};
