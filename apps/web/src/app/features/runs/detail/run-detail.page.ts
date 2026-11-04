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
  signal,
  type Signal,
  type WritableSignal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { combineLatest } from 'rxjs';
import type { ApiError } from '../../../core/http/api-error';
import { RequestStateComponent } from '../../../shared/ui/states/request-state.component';
import { AttemptsTableComponent } from './attempts/attempts-table.component';
import { DecisionViewComponent } from './decision/decision-view.component';
import { EvidencePanelComponent } from './evidence/evidence-panel.component';
import { resolveEvidenceAnchor } from './evidence/run-evidence-anchors';
import { ManifestPanelComponent } from './manifest-panel.component';
import { parsePinnedRevision } from './revision-pin';
import { RunHeaderComponent } from './run-header.component';
import { RunDetailStore } from './run-detail.store';

@Component({
  selector: 'app-run-detail-page',
  imports: [
    RouterLink,
    RequestStateComponent,
    RunHeaderComponent,
    ManifestPanelComponent,
    EvidencePanelComponent,
    DecisionViewComponent,
    AttemptsTableComponent,
  ],
  providers: [RunDetailStore],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './run-detail.page.html',
  styleUrl: './run-detail.page.css',
  host: { '[attr.data-run-detail-status]': 'store.status()' },
})
export class RunDetailPage {
  protected readonly store = inject(RunDetailStore);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly document = inject(DOCUMENT);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);
  private runId: string | null = null;
  /** The fragment this view was opened (or navigated) with; the landing target lives there. */
  private readonly fragment = signal<string | null>(null);
  /** The fragment landed once: a polling update never moves focus a second time. */
  private landed = false;
  protected readonly retryEligible = signal(false);
  /** The decision region's own retry window: a decision failure never gates the run's retry. */
  protected readonly decisionRetryEligible = signal(false);
  /** The revision list's own retry window: its failure never gates the decision or attempts. */
  protected readonly revisionsRetryEligible = signal(false);
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
  protected readonly copy = {
    title: $localize`:@@runs.detail.title:Run detail`,
    back: $localize`:@@runs.detail.back:Back to runs`,
  };
  constructor() {
    this.route.fragment.pipe(takeUntilDestroyed()).subscribe((fragment) => {
      this.fragment.set(fragment);
      this.landed = false;
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
    // Initial-fragment landing: `#attempt-…`/`#producer-…` names a row the attempts table renders
    // only after its first page lands, so resolution waits for that state. A target on a page the
    // visitor has not loaded yet is left alone: the landing retries after a visitor-requested page
    // arrives and never paginates on its own. One landing per fragment, so a poll never moves focus.
    effect(() => {
      const fragment = this.fragment();
      const attempts = this.store.attempts();
      if (fragment === null || attempts === null || this.landed) return;
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
    // The mismatch notice disappears once the pin matches the decision, taking its action with it.
    this.handoffEvidenceFocus('[data-decision-mismatch] .decision-mismatch__action');
  }
  protected showEvidence(): void {
    const target = this.host.nativeElement.querySelector<HTMLElement>('#evidence-title');
    if (target) this.focusEvidenceTarget(target);
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
