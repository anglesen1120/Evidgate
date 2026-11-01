import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
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
import { ManifestPanelComponent } from './manifest-panel.component';
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
  private runId: string | null = null;
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
  }
  /** The decision region's mismatch action pins the revision that decision was evaluated at. */
  protected viewDecisionEvidence(revision: number): void {
    this.store.pinRevision(revision);
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

/**
 * The pin is the positive integer server revision number. Anything else — absent, `0`, negative,
 * `ev-3`, an evaluation UUID — is not a pin and is left out of the view rather than coerced.
 */
function parsePinnedRevision(value: string | null): number | null {
  if (value === null || value === '') return null;
  const revision = Number(value);
  return Number.isInteger(revision) && revision > 0 ? revision : null;
}
