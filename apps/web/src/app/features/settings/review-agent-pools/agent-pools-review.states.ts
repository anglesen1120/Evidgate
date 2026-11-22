import type { InlineFeedbackTone } from '../../../shared/ui/states/inline-feedback.component';

export type ReviewStatus =
  | 'ready'
  | 'loading'
  | 'empty'
  | 'no-match'
  | 'error'
  | '403'
  | '404'
  | '401'
  | '429'
  | '412'
  | 'unavailable'
  | 'stale'
  | 'read-only'
  | 'long'
  | 'drift'
  | 'unschedulable'
  | 'restored';
interface NoticeViewModel {
  readonly title: string;
  readonly description?: string;
  readonly tone: InlineFeedbackTone;
}
export const notices: ReadonlyMap<string, NoticeViewModel> = new Map<string, NoticeViewModel>([
  [
    'loading',
    { tone: 'info', title: $localize`:@@review.agent-pools.loading.title:Loading the snapshot…` },
  ],
  [
    'empty',
    {
      tone: 'info',
      title: $localize`:@@review.agent-pools.empty.title:No agent pools configured`,
      description: $localize`:@@review.agent-pools.empty.detail:No project-visible pools are configured. See the existing agent-pool setup documentation for the next step; no runner is provisioned by this page.`,
    },
  ],
  [
    'no-match',
    {
      tone: 'info',
      title: $localize`:@@review.agent-pools.no.match.title:No results match these filters`,
      description: $localize`:@@review.agent-pools.no.match.detail:Clear the pool filter to see available project-visible records. The selected detail is not substituted with another pool.`,
    },
  ],
  [
    'error',
    {
      tone: 'destructive',
      title: $localize`:@@review.agent-pools.error.title:Could not load agent pool data`,
      description: $localize`:@@review.agent-pools.error.detail:Safe project context is retained. Request ID: req_eg_fixture_429 · retry only when the server permits it.`,
    },
  ],
  [
    '403',
    {
      tone: 'destructive',
      title: $localize`:@@review.agent-pools.http-403.title:Agent pools are not authorized`,
      description: $localize`:@@review.agent-pools.http-403.detail:You do not have permission to read agent pools in this project. The server declined this request; no role name grants access here.`,
    },
  ],
  [
    '404',
    {
      tone: 'destructive',
      title: $localize`:@@review.agent-pools.http-404.title:Not found`,
      description: $localize`:@@review.agent-pools.http-404.detail:This resource was not found. Unknown and foreign-tenant identifiers use the same response.`,
    },
  ],
  [
    '401',
    {
      tone: 'warning',
      title: $localize`:@@review.agent-pools.http-401.title:Sign-in required`,
      description: $localize`:@@review.agent-pools.http-401.detail:Sign in again to return to the selected record in storefront-web.`,
    },
  ],
  [
    '429',
    {
      tone: 'warning',
      title: $localize`:@@review.agent-pools.http-429.title:Rate limited`,
      description: $localize`:@@review.agent-pools.http-429.detail:The server asked this client to wait before retrying. Retry-After: 30 seconds · server supplied.`,
    },
  ],
  [
    '412',
    {
      tone: 'warning',
      title: $localize`:@@review.agent-pools.http-412.title:Someone else changed this — reload`,
      description: $localize`:@@review.agent-pools.http-412.detail:Linked confirmed pool-edit specimen · current revision 18 · changed fields: protocol, pinned image. Your draft keeps protocol v1 and pinned image sha256:5a73…90ce; current server values are protocol v1 and sha256:6b21…14af. This is not an agent-pool read response.`,
    },
  ],
  [
    'unavailable',
    {
      tone: 'warning',
      title: $localize`:@@review.agent-pools.unavailable.title:Capabilities unavailable`,
      description: $localize`:@@review.agent-pools.unavailable.detail:Capability records were not returned. Values are unavailable, not interpreted as unsupported, and no capability is inferred.`,
    },
  ],
  [
    'stale',
    {
      tone: 'warning',
      title: $localize`:@@review.agent-pools.stale.title:Heartbeat state supplied by server`,
      description: $localize`:@@review.agent-pools.stale.detail:web-linux-02 is marked unhealthy/lost. Last heartbeat 2026-10-13 10:31:00Z · epoch 3. No client threshold is applied and history is retained.`,
    },
  ],
  [
    'read-only',
    {
      tone: 'info',
      title: $localize`:@@review.agent-pools.read.only.title:Read-only · permission changed`,
      description: $localize`:@@review.agent-pools.read.only.detail:Read access is retained; pool changes are not permitted. Revoked read access is shown by the 403 specimen.`,
    },
  ],
  [
    'long',
    {
      tone: 'info',
      title: $localize`:@@review.agent-pools.long.title:Long capability labels wrap`,
    },
  ],
  [
    'drift',
    {
      tone: 'warning',
      title: $localize`:@@review.agent-pools.drift.title:Runtime mismatch`,
      description: $localize`:@@review.agent-pools.drift.detail:web-linux-03 advertises 1.54; browser-ci-east pins 1.55. Inspect the registered agent below.`,
    },
  ],
  [
    'unschedulable',
    {
      tone: 'warning',
      title: $localize`:@@review.agent-pools.unschedulable.title:job-72 · Unschedulable`,
      description: $localize`:@@review.agent-pools.unschedulable.detail:No pool advertises platform ios / engine xctest.`,
    },
  ],
  [
    'restored',
    {
      tone: 'success',
      title: $localize`:@@review.agent-pools.restored.title:Snapshot received`,
      description: $localize`:@@review.agent-pools.restored.detail:Agent records are available again. Health comes from the supplied snapshot.`,
    },
  ],
]);
