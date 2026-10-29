import { DOCUMENT } from '@angular/common';
import {
  afterNextRender,
  ChangeDetectionStrategy,
  Component,
  computed,
  ElementRef,
  inject,
  Injector,
  input,
  linkedSignal,
  output,
  viewChild,
} from '@angular/core';
import type { ApiError } from '../../../../core/http/api-error';
import {
  StatusPillComponent,
  type StatusTone,
} from '../../../../shared/ui/chips/status-pill.component';
import { EmptyPatternComponent } from '../../../../shared/ui/states/empty-pattern.component';
import { ErrorPatternComponent } from '../../../../shared/ui/states/error-pattern.component';
import { LoadingPatternComponent } from '../../../../shared/ui/states/loading-pattern.component';
import { escapeFormatChars } from '../../list/ref-name-display';
import { groupAttempts, paginateAttemptGroups } from './attempt-groups';
import type { AttemptRow } from './attempt-summary';

@Component({
  selector: 'app-attempts-table',
  imports: [
    StatusPillComponent,
    EmptyPatternComponent,
    ErrorPatternComponent,
    LoadingPatternComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './attempts-table.component.html',
  styleUrl: './attempts-table.component.css',
})
export class AttemptsTableComponent {
  readonly attempts = input.required<AttemptRow[] | null>();
  /** The first page's request state; `ready` with `null` attempts is the unavailable state. */
  readonly status = input<'loading' | 'ready' | 'error'>('ready');
  /** The first page's failure (`status` = error) or the failed follow-up page (`ready`). */
  readonly error = input<ApiError | null>(null);
  /** True while `next_cursor` names a further page of the keyset. */
  readonly hasMore = input(false);
  readonly loadingMore = input(false);
  readonly retry = output<void>();
  readonly loadMore = output<void>();
  /** Every wire row carries `healing_candidate: null` until 08.3.1: shown unavailable, not "none". */
  protected readonly healingUnavailable = computed(
    () => !(this.attempts() ?? []).some((attempt) => attempt.healingCandidate),
  );
  protected readonly display = escapeFormatChars;
  protected readonly pages = computed(() =>
    paginateAttemptGroups(groupAttempts(this.attempts() ?? [])),
  );
  // A follow-up page appends to the same array prefix and keeps the visitor's page; any other
  // change of the input (a new run, a re-read) starts at the first page again.
  protected readonly page = linkedSignal<AttemptRow[] | null, number>({
    source: this.attempts,
    computation: (next, previous) =>
      previous !== undefined &&
      previous.source !== null &&
      next !== null &&
      previous.source.length > 0 &&
      next.length > previous.source.length &&
      next[0] === previous.source[0]
        ? previous.value
        : 0,
  });
  protected readonly visibleGroups = computed(() => this.pages()[this.page()] ?? []);
  protected readonly copy = {
    title: $localize`:@@runs.detail.attempts.title:Attempts`,
    scrollRegion: $localize`:@@runs.detail.attempts.scroll-region:Attempts table`,
    pagination: $localize`:@@runs.detail.attempts.pagination:Attempts pagination`,
    execution: $localize`:@@runs.detail.attempts.execution:Execution`,
    attempt: $localize`:@@runs.detail.attempts.attempt:Attempt`,
    raw: $localize`:@@runs.detail.attempts.raw:Raw status`,
    normalised: $localize`:@@runs.detail.attempts.normalised:Normalised status`,
    duration: $localize`:@@runs.detail.attempts.duration:Duration`,
    artifacts: $localize`:@@runs.detail.attempts.artifacts:Artifacts`,
    healing: $localize`:@@runs.detail.attempts.healing-candidates:Healing candidates`,
    more: $localize`:@@runs.detail.attempts.more:Load more attempts`,
    loadingMore: $localize`:@@runs.detail.attempts.loading-more:Loading more attempts`,
    empty: $localize`:@@runs.detail.attempts.empty:No attempts recorded`,
    preHealing: $localize`:@@runs.detail.attempts.pre-healing:Pre-healing failure`,
    candidate: $localize`:@@runs.detail.attempts.candidate:Healing candidate`,
    shadow: $localize`:@@runs.detail.attempts.shadow:Shadow evaluation`,
    official: $localize`:@@runs.detail.attempts.official:Official evaluation`,
    page: $localize`:@@runs.detail.attempts.page:Page`,
    previous: $localize`:@@runs.detail.attempts.previous:Previous page`,
    next: $localize`:@@runs.detail.attempts.next:Next page`,
  };
  private readonly document = inject(DOCUMENT);
  private readonly injector = inject(Injector);
  private readonly previousButton = viewChild<ElementRef<HTMLButtonElement>>('previous');
  private readonly nextButton = viewChild<ElementRef<HTMLButtonElement>>('next');

  protected tone(status: string): StatusTone {
    switch (status) {
      case 'passed':
        return 'success';
      case 'failed':
      case 'timed_out':
      case 'error':
        return 'danger';
      case 'blocked':
        return 'warning';
      default:
        return 'neutral';
    }
  }

  protected knownStatus(status: string): boolean {
    return ['passed', 'failed', 'skipped', 'timed_out', 'error', 'blocked'].includes(status);
  }

  protected knownArtifactCount(count: number | null | undefined): boolean {
    return typeof count === 'number' && Number.isInteger(count) && count >= 0;
  }

  protected duration(milliseconds: number | null | undefined): string | null {
    if (milliseconds == null || !Number.isFinite(milliseconds) || milliseconds < 0) return null;
    return milliseconds < 1000
      ? `${milliseconds} ms`
      : `${(milliseconds / 1000).toFixed(2).replace(/\.?0+$/, '')} s`;
  }

  protected turnPage(delta: number): void {
    const source = (delta < 0 ? this.previousButton() : this.nextButton())?.nativeElement;
    // Only a control that actually held focus is replaced by its own activation: a pointer press
    // that never moved focus leaves the visitor's place alone.
    const owned = source !== undefined && this.document.activeElement === source;
    this.page.update((page) => Math.max(0, Math.min(this.pages().length - 1, page + delta)));
    if (!owned) return;
    // The pager disables the button that reaches the first or last page in the same render, and a
    // disabled element cannot hold focus. Hand the place to the opposite button, the pager's other
    // reachable control, instead of letting focus drop to `body`.
    afterNextRender(
      () => {
        const active: unknown = this.document.activeElement;
        // The browser moves focus to `body` when the focused element is disabled or removed; a
        // test environment may leave it on that element, so both count as the same loss. Anything
        // else means the visitor moved on, and their own focus wins.
        const lost =
          !(active instanceof HTMLElement) ||
          active === this.document.body ||
          !active.isConnected ||
          (active === source && source !== undefined && source.disabled);
        if (!lost) return;
        const target = delta < 0 ? this.nextButton() : this.previousButton();
        if (target !== undefined && !target.nativeElement.disabled) target.nativeElement.focus();
      },
      { injector: this.injector },
    );
  }
}
