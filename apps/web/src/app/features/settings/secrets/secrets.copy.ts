import type { IngestTokenView } from '@evidgate/sdk';

export const SECRETS_COPY = {
  title: $localize`:@@settings.secrets.title:Secret references`,
  description: $localize`:@@settings.secrets.description:Inspect reference metadata and rotation timing. Current credential values cannot be fetched or displayed.`,
  project: $localize`:@@settings.secrets.project:Project`,
  scope: $localize`:@@settings.secrets.scope:Resolution scope`,
  prefix: $localize`:@@settings.secrets.prefix:Token prefix`,
  status: $localize`:@@settings.secrets.status:Rotation state`,
  expires: $localize`:@@settings.secrets.expires:Expires · UTC`,
  overlap: $localize`:@@settings.secrets.overlap:Overlap ends · UTC`,
  used: $localize`:@@settings.secrets.used:Last used · UTC`,
  created: $localize`:@@settings.secrets.created:Created · UTC`,
  unavailable: $localize`:@@settings.secrets.unavailable:Unavailable`,
  noExpiry: $localize`:@@settings.secrets.no-expiry:No expiry`,
  noOverlap: $localize`:@@settings.secrets.no-overlap:No overlap window`,
  neverUsed: $localize`:@@settings.secrets.never-used:Not used yet`,
  rotate: $localize`:@@settings.secrets.rotate:Rotate credential`,
  submitting: $localize`:@@settings.secrets.submitting:Rotating…`,
  emptyTitle: $localize`:@@settings.secrets.empty-title:No secret references`,
  emptyDescription: $localize`:@@settings.secrets.empty-description:Ingest token references will appear here for the selected project.`,
  noProjects: $localize`:@@settings.secrets.no-projects:No projects available in this organization.`,
  more: $localize`:@@settings.secrets.more:Load more references`,
  previous: $localize`:@@settings.secrets.previous:Previous references`,
  next: $localize`:@@settings.secrets.next:Next references`,
  pagingFailure: $localize`:@@settings.secrets.paging-failure:The next page could not be loaded. References already shown are unchanged.`,
  saved: $localize`:@@settings.secrets.saved:Rotation recorded. Update your downstream CI with the new credential and verify it there.`,
  pagesLabel: $localize`:@@settings.secrets.pages-label:Secret references pages`,
  rotateUnavailableHint: $localize`:@@settings.secrets.rotate-unavailable-hint:The rotation may have been applied. Reload the list and check the status before rotating again.`,
  reload: $localize`:@@settings.secrets.reload:Reload`,
  search: $localize`:@@settings.secrets.search:Search name or prefix`,
  loadedOnly: $localize`:@@settings.secrets.loaded-only:Search and state filters apply to this loaded page only. Complete project totals are not available.`,
  noMatches: $localize`:@@settings.secrets.no-matches:No references match these filters`,
  clear: $localize`:@@settings.secrets.clear:Clear filters`,
  inspect: $localize`:@@settings.secrets.inspect:Inspect`,
  metadata: $localize`:@@settings.secrets.metadata:Metadata only. A prefix identifies a reference; it does not authenticate.`,
  lifecycle: $localize`:@@settings.secrets.lifecycle:Lifecycle details`,
  allStates: $localize`:@@settings.secrets.all-states:All states`,
  summary: $localize`:@@settings.secrets.summary:Reference summary`,
};

export function secretStatusLabel(status: IngestTokenView['status']): string {
  switch (status) {
    case 'active':
      return $localize`:@@settings.secrets.status-active:Active`;
    case 'rotated':
      return $localize`:@@settings.secrets.status-rotated:Rotated`;
    case 'revoked':
      return $localize`:@@settings.secrets.status-revoked:Revoked`;
    case 'expired':
      return $localize`:@@settings.secrets.status-expired:Expired`;
    default:
      return status;
  }
}
