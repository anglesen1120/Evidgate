import { DOCUMENT, formatNumber } from '@angular/common';
import {
  afterNextRender,
  afterRenderEffect,
  ChangeDetectionStrategy,
  Component,
  computed,
  ElementRef,
  inject,
  Injector,
  input,
  linkedSignal,
  LOCALE_ID,
  output,
  untracked,
  viewChild,
} from '@angular/core';
import { HlmAlert } from '@evidgate/ui/alert';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCard, HlmCardTitle } from '@evidgate/ui/card';
import { HlmTableImports } from '@evidgate/ui/table';
import type { ApiError } from '../../../../core/http/api-error';
import {
  StatusPillComponent,
  type StatusTone,
} from '../../../../shared/ui/chips/status-pill.component';
import { EmptyPatternComponent } from '../../../../shared/ui/states/empty-pattern.component';
import { ErrorPatternComponent } from '../../../../shared/ui/states/error-pattern.component';
import { LoadingPatternComponent } from '../../../../shared/ui/states/loading-pattern.component';
import { CopyValueButtonComponent } from '../../../../shared/ui/values/copy-value-button.component';
import { DataTableValueComponent } from '../../../../shared/ui/data-table/data-table-value.component';
import { NumberValueComponent } from '../../../../shared/ui/values/number-value.component';
import { escapeFormatChars } from '../../../../shared/ui/values/ref-name-display';
import { attemptAnchorId } from '../evidence/run-evidence-anchors';
import { RUN_DETAIL_NOT_PROVIDED, runValueLabel } from '../run-value-label';
import { groupAttempts, paginateAttemptGroups } from './attempt-groups';
import type { AttemptRow } from './attempt-summary';

