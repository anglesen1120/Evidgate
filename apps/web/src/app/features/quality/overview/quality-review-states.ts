import type { InlineFeedbackTone } from '../../../shared/ui/states/inline-feedback.component';

interface Notice {
  readonly title: string;
  readonly description?: string;
  readonly tone: InlineFeedbackTone;
  readonly action?: string;
}

const notices = {
  '401': {
    tone: 'info',
    title: $localize`:@@review.quality.code-401.title:Your session expired`,
    description: $localize`:@@review.quality.code-401.detail:Sign in again to return to this view.`,
    action: $localize`:@@review.quality.action.signin:Sign in`,
  },
  '403': {
    tone: 'info',
    title: $localize`:@@review.quality.code-403.title:Not authorized`,
    description: $localize`:@@review.quality.code-403.detail:Read permission is required in this project. No protected data is displayed.`,
  },
  '404': {
    tone: 'info',
    title: $localize`:@@review.quality.code-404.title:Not found`,
    description: $localize`:@@review.quality.code-404.detail:Check the address or return to the project.`,
  },
  '412': {
    tone: 'warning',
    title: $localize`:@@review.quality.code-412.title:Someone else changed this — reload`,
    description: $localize`:@@review.quality.code-412.detail:Linked policy edit, not an overview GET error. policy-r12 → policy-r13: threshold 2 → 1. Draft threshold 3 retained, not merged.`,
    action: $localize`:@@review.quality.action.reload:Reload current view`,
  },
  '429': {
    tone: 'warning',
    title: $localize`:@@review.quality.code-429.title:Too many requests`,
    description: $localize`:@@review.quality.code-429.detail:Server Retry-After: 60 seconds. No automatic retry.`,
    action: $localize`:@@review.quality.action.retry:Retry`,
  },
  empty: {
    tone: 'info',
    title: $localize`:@@review.quality.empty.title:No eligible runs in this period`,
    description: $localize`:@@review.quality.empty.detail:No data — 0 samples. An absent denominator is not 0% or a PASS.`,
  },
  'no-match': {
    tone: 'info',
    title: $localize`:@@review.quality.no-match.title:No results match these filters`,
    description: $localize`:@@review.quality.no-match.detail:Records exist outside this selection. Clear the filters to see them.`,
    action: $localize`:@@review.quality.action.clear:Clear filters`,
  },
  error: {
    tone: 'destructive',
    title: $localize`:@@review.quality.error.title:Could not load this snapshot`,
    description: $localize`:@@review.quality.error.detail:Last good snapshot retained · 2026-10-13T10:20Z · req-quality-503a`,
    action: $localize`:@@review.quality.action.retry:Retry`,
  },
  early: {
    tone: 'info',
    title: $localize`:@@review.quality.early.title:Available in G4`,
    description: $localize`:@@review.quality.early.detail:This reference is hidden in G1–G3. No assessment is available here.`,
    action: $localize`:@@review.quality.action.authoring:Open authoring`,
  },
  loading: {
    tone: 'info',
    title: $localize`:@@review.quality.loading.title:Loading · last good snapshot retained`,
  },
  partial: {
    tone: 'warning',
    title: $localize`:@@review.quality.partial.title:Partial series`,
    description: $localize`:@@review.quality.partial.detail:10 October is missing. The trend stops at the gap; no interpolation.`,
  },
  stale: {
    tone: 'warning',
    title: $localize`:@@review.quality.stale.title:Projection may be stale`,
    description: $localize`:@@review.quality.stale.detail:Watermark 2026-10-13T10:00Z · prj-18. Last snapshot: 2026-10-13T10:20Z.`,
  },
  purged: {
    tone: 'info',
    title: $localize`:@@review.quality.purged.title:Historical data purged`,
    description: $localize`:@@review.quality.purged.detail:The requested history was removed under retention policy. It is unavailable, not zero.`,
  },
  '503': {
    tone: 'destructive',
    title: $localize`:@@review.quality.code-503.title:503 · Service unavailable`,
    description: $localize`:@@review.quality.code-503.detail:Server Retry-After: 60 seconds · req-quality-503a. Showing last good snapshot.`,
    action: $localize`:@@review.quality.action.retry:Retry`,
  },
  invalid: {
    tone: 'destructive',
    title: $localize`:@@review.quality.invalid.title:400 / 422 · Invalid date range`,
    description: $localize`:@@review.quality.invalid.detail:Choose an end date on or after the start date, within 366 days. No previous rate is reused.`,
  },
  range: {
    tone: 'info',
    title: $localize`:@@review.quality.range.title:Range changed`,
    description: $localize`:@@review.quality.range.detail:Only the snapshot matching the current filter is displayed.`,
  },
  unprovided: {
    tone: 'info',
    title: $localize`:@@review.quality.unprovided.title:No snapshot supplied for this selection`,
    description: $localize`:@@review.quality.unprovided.detail:The fixture covers 14 September–13 October only. Reset the period to inspect supplied measures.`,
  },
} satisfies Record<string, Notice>;

type NoticeState = keyof typeof notices;
export type QualityState = NoticeState | 'default' | 'zero' | 'unavailable' | 'long';

function isNoticeState(value: string): value is NoticeState {
  return Object.hasOwn(notices, value);
}

export function qualityState(raw: string | null | undefined): QualityState {
  if (raw === 'zero' || raw === 'unavailable' || raw === 'long') return raw;
  return raw && isNoticeState(raw) ? raw : 'default';
}

export function noticeFor(state: QualityState): Notice | null {
  return isNoticeState(state) ? notices[state] : null;
}

export function showsSnapshot(state: QualityState): boolean {
  return [
    'default',
    'error',
    '503',
    'loading',
    'partial',
    'stale',
    'zero',
    'unavailable',
    'long',
    'range',
  ].includes(state);
}
