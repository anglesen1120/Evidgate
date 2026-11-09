// Per-operation fixture choices keep each run-detail scenario on the real page and session.
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

/** Additional run states and bindings derived from the validated run-detail response. */
export type RunStateVariant =
  | 'ready'
  | 'canceled'
  | 'many-producers'
  | 'no-producers'
  | 'slot-states'
  | 'release'
  | 'releases'
  | 'release-refused'
  | 'with-history';

/** The operations whose answer a caller chooses; every other one keeps the corpus default. */
export interface RunDetailAddress {
  readonly locale?: ShellLocale;
  readonly run?: RunStateVariant;
  readonly decision?: RunDecisionVariant;
  readonly attempts?: RunAttemptsVariant;
  readonly revisions?: RunRevisionsVariant;
  readonly release?: 'forbidden' | 'not-found';
  /** The evidence revision the page pins; omitted, the decision's own revision stays selected. */
  readonly evidenceRevision?: number | undefined;
}

/**
 * The review address of one run. `?evidence_revision` is the first query, so a caller can append a
 * fragment to the result and still name the revision it lands on.
 */
export function runDetailPath(options: RunDetailAddress = {}): string {
  const { locale = 'en', run = 'ready', decision, attempts, revisions, evidenceRevision } = options;
  const pin = evidenceRevision === undefined ? '' : `?evidence_revision=${evidenceRevision}`;
  return withVariants(
    localePath(
      locale,
      `/dev/run-detail/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}/runs/${runDetail.body.run_id}${pin}`,
    ),
    [
      RUN_DETAIL_GRANT,
      `?fixture.variant.getRun=detail-${run}`,
      ...(attempts === undefined ? [] : [`?fixture.variant.listRunAttempts=${attempts}`]),
      ...(decision === undefined ? [] : [`?fixture.variant.getRunDecision=${decision}`]),
      ...(revisions === undefined ? [] : [`?fixture.variant.listEvidenceRevisions=${revisions}`]),
      ...(options.release === undefined ? [] : [`?fixture.variant.getRelease=${options.release}`]),
    ],
  );
}
