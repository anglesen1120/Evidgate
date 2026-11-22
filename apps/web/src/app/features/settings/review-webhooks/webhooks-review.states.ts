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
  | 'submitting'
  | 'created'
  | 'rotated'
  | 'saved'
  | 'copy-fail'
  | 'unavailable'
  | 'stale'
  | 'read-only'
  | 'long'
  | 'empty-history'
  | 'rotation-error'
  | 'rotation-lost'
  | 'unhealthy'
  | 'pending-delivery';
interface NoticeViewModel {
  readonly title: string;
  readonly description?: string;
  readonly tone: InlineFeedbackTone;
}
export const notices: ReadonlyMap<string, NoticeViewModel> = new Map<string, NoticeViewModel>([
  [
    'loading',
    { tone: 'info', title: $localize`:@@review.webhooks.loading.title:Loading the snapshot…` },
  ],
  [
    'empty',
    {
      tone: 'info',
      title: $localize`:@@review.webhooks.empty.title:No webhook endpoints`,
      description: $localize`:@@review.webhooks.empty.detail:This project has no endpoints. An empty list is not a delivery success; no endpoint or credential is generated here.`,
    },
  ],
  [
    'no-match',
    {
      tone: 'info',
      title: $localize`:@@review.webhooks.no.match.title:No deliveries match this filter`,
      description: $localize`:@@review.webhooks.no.match.detail:Endpoint metadata remains visible above. Clear the event filter to restore the supplied delivery rows.`,
    },
  ],
  [
    'error',
    {
      tone: 'destructive',
      title: $localize`:@@review.webhooks.error.title:Integrations could not be loaded`,
      description: $localize`:@@review.webhooks.error.detail:Safe project context is retained. Request ID: req-wh-503-fixture · retry is deliberate.`,
    },
  ],
  [
    '403',
    {
      tone: 'destructive',
      title: $localize`:@@review.webhooks.http-403.title:Integrations are not authorized`,
      description: $localize`:@@review.webhooks.http-403.detail:webhook:read or webhook:write was not granted in this scope. The server declined the request; no endpoint metadata is inferred.`,
    },
  ],
  [
    '404',
    {
      tone: 'destructive',
      title: $localize`:@@review.webhooks.http-404.title:Integration not found`,
      description: $localize`:@@review.webhooks.http-404.detail:Unknown and foreign-tenant identifiers use the same response. No existence detail is disclosed.`,
    },
  ],
  [
    '401',
    {
      tone: 'warning',
      title: $localize`:@@review.webhooks.http-401.title:Sign in again`,
      description: $localize`:@@review.webhooks.http-401.detail:Sign in again to return to the selected record in storefront-web.`,
    },
  ],
  [
    '429',
    {
      tone: 'warning',
      title: $localize`:@@review.webhooks.http-429.title:Too many requests`,
      description: $localize`:@@review.webhooks.http-429.detail:Server Retry-After: 30 seconds. No delivery state or retry time is computed here.`,
    },
  ],
  [
    '412',
    {
      tone: 'warning',
      title: $localize`:@@review.webhooks.http-412.title:Someone else changed this — reload`,
      description: $localize`:@@review.webhooks.http-412.detail:Endpoint edit conflict · wh-19. Draft at revision 8: /hooks/evidgate. Current revision 9: /hooks/release. The draft is retained; reload deliberately rather than resubmitting.`,
    },
  ],
  [
    'submitting',
    {
      tone: 'info',
      title: $localize`:@@review.webhooks.submitting.title:Reviewing local intent · no request sent`,
      description: $localize`:@@review.webhooks.submitting.detail:The confirmation is a local specimen. No credential, endpoint or redelivery request is created.`,
    },
  ],
  [
    'created',
    {
      tone: 'success',
      title: $localize`:@@review.webhooks.created.title:Create intent prepared · nothing created`,
      description: $localize`:@@review.webhooks.created.detail:The form is a local fixture. Endpoint creation and its server response are not supplied; reuse the idempotency key for any real retry.`,
    },
  ],
  [
    'rotated',
    {
      tone: 'success',
      title: $localize`:@@review.webhooks.rotated.title:Rotation recorded · value redacted`,
      description: $localize`:@@review.webhooks.rotated.detail:Only rotation metadata is shown. No signing value was generated, revealed or copied.`,
    },
  ],
  [
    'saved',
    {
      tone: 'success',
      title: $localize`:@@review.webhooks.saved.title:Redelivery intent recorded · no request sent`,
      description: $localize`:@@review.webhooks.saved.detail:Source delivery dl-91 and its history are unchanged. Attempts and retry timing remain server-declared; nothing is retried automatically.`,
    },
  ],
  [
    'copy-fail',
    {
      tone: 'warning',
      title: $localize`:@@review.webhooks.copy.fail.title:Clipboard unavailable · redacted only`,
      description: $localize`:@@review.webhooks.copy.fail.detail:Only the placeholder [REDACTED · VALUE NOT GENERATED] can be copied. No credential value exists in this fixture.`,
    },
  ],
  [
    'unavailable',
    {
      tone: 'warning',
      title: $localize`:@@review.webhooks.unavailable.title:Rotation result unavailable`,
      description: $localize`:@@review.webhooks.unavailable.detail:The server did not return a rotation result. The overlap end and credential state remain the supplied values; no value is invented.`,
    },
  ],
  [
    'stale',
    {
      tone: 'warning',
      title: $localize`:@@review.webhooks.stale.title:Endpoint metadata may be stale`,
      description: $localize`:@@review.webhooks.stale.detail:The supplied snapshot is 2026-10-13T10:40Z. Delivery history is server-reported; reload is explicit and nothing is replayed.`,
    },
  ],
  [
    'read-only',
    {
      tone: 'info',
      title: $localize`:@@review.webhooks.read.only.title:Read-only · webhook:write revoked`,
      description: $localize`:@@review.webhooks.read.only.detail:Metadata remains readable. Create, rotate and redeliver controls are unavailable; no credential is exposed.`,
    },
  ],
  [
    'long',
    {
      tone: 'info',
      title: $localize`:@@review.webhooks.long.title:Long URLs wrap without clipping`,
    },
  ],
  [
    'empty-history',
    {
      tone: 'info',
      title: $localize`:@@review.webhooks.empty.history.title:No deliveries recorded`,
      description: $localize`:@@review.webhooks.empty.history.detail:No delivery records were supplied for this endpoint.`,
    },
  ],
  [
    'rotation-error',
    {
      tone: 'destructive',
      title: $localize`:@@review.webhooks.rotation.error.title:Rotation did not complete`,
      description: $localize`:@@review.webhooks.rotation.error.detail:wh-19 · req-wh-503. Inspect the last supplied signing metadata before starting another intent.`,
    },
  ],
  [
    'rotation-lost',
    {
      tone: 'warning',
      title: $localize`:@@review.webhooks.rotation.lost.title:Rotation response lost · outcome unknown`,
      description: $localize`:@@review.webhooks.rotation.lost.detail:The response for wh-19 was lost. Check the last known signing metadata; it does not establish whether this rotation took effect. Do not submit another rotation until the server confirms the outcome.`,
    },
  ],
  [
    'unhealthy',
    {
      tone: 'warning',
      title: $localize`:@@review.webhooks.unhealthy.title:Endpoint reported unhealthy`,
      description: $localize`:@@review.webhooks.unhealthy.detail:wh-19 · latest response 503. Delivery dl-91 remains failed.`,
    },
  ],
  [
    'pending-delivery',
    {
      tone: 'info',
      title: $localize`:@@review.webhooks.pending.delivery.title:Pending delivery`,
      description: $localize`:@@review.webhooks.pending.delivery.detail:No completed delivery or next attempt was supplied.`,
    },
  ],
]);
