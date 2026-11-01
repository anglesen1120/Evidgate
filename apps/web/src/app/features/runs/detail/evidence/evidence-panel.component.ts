import { DOCUMENT, Location } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  linkedSignal,
  output,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { Router } from '@angular/router';
import type { EvidenceRevision } from '@evidgate/sdk';
import { map, startWith } from 'rxjs';
import type { ApiError } from '../../../../core/http/api-error';
import {
  MissingReasonList,
  type MissingReason,
} from '../../../../shared/ui/annotations/missing-reason-list.component';
import { RetryAfterComponent } from '../../../../shared/ui/conflict';
import { ErrorPatternComponent } from '../../../../shared/ui/states/error-pattern.component';
import { LoadingPatternComponent } from '../../../../shared/ui/states/loading-pattern.component';
import { EvidenceBadgeComponent } from '../../../../shared/ui/verdict/evidence-badge.component';
import type { Completeness } from '../../../../shared/ui/verdict/verdict-values';
import type { DecisionViewModel } from '../decision/decision-view-model';
import type { RevisionsStatus } from '../revision-walk';
import { EvidenceRevisionSelectorComponent } from './evidence-revision-selector.component';
import { missingReasonEntries, type ConfirmedMissingTarget } from './missing-reasons';
import { resolveEvidenceAnchor } from './run-evidence-anchors';

/**
 * Evidence region for one pinned revision: the immutable revision's completeness, its exact
 * `missing[]` literals and its evaluation reference. Everything rendered is read from the accepted
 * decision body or from one revision row of the list; the panel never computes a verdict, a
 * completeness value or a reason, never invents a revision and never changes the pin itself.
 *
 * The revision list is this region's own read, so the panel renders whether or not the decision
 * read has answered: `decision` is `null` until it does, and no decision-derived copy is shown
 * from that state.
 */
@Component({
  selector: 'app-evidence-panel',
  imports: [
    EvidenceBadgeComponent,
    EvidenceRevisionSelectorComponent,
    LoadingPatternComponent,
    ErrorPatternComponent,
    RetryAfterComponent,
    MissingReasonList,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '(click)': 'onReasonLinkClick($event)' },
  templateUrl: './evidence-panel.component.html',
  styleUrl: './evidence-panel.component.css',
})
export class EvidencePanelComponent {
  /** `null` while the decision read has not answered: the panel shows its own revision state. */
  readonly decision = input<DecisionViewModel | null>(null);
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

