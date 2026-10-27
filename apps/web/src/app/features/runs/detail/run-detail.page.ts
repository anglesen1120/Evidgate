import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { RequestStateComponent } from '../../../shared/ui/states/request-state.component';
import { AttemptsTableComponent } from './attempts/attempts-table.component';
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
  protected readonly requestStatus = computed<'loading' | 'ready' | 'error'>(() => {
    const status = this.store.status();
    return status === 'idle' || status === 'loading'
      ? 'loading'
      : status === 'ready'
        ? 'ready'
        : 'error';
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
    effect((onCleanup) => {
      const error = this.store.loadError();
      const seconds = error?.retryAfterSeconds;
      this.retryEligible.set(Boolean(error?.retryable) && (seconds == null || seconds <= 0));
      if (error?.retryable && seconds != null && seconds > 0) {
        const timer = setTimeout(() => this.retryEligible.set(true), seconds * 1000);
        onCleanup(() => clearTimeout(timer));
      }
    });
  }
  protected reload(): void {
    if (this.runId && this.retryEligible()) void this.store.load(this.runId);
  }
}
