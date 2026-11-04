// One review address for the run detail page. Four specs open it with their own combination of
// per-operation fixtures, so the route, the run id and the session are spelled once and each caller
// names only the operations it reads. An omitted operation keeps its corpus default, which is why
// the options are named and not positional.
import runDetail from '../../../../fixtures/web-api/run-detail/input.json';
import {
  localePath,
  ORG_ACME,
  PROJECT_STOREFRONT,
  withVariants,
  type ShellLocale,
} from './support';

/** The corpus session the review routes serve: the project picker and the tab strip need it. */
export const RUN_DETAIL_GRANT = '?fixture.variant.listPermissions=settings-policy-readonly';

/** The answers the latest-live-decision region can be asked for. */
export type RunDecisionVariant =
  'ok' | 'evaluated-fail-missing-shard' | 'evaluated-pass' | 'shadow-refused' | 'unavailable';

/** The answers the attempts table can be asked for. */
export type RunAttemptsVariant = 'ok' | 'empty' | 'unavailable';

/** The answers the evidence revision list can be asked for. */
export type RunRevisionsVariant = 'ok' | 'empty' | 'unavailable';

/** The operations whose answer a caller chooses; every other one keeps the corpus default. */
export interface RunDetailAddress {
  readonly locale?: ShellLocale;
  readonly decision?: RunDecisionVariant;
  readonly attempts?: RunAttemptsVariant;
  readonly revisions?: RunRevisionsVariant;
  /** The evidence revision the page pins; omitted, the decision's own revision stays selected. */
  readonly evidenceRevision?: number | undefined;
}

/**
 * The review address of one run. `?evidence_revision` is the first query, so a caller can append a
 * fragment to the result and still name the revision it lands on.
 */
export function runDetailPath(options: RunDetailAddress = {}): string {
  const { locale = 'en', decision, attempts, revisions, evidenceRevision } = options;
  const pin = evidenceRevision === undefined ? '' : `?evidence_revision=${evidenceRevision}`;
  return withVariants(
    localePath(
      locale,
      `/dev/run-detail/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}/runs/${runDetail.body.run_id}${pin}`,
    ),
    [
      RUN_DETAIL_GRANT,
      '?fixture.variant.getRun=detail-ready',
      ...(attempts === undefined ? [] : [`?fixture.variant.listRunAttempts=${attempts}`]),
      ...(decision === undefined ? [] : [`?fixture.variant.getRunDecision=${decision}`]),
      ...(revisions === undefined ? [] : [`?fixture.variant.listEvidenceRevisions=${revisions}`]),
    ],
  );
}
