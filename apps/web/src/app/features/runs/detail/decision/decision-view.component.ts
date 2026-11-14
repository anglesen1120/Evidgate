import {
  ChangeDetectionStrategy,
  Component,
  computed,
  input,
  linkedSignal,
  output,
} from '@angular/core';
import { HlmAlertImports } from '@evidgate/ui/alert';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCardImports } from '@evidgate/ui/card';

import type { ApiError } from '../../../../core/http/api-error';
import { DecisionChipComponent } from '../../../../shared/ui/verdict/decision-chip.component';
import { OutcomePillComponent } from '../../../../shared/ui/verdict/outcome-pill.component';
import { RetryAfterComponent } from '../../../../shared/ui/conflict';
import { ErrorPatternComponent } from '../../../../shared/ui/states/error-pattern.component';
import { ForbiddenPatternComponent } from '../../../../shared/ui/states/forbidden-pattern.component';
import { LoadingPatternComponent } from '../../../../shared/ui/states/loading-pattern.component';
import { NotFoundPatternComponent } from '../../../../shared/ui/states/not-found-pattern.component';
import { patternForError } from '../../../../shared/ui/states/state-pattern';
import type { DecisionViewModel } from './decision-view-model';
import { refusesAccess } from './decision-refusal';
import { runValueLabel } from '../run-value-label';
import { decisionReasonExplanation } from '../reason-explanation';

/**
 * The live-decision region. Every value comes from one accepted body; the component decides how to
 * present a state, never what the state is. A missing or failed read is not `pending`: pending is a
 * served answer, so the loading and error states are local to this region and keep whatever body was
 * already accepted on screen.
 */
