import { DOCUMENT } from '@angular/common';
import {
  afterNextRender,
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  ElementRef,
  inject,
  Injector,
  linkedSignal,
  signal,
  type Signal,
  type WritableSignal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { HlmButton } from '@evidgate/ui/button';
import { HlmTabsImports } from '@evidgate/ui/tabs';
import { combineLatest } from 'rxjs';
import type { ApiError } from '../../../core/http/api-error';
import { PageHeaderImports } from '../../../shared/ui/page-header';
import { RequestStateComponent } from '../../../shared/ui/states/request-state.component';
import { AttemptsTableComponent } from './attempts/attempts-table.component';
import { DecisionViewComponent } from './decision/decision-view.component';
import { EvidencePanelComponent } from './evidence/evidence-panel.component';
import { decodeEvidenceAnchor, resolveEvidenceAnchor } from './evidence/run-evidence-anchors';
import { ManifestPanelComponent } from './manifest-panel.component';
import { producerSlotsFromRun } from './producers/producer-slots';
import { ProducerStatusTableComponent } from './producers/producer-status-table.component';
import { ReleasePanelComponent } from './release/release-panel.component';
import { releaseFromDetail } from './release/release-summary';
import { parsePinnedRevision } from './revision-pin';
import { RunHeaderComponent } from './run-header.component';
import { RunDetailStore } from './run-detail.store';
import { RunTimelineComponent } from './timeline/run-timeline.component';

@Component({
  selector: 'app-run-detail-page',
  imports: [
    RouterLink,
    RequestStateComponent,
    RunHeaderComponent,
    ManifestPanelComponent,
    ProducerStatusTableComponent,
    ReleasePanelComponent,
    RunTimelineComponent,
    EvidencePanelComponent,
    DecisionViewComponent,
    AttemptsTableComponent,
    HlmButton,
    HlmTabsImports,
    PageHeaderImports,
  ],
  providers: [RunDetailStore],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './run-detail.page.html',
  host: {
    '[attr.data-run-detail-status]': 'store.status()',
    class:
      'block min-w-0 font-sans text-sm leading-(--eg-leading-sm) text-foreground wrap-anywhere',
  },
})
export class RunDetailPage {
  protected readonly store = inject(RunDetailStore);

  private readonly route = inject(ActivatedRoute);

  private readonly router = inject(Router);

  private readonly document = inject(DOCUMENT);

  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  private readonly injector = inject(Injector);

  protected readonly detailTab = signal('evidence');
  protected readonly producerSlots = computed(() => {
    const run = this.store.run();
    return run === null ? null : producerSlotsFromRun(run);
  });
  protected readonly release = computed(() => {
    const release = this.store.release();
    return release === null ? null : releaseFromDetail(release);
  });
  protected readonly releases = computed(() => {
    const seen = new Set<string>();
    return (this.store.run()?.releases ?? [])
      .filter((binding) => {
        if (seen.has(binding.release_id)) return false;
        seen.add(binding.release_id);
        return true;
      })
      .map((binding) => ({
        releaseId: binding.release_id,
        name: binding.release_name ?? null,
      }));
  });
  private runId: string | null = null;
  /** The fragment this view was opened (or navigated) with; the landing target lives there. */
  private readonly fragment = signal<string | null>(null);
  protected readonly producerTargetAnchor = computed(() =>
    decodeEvidenceAnchor(this.fragment() ?? ''),
  );
  /** The fragment landed once: a polling update never moves focus a second time. */
  private landed = false;
  protected readonly retryEligible = signal(false);
  /** The decision region's own retry window: a decision failure never gates the run's retry. */
  protected readonly decisionRetryEligible = signal(false);
  /** The revision list's own retry window: its failure never gates the decision or attempts. */
  protected readonly revisionsRetryEligible = signal(false);
  protected readonly releaseRetryEligible = signal(false);
  protected readonly requestStatus = computed<'loading' | 'ready' | 'error'>(() => {
    const status = this.store.status();
    return status === 'idle' || status === 'loading'
      ? 'loading'
      : status === 'ready'
        ? 'ready'
        : 'error';
  });
  protected readonly attemptsStatus = computed<'loading' | 'ready' | 'error'>(() => {
    const status = this.store.attemptsStatus();
    return status === 'idle' || status === 'loading' ? 'loading' : status;
  });
  /** The revision list renders its own loading state; `idle` is the same thing before the read. */
  protected readonly revisionsStatus = computed<'loading' | 'ready' | 'error'>(() => {
    const status = this.store.revisionsStatus();
    return status === 'idle' || status === 'loading' ? 'loading' : status;
  });
  /**
   * The evidence update announced to assistive tech. The region that renders this lives outside the
   * tab panels, so a change is still spoken while the Evidence tab is hidden; the first paint sets
   * the baseline silently and an unchanged state stays silent, so polling never repeats a message.
   */
  private readonly announcedEvidence = linkedSignal<
    { readonly selected: number | null; readonly newer: number | null },
    string | null
  >({
    source: () => ({
      selected: this.store.pinnedRevision(),
      newer: this.store.newerRevisionExists() ? this.store.latestRevisionNo() : null,
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

  protected readonly evidenceAnnouncement = computed(() => this.announcedEvidence() ?? '');

  protected readonly copy = {
    title: $localize`:@@runs.detail.title:Run detail`,
    sections: $localize`:@@runs.detail.sections:Run sections`,
    back: $localize`:@@runs.detail.back:Back to runs`,
    evidence: $localize`:@@runs.evidence.title:Evidence`,
    decision: $localize`:@@runs.decision.title:Latest live decision`,
    decisionTab: $localize`:@@runs.detail.decision-tab-compact:Live decision`,
  };
  constructor() {
    this.route.fragment.pipe(takeUntilDestroyed()).subscribe((fragment) => {
      this.fragment.set(fragment);
      this.landed = false;
      if (fragment?.startsWith('producer-')) this.detailTab.set('evidence');
    });
    combineLatest([this.route.paramMap, this.route.queryParamMap])
      .pipe(takeUntilDestroyed())
      .subscribe(([params, query]) => {
        const runId = params.get('runId');
        if (runId && runId !== this.runId) {
          this.runId = runId;
          void this.store.load(runId);
        }
        this.store.applyUrlPin(parsePinnedRevision(query.get('evidence_revision')));
      });
    // The pin is committed to the URL with replacement navigation, so Back/Forward restores it and
    // the history stays one entry per view. Unrelated parameters and the fragment are preserved.
    effect(() => {
      const revision = this.store.pinnedRevision();
      if (revision === null) return;
      const current = parsePinnedRevision(
        this.route.snapshot.queryParamMap.get('evidence_revision'),
      );
      if (revision === current) return;
      void this.router.navigate([], {
        relativeTo: this.route,
        queryParams: { evidence_revision: revision },
        queryParamsHandling: 'merge',
        replaceUrl: true,
        preserveFragment: true,
      });
    });
    this.watchRetryWindow(this.store.loadError, this.retryEligible);
    this.watchRetryWindow(this.store.decisionError, this.decisionRetryEligible);
    this.watchRetryWindow(this.store.revisionsError, this.revisionsRetryEligible);
    this.watchRetryWindow(this.store.releaseError, this.releaseRetryEligible);
    // Producer anchors depend on the run declaration, not a successful attempts read.
    // Unloaded attempt targets retry only when their requested page arrives.
    effect(() => {
      const fragment = this.fragment();
      const run = this.store.run();
      const attempts = this.store.attempts();
      const anchor = decodeEvidenceAnchor(fragment ?? '');
      if (fragment === null || anchor === null || run === null || this.landed) return;
      if (anchor.startsWith('attempt-') && attempts === null) return;
      afterNextRender(
        () => {
          if (this.landed || this.fragment() !== fragment) return;
          const target = resolveEvidenceAnchor(fragment, this.document);
          if (target === null) return;
          this.landed = true;
          this.focusEvidenceTarget(target);
        },
        { injector: this.injector },
      );
    });
  }
  /**
   * A failure opens its retry control at once, or after the answer's own `Retry-After` window. One
   * timer per failure, never a poll; a new failure replaces the previous window.
   */
  private watchRetryWindow(
    failureSource: Signal<ApiError | null>,
    eligible: WritableSignal<boolean>,
  ): void {
    effect((onCleanup) => {
      const failure = failureSource();
      const seconds = failure?.retryAfterSeconds;
      eligible.set(Boolean(failure?.retryable) && (seconds == null || seconds <= 0));
      if (failure?.retryable && seconds != null && seconds > 0) {
        const timer = setTimeout(() => eligible.set(true), seconds * 1000);
        onCleanup(() => clearTimeout(timer));
      }
    });
  }
  protected retryRelease(): void {
    const selected = this.store.selectedReleaseId();
    if (selected !== null) void this.store.loadRelease(selected);
  }
  protected retryAttempts(): void {
    void this.store.retryAttempts();
  }
  protected loadMoreAttempts(): void {
    void this.store.loadMoreAttempts();
  }
  protected retryDecision(): void {
    if (this.decisionRetryEligible()) void this.store.refreshDecision();
  }
  protected selectRevision(revision: number): void {
    this.store.pinRevision(revision);
  }
  protected viewLatestRevision(): void {
    this.store.pinLatestRevision();
    // Pinning the newest revision removes the banner that held the action; without a handoff the
    // visitor's place would fall to `body`. The evidence heading receives it once the banner is gone.
    this.handoffEvidenceFocus('[data-evidence-newer] .evidence-newer-action');
  }
  /** The decision region's mismatch action pins the revision that decision was evaluated at. */
  protected viewDecisionEvidence(revision: number): void {
    this.store.pinRevision(revision);
    this.showEvidence();
  }
  protected showEvidence(): void {
    const focus = () => {
      const target = this.host.nativeElement.querySelector<HTMLElement>('#evidence-title');
      if (target) this.focusEvidenceTarget(target);
    };
    if (this.detailTab() === 'evidence') {
      focus();
      return;
    }
    this.detailTab.set('evidence');
    // The destination must be visible before focus leaves the now-hidden decision panel.
    afterNextRender(focus, { injector: this.injector });
  }
  /**
   * Moves focus to the evidence heading after the render that removed `controlSelector`, and only
   * when the control is actually gone: a control that still answers keeps the focus it had.
   */
  private handoffEvidenceFocus(controlSelector: string): void {
    afterNextRender(
      () => {
        const host: HTMLElement = this.host.nativeElement;
        if (host.querySelector(controlSelector) !== null) return;
        host.querySelector<HTMLElement>('#evidence-title')?.focus();
      },
      { injector: this.injector },
    );
  }
  /** Scrolls a landing target clear of the sticky header, then focuses it without a second scroll. */
  private focusEvidenceTarget(target: HTMLElement): void {
    target.scrollIntoView?.({ block: 'start', inline: 'nearest' });
    target.focus({ preventScroll: true });
  }
  protected loadOlderRevisions(): void {
    void this.store.loadOlderRevisions();
  }
  protected retryRevisions(): void {
    if (this.revisionsRetryEligible()) void this.store.retryRevisions();
  }
  protected reload(): void {
    if (this.runId === null || !this.retryEligible()) return;
    void this.store.load(this.runId);
    // `load` clears the pin like a route change does, but this route's query does not change on a
    // retry, so the URL's own pin is reapplied here rather than waiting for an emission.
    this.store.applyUrlPin(
      parsePinnedRevision(this.route.snapshot.queryParamMap.get('evidence_revision')),
    );
  }
}
