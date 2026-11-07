// The run and evidence half of the catalog: runs, attempts, decisions, evidence revisions and
// suite revisions. `fixture-catalog-entries.ts` concatenates both halves, so the served order is
// the original single list.

import runAttempts from '../../../../../../fixtures/web-api/run-attempts/input.json';
import runAttemptsEmpty from '../../../../../../fixtures/web-api/run-attempts-empty/input.json';
import runAttemptsPage1 from '../../../../../../fixtures/web-api/run-attempts-page-1/input.json';
import runAttemptsPage2 from '../../../../../../fixtures/web-api/run-attempts-page-2/input.json';
import runAttemptsUnavailable from '../../../../../../fixtures/web-api/run-attempts-unavailable/input.json';
import runDecision from '../../../../../../fixtures/web-api/run-decision/input.json';
import runDecisionEvaluatedFailMissingShard from '../../../../../../fixtures/web-api/run-decision-evaluated-fail-missing-shard/input.json';
import runDecisionEvaluatedPass from '../../../../../../fixtures/web-api/run-decision-evaluated-pass/input.json';
import runDecisionShadowRefused from '../../../../../../fixtures/web-api/run-decision-shadow-refused/input.json';
import runDecisionUnavailable from '../../../../../../fixtures/web-api/run-decision-unavailable/input.json';
import runEvidenceRevisions from '../../../../../../fixtures/web-api/run-evidence-revisions/input.json';
import runEvidenceRevisionsEmpty from '../../../../../../fixtures/web-api/run-evidence-revisions-empty/input.json';
import runEvidenceRevisionsUnavailable from '../../../../../../fixtures/web-api/run-evidence-revisions-unavailable/input.json';
import runDetail from '../../../../../../fixtures/web-api/run-detail/input.json';
import runDetailForbidden from '../../../../../../fixtures/web-api/run-detail-forbidden/input.json';
import runDetailNotFound from '../../../../../../fixtures/web-api/run-detail-not-found/input.json';
import runDetailNullEvidence from '../../../../../../fixtures/web-api/run-detail-null-evidence/input.json';
import runDetailRefFormat from '../../../../../../fixtures/web-api/run-detail-ref-format/input.json';
import runList from '../../../../../../fixtures/web-api/run-list/input.json';
import runList250 from '../../../../../../fixtures/web-api/run-list-250/input.json';
import runListEmpty from '../../../../../../fixtures/web-api/run-list-empty/input.json';
import runListNomatchPages from '../../../../../../fixtures/web-api/run-list-nomatch-pages/input.json';
import runListPage1 from '../../../../../../fixtures/web-api/run-list-page-1/input.json';
import runListPage1Full from '../../../../../../fixtures/web-api/run-list-page-1-full/input.json';
import runListPage2 from '../../../../../../fixtures/web-api/run-list-page-2/input.json';
import runListUnavailable from '../../../../../../fixtures/web-api/run-list-unavailable/input.json';
import runState from '../../../../../../fixtures/web-api/run-state/input.json';
import runStateOnboardingEvidence from '../../../../../../fixtures/web-api/run-state-onboarding-evidence/input.json';
import runStateOnboardingRunComplete from '../../../../../../fixtures/web-api/run-state-onboarding-run-complete/input.json';
import runsListForbidden from '../../../../../../fixtures/web-api/runs-list-forbidden/input.json';
import runsList from '../../../../../../fixtures/web-api/runs-list/input.json';
import runsListOnboardingConnected from '../../../../../../fixtures/web-api/runs-list-onboarding-connected/input.json';
import runsListOnboardingProject from '../../../../../../fixtures/web-api/runs-list-onboarding-project/input.json';
import runsListOnboardingRun from '../../../../../../fixtures/web-api/runs-list-onboarding-run/input.json';
import suiteRevision from '../../../../../../fixtures/web-api/suite-revision/input.json';
import suiteRevisionEmpty from '../../../../../../fixtures/web-api/suite-revision-empty/input.json';
import suiteRevisionForbidden from '../../../../../../fixtures/web-api/suite-revision-forbidden/input.json';
import suiteRevisionLarge from '../../../../../../fixtures/web-api/suite-revision-large/input.json';
import suiteRevisionNotFound from '../../../../../../fixtures/web-api/suite-revision-not-found/input.json';
import suiteRevisionPublished from '../../../../../../fixtures/web-api/suite-revision-published/input.json';
import suiteRevisionUnavailable from '../../../../../../fixtures/web-api/suite-revision-unavailable/input.json';

