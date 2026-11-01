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
import { ActivatedRoute, RouterLink } from '@angular/router';
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
  private runId: string | null = null;
  protected readonly retryEligible = signal(false);
  /** The decision region's own retry window: a decision failure never gates the run's retry. */
  protected readonly decisionRetryEligible = signal(false);
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
  protected readonly copy = {
    title: $localize`:@@runs.detail.title:Run detail`,
    back: $localize`:@@runs.detail.back:Back to runs`,
  };
  constructor() {
    this.route.paramMap.pipe(takeUntilDestroyed()).subscribe((params) => {
      const runId = params.get('runId');
      if (runId && runId !== this.runId) {
        this.runId = runId;
        void this.store.load(runId);
      }
    });
    this.watchRetryWindow(this.store.loadError, this.retryEligible);
    this.watchRetryWindow(this.store.decisionError, this.decisionRetryEligible);
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
  protected reload(): void {
    if (this.runId && this.retryEligible()) void this.store.load(this.runId);
  }
}