@Component({
  selector: 'app-attempts-table',
  imports: [
    StatusPillComponent,
    EmptyPatternComponent,
    ErrorPatternComponent,
    LoadingPatternComponent,
    HlmAlert,
    HlmButton,
    HlmCard,
    HlmCardTitle,
    HlmTableImports,
    CopyValueButtonComponent,
    DataTableValueComponent,
    NumberValueComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './attempts-table.component.html',
  host: { class: 'block min-w-0 text-foreground wrap-anywhere [&_p]:m-0' },
})
export class AttemptsTableComponent {
  readonly attempts = input.required<AttemptRow[] | null>();
  protected readonly sectionSize = 'section' as const;
  protected readonly columnRole = {
    identity: 'identity',
    text: 'text',
    status: 'status',
    number: 'number',
  } as const;
  protected readonly identifierKind = 'identifier' as const;
  protected readonly primaryColumn = 'primary' as const;
  protected readonly scanOverflow = 'truncate' as const;
  private readonly locale = inject(LOCALE_ID);
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
  protected readonly valueLabel = runValueLabel;
  /** Deep-link target identity (`#attempt-<execution_key>-<n>`), shared with the reason links. */
  protected readonly attemptAnchorId = attemptAnchorId;
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
  protected readonly pageLabel = computed(() => {
    const page = this.page() + 1;
    const pages = this.pages().length;
    return $localize`:@@runs.detail.attempts.page:Page ${page}:page: / ${pages}:pages:`;
  });
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
    notProvided: RUN_DETAIL_NOT_PROVIDED,
    more: $localize`:@@runs.detail.attempts.more:Load more attempts`,
    loadingMore: $localize`:@@runs.detail.attempts.loading-more:Loading more attempts`,
    moreLoaded: $localize`:@@runs.detail.attempts.more-loaded:More attempts loaded`,
    empty: $localize`:@@runs.detail.attempts.empty:No attempts recorded`,
    preHealing: $localize`:@@runs.detail.attempts.pre-healing:Pre-healing failure`,
    candidate: $localize`:@@runs.detail.attempts.candidate:Healing candidate`,
    shadow: $localize`:@@runs.detail.attempts.shadow:Shadow evaluation`,
    official: $localize`:@@runs.detail.attempts.official:Official evaluation`,
    previous: $localize`:@@runs.detail.attempts.previous:Previous page`,
    next: $localize`:@@runs.detail.attempts.next:Next page`,
  };
  private readonly document = inject(DOCUMENT);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);
  private readonly previousButton = viewChild<ElementRef<HTMLButtonElement>>('previous');
  private readonly nextButton = viewChild<ElementRef<HTMLButtonElement>>('next');
  private readonly loadMoreButton = viewChild<ElementRef<HTMLButtonElement>>('loadMore');
  private readonly tableRegion = viewChild<ElementRef<HTMLElement>>('tableRegion');
  private readonly heading = viewChild.required<ElementRef<HTMLElement>>('heading');
  /** The control whose activation started a read; the render that settles it can remove it. */
  private focusAnchor: HTMLElement | null = null;
  private restoreFocusPending = false;
  private restoreKind: 'more' | 'retry' = 'more';
  /**
   * Follow-up page phases: `loading` while one is being added, `done` for the settled read that
   * kept rows. A failed read reports through the error panel's own alert, not as a completion.
   */
  private readonly followUpPhase = linkedSignal<
    { loading: boolean; failed: boolean },
    'idle' | 'loading' | 'done'
  >({
    source: computed(() => ({ loading: this.loadingMore(), failed: this.error() !== null })),
    computation: (next, previous) => {
      if (next.loading) return 'loading';
      if (previous?.value !== 'loading') return previous?.value ?? 'idle';
      return next.failed ? 'idle' : 'done';
    },
  });
  /** A live region kept out of the busy region: AT defers changes inside `aria-busy`. */
  protected readonly followUpStatus = computed(() => {
    switch (this.followUpPhase()) {
      case 'loading':
        return this.copy.loadingMore;
      case 'done':
        return this.copy.moreLoaded;
      default:
        return '';
    }
  });

  /** Which follow-up controls this render keeps: the place is only spent once the control the
   * visitor activated is gone. Load more survives a busy read; an error block does not. */
  private readonly presentControls = computed(() => ({
    more: this.hasMore(),
    failure: this.error() !== null,
  }));

  constructor() {
    // A settled read can remove the control that had focus: the last page clears Load more and a
    // retry clears the error block. The document then drops focus to `body` (WCAG 2.4.3), so the
    // place moves on in the render that removes the control — not only once the read settles,
    // because the retry's own block is gone for the whole request. A surviving control or a place
    // the visitor moved to keeps the focus, so nothing is pulled back from a chosen element.
    afterRenderEffect(() => {
      const present = this.presentControls();
      untracked(() => {
        if (!this.restoreFocusPending) return;
        if (this.restoreKind === 'more' ? present.more : present.failure) return;
        const anchor = this.focusAnchor;
        if (anchor !== null && anchor.isConnected) return;
        this.restoreFocusPending = false;
        this.focusAnchor = null;
        const active: unknown = this.document.activeElement;
        // A removed control can still be `activeElement` in a test environment, and the browser
        // leaves it on `body` or `documentElement`: all of those are the same loss (see turnPage).
        if (
          active instanceof HTMLElement &&
          active !== this.document.body &&
          active !== this.document.documentElement &&
          active.isConnected
        ) {
          return;
        }
        this.focusTarget().focus();
      });
    });
  }

  protected requestLoadMore(): void {
    this.armFocusRestore('more');
    this.loadMore.emit();
  }

  protected requestFollowUpRetry(): void {
    this.armFocusRestore('retry');
    this.loadMore.emit();
  }

  protected requestFirstPageRetry(): void {
    this.armFocusRestore('retry');
    this.retry.emit();
  }

  /** Arms the restore for the control that asked: only a control inside this panel that actually
   * holds focus is handed off, so a pointer press that never moved focus leaves the place alone. */
  private armFocusRestore(kind: 'more' | 'retry'): void {
    const active: unknown = this.document.activeElement;
    this.focusAnchor =
      active instanceof HTMLElement && this.host.nativeElement.contains(active) ? active : null;
    this.restoreKind = kind;
    this.restoreFocusPending = this.focusAnchor !== null;
  }

  /** The forward pager when the page grew, the append control when a retry left it, else the
   * table's own region and the panel heading, which always exist. */
  private focusTarget(): HTMLElement {
    const next = this.nextButton()?.nativeElement;
    const more = this.loadMoreButton()?.nativeElement;
    const preferred = this.restoreKind === 'more' ? [next, more] : [more, next];
    const usable = preferred.find(
      (element) => element !== undefined && element.isConnected && !element.disabled,
    );
    if (usable !== undefined) return usable;
    const region = this.tableRegion()?.nativeElement;
    if (region !== undefined && region.isConnected) return region;
    return this.heading().nativeElement;
  }

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
      ? `${formatNumber(milliseconds, this.locale, '1.0-20')} ms`
      : `${formatNumber(milliseconds / 1000, this.locale, '1.0-2')} s`;
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