import type { FixtureCatalogRow, FixtureDocument } from './fixture-document';

/**
 * The canceled run: `GET /v1/runs/{r}` serves the state, and the timeline's after-cancel note is
 * only reachable on a canceled run, but the corpus carries no canceled detail document. One field
 * of the validated `run-detail` body is replaced, so this state cannot drift from the response
 * shape the contract check already proves.
 */
const runDetailCanceled: FixtureDocument = {
  ...runDetail,
  variant: 'detail-canceled',
  body: { ...runDetail.body, state: 'canceled' },
};

export const FIXTURE_CATALOG_RUN_ROWS: readonly FixtureCatalogRow[] = [
  { name: 'run-attempts', document: runAttempts },
  { name: 'run-attempts-empty', document: runAttemptsEmpty },
  { name: 'run-attempts-page-1', document: runAttemptsPage1 },
  {
    name: 'run-attempts-page-2',
    document: runAttemptsPage2,
    cursor: 'attempts-cursor-after-checkout-2',
  },
  { name: 'run-attempts-unavailable', document: runAttemptsUnavailable },
  { name: 'run-decision', document: runDecision },
  {
    name: 'run-decision-evaluated-fail-missing-shard',
    document: runDecisionEvaluatedFailMissingShard,
  },
  { name: 'run-decision-evaluated-pass', document: runDecisionEvaluatedPass },
  { name: 'run-decision-shadow-refused', document: runDecisionShadowRefused },
  { name: 'run-decision-unavailable', document: runDecisionUnavailable },
  { name: 'run-detail', document: runDetail },
  { name: 'run-detail-canceled', document: runDetailCanceled, derived: true },
  { name: 'run-detail-forbidden', document: runDetailForbidden },
  { name: 'run-detail-not-found', document: runDetailNotFound },
  { name: 'run-detail-null-evidence', document: runDetailNullEvidence },
  { name: 'run-detail-ref-format', document: runDetailRefFormat },
  { name: 'run-evidence-revisions', document: runEvidenceRevisions },
  { name: 'run-evidence-revisions-empty', document: runEvidenceRevisionsEmpty },
  { name: 'run-evidence-revisions-unavailable', document: runEvidenceRevisionsUnavailable },
  { name: 'run-list', document: runList },
  { name: 'run-list-250', document: runList250 },
  { name: 'run-list-empty', document: runListEmpty },
  { name: 'run-list-nomatch-pages', document: runListNomatchPages },
  { name: 'run-list-page-1', document: runListPage1 },
  { name: 'run-list-page-1-full', document: runListPage1Full },
  {
    name: 'run-list-page-2',
    document: runListPage2,
    cursor: '08df2f51afe86800-018f0000-0000-7000-8000-000000000083',
  },
  { name: 'run-list-unavailable', document: runListUnavailable },
  { name: 'run-state', document: runState },
  { name: 'run-state-onboarding-evidence', document: runStateOnboardingEvidence },
  { name: 'run-state-onboarding-run-complete', document: runStateOnboardingRunComplete },
  { name: 'runs-list', document: runsList },
  { name: 'runs-list-forbidden', document: runsListForbidden },
  { name: 'runs-list-onboarding-connected', document: runsListOnboardingConnected },
  { name: 'runs-list-onboarding-project', document: runsListOnboardingProject },
  { name: 'runs-list-onboarding-run', document: runsListOnboardingRun },
  { name: 'suite-revision', document: suiteRevision },
  { name: 'suite-revision-empty', document: suiteRevisionEmpty },
  { name: 'suite-revision-forbidden', document: suiteRevisionForbidden },
  { name: 'suite-revision-large', document: suiteRevisionLarge },
  { name: 'suite-revision-not-found', document: suiteRevisionNotFound },
  { name: 'suite-revision-published', document: suiteRevisionPublished },
  { name: 'suite-revision-unavailable', document: suiteRevisionUnavailable },
];
