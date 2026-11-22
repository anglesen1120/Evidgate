import type { ReviewNoticeViewModel } from './scripts-review-state';

export const scriptNotices = {
  unavailable: {
    replace: true,
    tone: 'info',
    title: $localize`:@@review.generated-scripts.requested-record:Requested record unavailable`,
    description: $localize`:@@review.generated-scripts.no-record:No matching record in this preview.`,
  },
  loading: {
    replace: true,
    tone: 'info',
    title: $localize`:@@review.generated-scripts.state-loading:Loading`,
    description: $localize`:@@review.generated-scripts.state-loading-detail:Reading the requested project scope.`,
  },
  empty: {
    replace: true,
    tone: 'info',
    title: $localize`:@@review.generated-scripts.state-empty:No generated scripts in this project`,
    description: $localize`:@@review.generated-scripts.state-empty-detail:Scripts appear only after a customer-side generation with an approved case, environment URL and seed test. No script is fabricated here.`,
  },
  error: {
    replace: true,
    tone: 'destructive',
    title: $localize`:@@review.generated-scripts.state-error:Generated scripts could not be loaded`,
    description: $localize`:@@review.generated-scripts.state-error-detail:Retry keeps the supplied testability score and filters. No generation, run or PR action is started.`,
  },
  '403': {
    replace: true,
    tone: 'info',
    title: $localize`:@@review.generated-scripts.state-403:You do not have access to generated scripts`,
    description: $localize`:@@review.generated-scripts.state-403-detail:Case read access is required. Script provenance and step metadata are not disclosed.`,
  },
  '404': {
    replace: true,
    tone: 'info',
    title: $localize`:@@review.generated-scripts.state-404:Script not found`,
    description: $localize`:@@review.generated-scripts.state-404-detail:The script may have moved, or you may not have access. Existence details are not disclosed.`,
  },
  '401': {
    replace: true,
    tone: 'info',
    title: $localize`:@@review.generated-scripts.state-401:Your session expired`,
    description: $localize`:@@review.generated-scripts.state-401-detail:Sign in again. The open drawer, filters and cursor are not submitted automatically.`,
  },
  '429': {
    replace: true,
    tone: 'warning',
    title: $localize`:@@review.generated-scripts.state-429:Too many requests`,
    description: $localize`:@@review.generated-scripts.state-429-detail:The server asked the client to wait. Nothing is retried automatically and the script table stays unchanged.`,
  },
  '412': {
    replace: false,
    tone: 'warning',
    title: $localize`:@@review.generated-scripts.state-412:The script changed while you were working`,
    description: $localize`:@@review.generated-scripts.state-412-detail:This read page demonstrates a linked edit only: base rv-12, current rv-13, selected drawer content retained. Reload is explicit.`,
  },
  nomatch: {
    replace: false,
    tone: 'info',
    title: $localize`:@@review.generated-scripts.state-nomatch:No matching scripts`,
    description: $localize`:@@review.generated-scripts.state-nomatch-detail:Clear the filters to inspect all three scripts.`,
  },
  mixed: {
    replace: false,
    tone: 'warning',
    title: $localize`:@@review.generated-scripts.state-mixed:Mixed builds`,
    description: $localize`:@@review.generated-scripts.state-mixed-detail:One green attempt on b-317, two on b-318. Not 3/3 on one build.`,
  },
  reject: {
    replace: false,
    tone: 'destructive',
    title: $localize`:@@review.generated-scripts.state-reject:Lint rejected`,
    description: $localize`:@@review.generated-scripts.state-reject-detail:Step 02: absolute XPath and force: true were rejected. No policy approval is supplied.`,
  },
  nolint: {
    replace: false,
    tone: 'info',
    title: $localize`:@@review.generated-scripts.state-nolint:Lint unavailable`,
    description: $localize`:@@review.generated-scripts.state-nolint-detail:No result was supplied. Missing lint is not a pass.`,
  },
  seed: {
    replace: false,
    tone: 'warning',
    title: $localize`:@@review.generated-scripts.state-seed:Generation halted · missing seed`,
    description: $localize`:@@review.generated-scripts.state-seed-detail:Approved case and environment URL are present. The seed supplier remains an open contract question.`,
  },
  score: {
    replace: false,
    tone: 'info',
    title: $localize`:@@review.generated-scripts.state-score:Score unavailable`,
    description: $localize`:@@review.generated-scripts.state-score-detail:No measurement was supplied; this is not a score of zero.`,
  },
  closed: {
    replace: false,
    tone: 'info',
    title: $localize`:@@review.generated-scripts.state-closed:Closed PR specimen`,
    description: $localize`:@@review.generated-scripts.state-closed-detail:No PR URL supplied. Repository state does not change a run or the supplied stability.`,
  },
} as const satisfies Readonly<Record<string, ReviewNoticeViewModel>>;

export type GeneratedScriptsState = 'ready' | keyof typeof scriptNotices;
