import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import {
  MissingReasonList,
  type MissingReason,
} from '../../../../shared/ui/annotations/missing-reason-list.component';
import { EvidenceBadgeComponent } from '../../../../shared/ui/verdict/evidence-badge.component';
import type { Completeness } from '../../../../shared/ui/verdict/verdict-values';
import type { DecisionViewModel } from '../decision/decision-view-model';
import { missingReasonEntries, type ConfirmedMissingTarget } from './missing-reasons';

/**
 * Evidence region: the immutable revision's completeness and the exact `missing[]` literals.
 * Everything rendered here is read from the accepted decision body; the panel never computes a
 * verdict, a completeness value or a reason, and it never names a target on its own.
 */
@Component({
  selector: 'app-evidence-panel',
  imports: [EvidenceBadgeComponent, MissingReasonList],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './evidence-panel.component.html',
  styleUrl: './evidence-panel.component.css',
})
export class EvidencePanelComponent {
  readonly decision = input.required<DecisionViewModel>();
  /**
   * Collection completeness from the run read: the only completeness the badge may show while no
   * live evaluation exists. It never stands in for a decision's own completeness.
   */
  readonly runCompleteness = input<Completeness | null>(null);
  /**
   * Relations the caller confirmed from run data. A reason literal cannot determine its target, so
   * a link exists only for an entry here and every confirmed target is listed.
   */
  readonly confirmedTargets = input<readonly ConfirmedMissingTarget[]>([]);

  protected readonly copy = {
    title: $localize`:@@runs.evidence.title:Evidence`,
    missingTitle: $localize`:@@runs.evidence.missing-title:Missing evidence reasons`,
    missingUnavailable: $localize`:@@runs.evidence.missing-unavailable:Missing evidence reasons unavailable`,
    noEvaluation: $localize`:@@runs.evidence.no-evaluation:No live evaluation recorded for this revision`,
  };

  /**
   * The decision's completeness when evaluated, collection completeness otherwise. No verdict.
   *
   * Only the decision's revision is known here. Once a selected revision can differ from it, the
   * decision's completeness must be shown only when the two match; the collection value then
   * describes the selected revision.
   */
  protected readonly completeness = computed<Completeness | null>(() => {
    const decision = this.decision();
    return decision.kind === 'evaluated' ? decision.completeness : this.runCompleteness();
  });

  /**
   * The literals only when the body carries an explicit array: a pending or unavailable body has
   * no list, so it must not mount the shared list with its default empty array.
   */
  protected readonly missing = computed<readonly string[] | null>(() => {
    const decision = this.decision();
    return decision.kind === 'evaluated' && Array.isArray(decision.missing)
      ? decision.missing
      : null;
  });

  protected readonly reasons = computed<readonly MissingReason[]>(() =>
    missingReasonEntries(this.missing() ?? [], this.confirmedTargets()),
  );
}
