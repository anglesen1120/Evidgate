import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import type { EvidenceRevision } from '@evidgate/sdk';
import type { ApiError } from '../../../../core/http/api-error';
import {
  MissingReasonList,
  type MissingReason,
} from '../../../../shared/ui/annotations/missing-reason-list.component';
import { ErrorPatternComponent } from '../../../../shared/ui/states/error-pattern.component';
import { LoadingPatternComponent } from '../../../../shared/ui/states/loading-pattern.component';
import { EvidenceBadgeComponent } from '../../../../shared/ui/verdict/evidence-badge.component';
import type { Completeness } from '../../../../shared/ui/verdict/verdict-values';
import type { DecisionViewModel } from '../decision/decision-view-model';
import type { RevisionsStatus } from '../revision-walk';
import { EvidenceRevisionSelectorComponent } from './evidence-revision-selector.component';
import { missingReasonEntries, type ConfirmedMissingTarget } from './missing-reasons';

/**
 * Evidence region for one pinned revision: the immutable revision's completeness, its exact
 * `missing[]` literals and its evaluation reference. Everything rendered is read from the accepted
 * decision body or from one revision row of the list; the panel never computes a verdict, a
 * completeness value or a reason, never invents a revision and never changes the pin itself.
 */
@Component({
  selector: 'app-evidence-panel',
  imports: [
    EvidenceBadgeComponent,
    EvidenceRevisionSelectorComponent,
    LoadingPatternComponent,
    ErrorPatternComponent,
    MissingReasonList,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './evidence-panel.component.html',
  styleUrl: './evidence-panel.component.css',
})
export class EvidencePanelComponent {
  readonly decision = input.required<DecisionViewModel>();
  /** The revision the URL/view is pinned to; `null` until a pin is chosen. */
  readonly pinnedRevision = input<number | null>(null);
  /** The loaded list row for that pin, or `null` while it is not on a loaded page. */
  readonly pinned = input<EvidenceRevision | null>(null);
  readonly revisions = input<readonly EvidenceRevision[] | null>(null);
  readonly revisionsStatus = input<RevisionsStatus>('idle');
  readonly revisionsError = input<ApiError | null>(null);
  readonly revisionsHasMore = input(false);
  readonly revisionsLoadingMore = input(false);
  /** A known revision above the pin: the banner offers the newest one, never an automatic jump. */
  readonly newerRevisionExists = input(false);
  readonly latestRevisionNo = input<number | null>(null);
  readonly retryEligible = input(false);
  /**
   * Relations the caller confirmed from run data for the *current* collection. A historical pin is
   * not that collection, so its literals get no target links.
   */
  readonly confirmedTargets = input<readonly ConfirmedMissingTarget[]>([]);
  readonly selectRevision = output<number>();
  readonly loadOlder = output<void>();
  readonly viewLatest = output<void>();
  readonly retry = output<void>();

  protected readonly copy = {
    title: $localize`:@@runs.evidence.title:Evidence`,
    loading: $localize`:@@runs.evidence.loading:Loading evidence revisions`,
    emptyTitle: $localize`:@@runs.evidence.empty-title:No frozen evidence revision yet`,
    emptyDescription: $localize`:@@runs.evidence.empty-description:Collection may still be in progress. No evidence verdict is inferred.`,
    searchOlder: $localize`:@@runs.evidence.search-older:This revision is not in the loaded pages. Load older revisions to look for it.`,
    selectedUnavailable: $localize`:@@runs.evidence.selected-unavailable:Selected revision unavailable`,
    newerTitle: $localize`:@@runs.evidence.newer-title:A newer revision exists`,
    viewLatest: $localize`:@@runs.evidence.view-latest:View latest revision`,
    missingTitle: $localize`:@@runs.evidence.missing-title:Missing evidence reasons`,
    missingUnavailable: $localize`:@@runs.evidence.missing-unavailable:Missing evidence reasons unavailable`,
    evaluationReference: $localize`:@@runs.evidence.evaluation-reference:Live evaluation reference`,
    noEvaluation: $localize`:@@runs.evidence.no-evaluation:No live evaluation recorded for this revision`,
    historicalDetails: $localize`:@@runs.evidence.historical-details:Historical decision details are not available from this reference.`,
  };

  /**
   * True only while the pin is the revision the live decision was evaluated at: the decision body
   * is then the authoritative data for this view, and no list row is needed for it.
   */
  protected readonly decisionBound = computed(() => {
    const decision = this.decision();
    const pinned = this.pinnedRevision();
    return (
      decision.kind === 'evaluated' && (pinned === null || pinned === decision.evidenceRevision)
    );
  });

  /** The pin names an older revision than the live decision's: decision data must not be reused. */
  protected readonly historical = computed(() => {
    const decision = this.decision();
    const pinned = this.pinnedRevision();
    return decision.kind === 'evaluated' && pinned !== null && pinned !== decision.evidenceRevision;
  });

  /** The bound revision's completeness, or neutral when no authoritative value is loaded. */
  protected readonly completeness = computed<Completeness | null>(() => {
    if (this.decisionBound()) {
      const decision = this.decision();
      return decision.kind === 'evaluated' ? decision.completeness : null;
    }
    return this.pinned()?.completeness ?? null;
  });

  /**
   * The literals only when an explicit array exists. The list is authoritative for a historical
   * pin, and `[]` is legitimate only for a complete revision: an incomplete row without reasons is
   * a contract inconsistency, not "nothing missing", so it stays unavailable.
   */
  protected readonly missing = computed<readonly string[] | null>(() => {
    if (this.decisionBound()) {
      const decision = this.decision();
      return decision.kind === 'evaluated' && Array.isArray(decision.missing)
        ? decision.missing
        : null;
    }
    const pinned = this.pinned();
    if (pinned === null) return null;
    return pinned.completeness === 'complete' || pinned.missing.length > 0 ? pinned.missing : null;
  });

  protected readonly reasons = computed<readonly MissingReason[]>(() =>
    missingReasonEntries(this.missing() ?? [], this.historical() ? [] : this.confirmedTargets()),
  );

  /** A successful empty page: nothing frozen yet, and no pin to look for. */
  protected readonly noFrozenRevision = computed(
    () =>
      this.revisionsStatus() === 'ready' &&
      (this.revisions()?.length ?? 0) === 0 &&
      this.pinnedRevision() === null &&
      !this.decisionBound(),
  );

  protected readonly showSelector = computed(
    () => this.revisionsStatus() === 'ready' && (this.revisions()?.length ?? 0) > 0,
  );

  /** The pin is not on a loaded page: more pages mean "look older", none means unavailable. */
  private readonly pinNotLoaded = computed(
    () => this.pinnedRevision() !== null && this.pinned() === null,
  );

  protected readonly searchOlder = computed(() => this.pinNotLoaded() && this.revisionsHasMore());

  protected readonly pinUnavailable = computed(
    () => this.pinNotLoaded() && !this.revisionsHasMore() && this.revisionsStatus() === 'ready',
  );

  protected selectedLabel(revision: number): string {
    return $localize`:@@runs.evidence.selected-revision:Selected evidence revision: ${revision}:revision:`;
  }

  protected newerDescription(): string {
    const selected = this.pinnedRevision();
    const latest = this.latestRevisionNo();
    if (selected === null || latest === null) return '';
    return $localize`:@@runs.evidence.newer-description:You are viewing revision ${selected}:selected:. The newest revision is ${latest}:latest:.`;
  }
}