@Component({
  selector: 'app-decision-view',
  imports: [
    HlmAlertImports,
    HlmButton,
    HlmCardImports,
    DecisionChipComponent,
    OutcomePillComponent,
    RetryAfterComponent,
    ErrorPatternComponent,
    ForbiddenPatternComponent,
    LoadingPatternComponent,
    NotFoundPatternComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './decision-view.component.html',
  host: { class: 'block min-w-0' },
})
export class DecisionViewComponent {
  /** `null` while the first read is in flight, so the region has a loading state of its own. */
  readonly decision = input<DecisionViewModel | null>(null);
  readonly shadowIgnored = input(false);
  /** A refresh is in flight over an already accepted body. */
  readonly updating = input(false);
  readonly error = input<ApiError | null>(null);
  readonly retryEligible = input(false);
  /** The evidence revision the rest of the page is pinned to, `null` when nothing is pinned. */
  readonly pinnedRevision = input<number | null>(null);
  readonly retry = output<void>();
  /** Asks the page to pin evidence to the revision this decision was evaluated at. */
  readonly viewDecisionEvidence = output<number>();
  protected readonly valueLabel = runValueLabel;
  protected readonly reasonExplanation = decisionReasonExplanation;

  protected readonly copy = {
    title: $localize`:@@runs.decision.title:Latest live decision`,
    loading: $localize`:@@runs.decision.loading:Loading live decision`,
    updating: $localize`:@@runs.decision.updating:Updating decision`,
    pending: $localize`:@@runs.decision.pending-description:No live evaluation is available yet.`,
    unavailable: $localize`:@@runs.decision.unavailable:Live decision unavailable`,
    refreshFailed: $localize`:@@runs.decision.refresh-failed:Decision refresh failed. Previously loaded decision retained.`,
    shadowIgnoredTitle: $localize`:@@runs.decision.shadow-ignored:Shadow evaluation ignored`,
    shadowIgnoredDescription: $localize`:@@runs.decision.shadow-description:A shadow evaluation does not replace the live decision.`,
    reasons: $localize`:@@runs.decision.reasons-title:Decision reasons`,
    noReasons: $localize`:@@runs.decision.no-reasons:No decision reasons reported`,
    status: $localize`:@@runs.decision.status:Evaluation status`,
    evidenceRevision: $localize`:@@runs.decision.evidence-revision:Decision evidence revision`,
    evaluationId: $localize`:@@runs.decision.evaluation-id:Evaluation ID`,
    policyHash: $localize`:@@runs.decision.policy-hash:Policy hash`,
    manifestHash: $localize`:@@runs.decision.manifest-hash:Manifest hash`,
    evaluatorVersion: $localize`:@@runs.decision.evaluator-version:Evaluator version`,
    mode: $localize`:@@runs.decision.mode:Evaluation mode`,
    revisionMismatch: (decisionRevision: number, selectedRevision: number) =>
      $localize`:@@runs.decision.revision-mismatch:This decision was evaluated at evidence revision ${decisionRevision}:DECISION: while the selected evidence revision is ${selectedRevision}:SELECTED:.`,
    viewEvidence: $localize`:@@runs.decision.view-evidence:View decision evidence`,
  };

  protected viewEvidence(): void {
    const decision = this.evaluated();
    if (decision !== null) this.viewDecisionEvidence.emit(decision.evidenceRevision);
  }

  /**
   * The accepted body kept on screen while a refresh runs or fails. An access refusal never keeps
   * a body: its reasons and hashes must not stay readable for a caller the server just refused,
   * so a refusal renders the failure pattern even if a caller passes both inputs.
   */
  protected readonly body = computed(() => {
    const decision = this.decision();
    return decision !== null && decision.kind !== 'unavailable' && !refusesAccess(this.error())
      ? decision
      : null;
  });
  protected readonly pending = computed(() => this.decision()?.kind === 'pending');
  protected readonly unavailable = computed(() => this.decision()?.kind === 'unavailable');
  protected readonly evaluated = computed(() => {
    const decision = this.decision();
    return decision?.kind === 'evaluated' ? decision : null;
  });
  protected readonly loading = computed(() => this.decision() === null && this.error() === null);
  /** No body was ever accepted: the read failure owns the region. */
  protected readonly failure = computed(() => this.body() === null && this.error() !== null);
  /** A refresh failed over an accepted body: the body stays, with a stale-read notice. */
  protected readonly stale = computed(() => this.body() !== null && this.error() !== null);
  /**
   * The decision was evaluated at a revision the page is no longer showing. Only a pinned view can
   * disagree: with no pin the visitor is looking at the live decision, so there is nothing to
   * reconcile and no notice to raise.
   */
  protected readonly revisionMismatch = computed(() => {
    const decision = this.evaluated();
    const pinned = this.pinnedRevision();
    return decision !== null && pinned !== null && pinned !== decision.evidenceRevision
      ? { decisionRevision: decision.evidenceRevision, selectedRevision: pinned }
      : null;
  });
  protected readonly pattern = computed(() => patternForError(this.error()));
  protected readonly retryAfter = computed(() => {
    const failure = this.error();
    return failure?.kind === 'rate_limited' ||
      (failure?.kind === 'unavailable' && failure.retryAfterSeconds !== null)
      ? failure
      : null;
  });

  /**
   * The revision announced after a refresh changed it. The first accepted body sets the baseline
   * silently: an initial paint is not a decision update, and an unchanged poll stays silent.
   */
  protected readonly announcedRevision = linkedSignal<number | null, number | null>({
    source: () => this.evaluated()?.evidenceRevision ?? null,
    computation: (revision, previous) =>
      revision !== null && typeof previous?.source === 'number' && previous.source !== revision
        ? revision
        : null,
  });

  protected readonly announcement = computed(() => {
    const revision = this.announcedRevision();
    return revision === null
      ? null
      : $localize`:@@runs.decision.updated-announcement:Live decision updated for evidence revision ${revision}:REVISION:`;
  });

  protected requestRetry(): void {
    if (this.retryEligible()) this.retry.emit();
  }
}