  private readonly document = inject(DOCUMENT);
  private readonly router = inject(Router);
  private readonly location = inject(Location);
  /**
   * The current route URL, qualified with the app's base href. A reason link's `href` is built
   * from it, because a bare `#…` resolves against the base and would navigate to the site root;
   * a localized build serves `/vi/`, and `Router.url` alone drops that prefix.
   */
  private readonly currentUrl = toSignal(
    this.router.events.pipe(
      startWith(null),
      map(() => this.location.prepareExternalUrl(this.router.url)),
    ),
    { initialValue: this.location.prepareExternalUrl(this.router.url) },
  );

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
      decision?.kind === 'evaluated' && (pinned === null || pinned === decision.evidenceRevision)
    );
  });

  /** The pin names an older revision than the live decision's: decision data must not be reused. */
  protected readonly historical = computed(() => {
    const decision = this.decision();
    const pinned = this.pinnedRevision();
    return (
      decision?.kind === 'evaluated' && pinned !== null && pinned !== decision.evidenceRevision
    );
  });

  /** The bound revision's completeness, or neutral when no authoritative value is loaded. */
  protected readonly completeness = computed<Completeness | null>(() => {
    if (this.decisionBound()) {
      const decision = this.decision();
      return decision?.kind === 'evaluated' ? decision.completeness : null;
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
      return decision?.kind === 'evaluated' && Array.isArray(decision.missing)
        ? decision.missing
        : null;
    }
    const pinned = this.pinned();
    if (pinned === null) return null;
    return pinned.completeness === 'complete' || pinned.missing.length > 0 ? pinned.missing : null;
  });

  /**
   * The reason entries, with every target link qualified by the current route path and query. The
   * shared list renders `href` verbatim, so the fragment is completed here: it stays copyable and
   * openable in a new tab, and a plain click is handled in this panel instead.
   */
  protected readonly reasons = computed<readonly MissingReason[]>(() => {
    const base = this.currentUrl().split('#')[0] ?? '';
    const entries = missingReasonEntries(
      this.missing() ?? [],
      this.historical() ? [] : this.confirmedTargets(),
    );
    return entries.map((reason) =>
      reason.href?.startsWith('#') ? { ...reason, href: `${base}${reason.href}` } : reason,
    );
  });

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

  /** A read is in flight: the first page, or an older page over rows already on screen. */
  protected readonly busy = computed(
    () => this.revisionsStatus() === 'loading' || this.revisionsLoadingMore(),
  );

  /** A follow-up page failed over rows that stay readable: its own notice, never the region's. */
  protected readonly continuationFailure = computed(
    () => this.revisionsStatus() === 'ready' && this.revisionsError() !== null,
  );

  /** The failure's own wait window, as the decision region presents it. */
  protected readonly retryAfter = computed(() => {
    const failure = this.revisionsError();
    return failure?.kind === 'rate_limited' ||
      (failure?.kind === 'unavailable' && failure.retryAfterSeconds !== null)
      ? failure
      : null;
  });

  /** The pin is not on a loaded page: more pages mean "look older", none means unavailable. */
  private readonly pinNotLoaded = computed(
    () => this.pinnedRevision() !== null && this.pinned() === null,
  );

  protected readonly searchOlder = computed(() => this.pinNotLoaded() && this.revisionsHasMore());

  protected readonly pinUnavailable = computed(
    () => this.pinNotLoaded() && !this.revisionsHasMore() && this.revisionsStatus() === 'ready',
  );

  /**
   * The selected/newer revision announced after an update. The first paint sets the baseline
   * silently and an unchanged state stays silent, so polling never repeats an announcement.
   */
  private readonly announcedState = linkedSignal<
    { readonly selected: number | null; readonly newer: number | null },
    string | null
  >({
    source: () => ({
      selected: this.pinnedRevision(),
      newer: this.newerRevisionExists() ? this.latestRevisionNo() : null,
    }),
    computation: (state, previous) => {
      const before = previous?.source;
      // A first paint is not an update, and a null pin is not a selection to announce.
      if (before === undefined || before.selected === null || state.selected === null) return null;
      if (before.selected === state.selected && before.newer === state.newer) return null;
      return state.newer === null
        ? $localize`:@@runs.evidence.announcement-selected:Viewing evidence revision ${state.selected}:SELECTED:`
        : $localize`:@@runs.evidence.announcement-newer:Viewing evidence revision ${state.selected}:SELECTED:. Newer revision ${state.newer}:NEWER: is available.`;
    },
  });

  protected readonly announcement = computed(() => this.announcedState() ?? '');

  protected selectedLabel(revision: number): string {
    return $localize`:@@runs.evidence.selected-revision:Selected evidence revision: ${revision}:revision:`;
  }

  protected newerDescription(): string {
    const selected = this.pinnedRevision();
    const latest = this.latestRevisionNo();
    if (selected === null || latest === null) return '';
    return $localize`:@@runs.evidence.newer-description:You are viewing revision ${selected}:selected:. The newest revision is ${latest}:latest:.`;
  }

  /**
   * A reason link is an in-page target, so a plain activation scrolls and focuses the row instead
   * of navigating. Modified activations keep the browser's own behaviour: the qualified `href`
   * opens the same view with the fragment in a new tab.
   */
  protected onReasonLinkClick(event: MouseEvent): void {
    if (event.defaultPrevented || event.button !== 0) return;
    if (event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
    const target = event.target;
    const anchor = target instanceof Element ? target.closest('a.reason-link') : null;
    if (!(anchor instanceof HTMLAnchorElement)) return;
    const href = anchor.getAttribute('href') ?? '';
    const fragment = href.slice(href.indexOf('#'));
    if (!fragment.startsWith('#')) return;
    event.preventDefault();
    const row = resolveEvidenceAnchor(fragment, this.document);
    if (row === null) return;
    row.scrollIntoView?.({ block: 'start', inline: 'nearest' });
    row.focus({ preventScroll: true });
  }
}
