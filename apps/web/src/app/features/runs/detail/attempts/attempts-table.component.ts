import { DOCUMENT, formatNumber } from '@angular/common';
import {
  afterRenderEffect,
  ChangeDetectionStrategy,
  Component,
  computed,
  ElementRef,
  inject,
  input,
  linkedSignal,
  LOCALE_ID,
  output,
  untracked,
  viewChild,
} from '@angular/core';
import { HlmAlert } from '@evidgate/ui/alert';
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
import type { DataTableColumnWidth } from '../../../../shared/ui/data-table/data-table-column';
import { NumberValueComponent } from '../../../../shared/ui/values/number-value.component';
import { escapeFormatChars } from '../../../../shared/ui/values/ref-name-display';
import { attemptAnchorId } from '../evidence/run-evidence-anchors';
import { RUN_DETAIL_NOT_PROVIDED, runValueLabel } from '../run-value-label';
import { groupAttempts } from './attempt-groups';
import { DataTablePagerComponent } from '../../../../shared/ui/data-table/data-table-pager.component';
import type { AttemptRow } from './attempt-summary';

@Component({
  selector: 'app-attempts-table',
  imports: [
    StatusPillComponent,
    EmptyPatternComponent,
    ErrorPatternComponent,
    LoadingPatternComponent,
    HlmAlert,
    DataTablePagerComponent,
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
  private readonly locale = inject(LOCALE_ID);

  private readonly document = inject(DOCUMENT);

  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
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
  protected readonly normalisedWidth = {
    min: 'calc(14ch + var(--eg-space-4) * 2)',
  } as const satisfies DataTableColumnWidth;
  /** The first page's request state; `ready` with `null` attempts is the unavailable state. */
  readonly status = input<'loading' | 'ready' | 'error'>('ready');
  /** The first page's failure (`status` = error) or the failed follow-up page (`ready`). */
  readonly error = input<ApiError | null>(null);
  /** True while `next_cursor` names a further page of the keyset. */
  readonly hasMore = input(false);
  readonly loadingMore = input(false);
  readonly page = input(1);
  readonly pageSize = input(20);
  readonly retry = output<void>();
  readonly next = output<void>();
  readonly previous = output<void>();
  readonly pageSizeChange = output<number>();
  /** Every wire row carries `healing_candidate: null` until 08.3.1: shown unavailable, not "none". */
  protected readonly healingUnavailable = computed(
    () => !(this.attempts() ?? []).some((attempt) => attempt.healingCandidate),
  );
  protected readonly display = escapeFormatChars;
  protected readonly valueLabel = runValueLabel;
  /** Deep-link target identity (`#attempt-<execution_key>-<n>`), shared with the reason links. */
  protected readonly attemptAnchorId = attemptAnchorId;
  protected readonly visibleGroups = computed(() => groupAttempts(this.attempts() ?? []));
  protected readonly copy = {
    title: $localize`:@@runs.detail.attempts.title:Attempts`,
    scrollRegion: $localize`:@@runs.detail.attempts.scroll-region:Attempts table`,
    serverOrder: $localize`:@@runs.detail.attempts.server-order:Server order: execution key ascending, then attempt number ascending.`,
    execution: $localize`:@@runs.detail.attempts.execution:Execution`,
    attempt: $localize`:@@runs.detail.attempts.attempt:Attempt`,
    raw: $localize`:@@runs.detail.attempts.raw:Raw status`,
    normalised: $localize`:@@runs.detail.attempts.normalised:Normalised status`,
    duration: $localize`:@@runs.detail.attempts.duration:Duration`,
    artifacts: $localize`:@@runs.detail.attempts.artifacts:Artifacts`,
    healing: $localize`:@@runs.detail.attempts.healing-candidates:Healing candidates`,
    notProvided: RUN_DETAIL_NOT_PROVIDED,
    loadingMore: $localize`:@@runs.detail.attempts.page-loading:Loading attempts page`,
    moreLoaded: $localize`:@@runs.detail.attempts.page-loaded:Attempts page loaded`,
    empty: $localize`:@@runs.detail.attempts.empty:No attempts recorded`,
    preHealing: $localize`:@@runs.detail.attempts.pre-healing:Pre-healing failure`,
    candidate: $localize`:@@runs.detail.attempts.candidate:Healing candidate`,
    shadow: $localize`:@@runs.detail.attempts.shadow:Shadow evaluation`,
    official: $localize`:@@runs.detail.attempts.official:Official evaluation`,
  };
  private readonly tableRegion = viewChild<ElementRef<HTMLElement>>('tableRegion');
  private readonly heading = viewChild.required<ElementRef<HTMLElement>>('heading');
  /** The control whose activation started a read; the render that settles it can remove it. */
  private focusAnchor: HTMLElement | null = null;
  private restoreFocusPending = false;
  private restoreKind: 'page' | 'retry' = 'page';
  /**
   * Follow-up page phases: `loading` while one is fetched, `done` for the settled read that
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

  /** Retry can disappear before the next page settles. */
  private readonly presentControls = computed(() => ({
    failure: this.error() !== null,
    loading: this.loadingMore(),
  }));

  constructor() {
    // A retry removes its focused control while loading; preserve the visitor's place.
    afterRenderEffect(() => {
      const present = this.presentControls();
      untracked(() => {
        if (!this.restoreFocusPending) return;
        if (this.restoreKind === 'page' ? present.loading : present.failure) return;
        const anchor = this.focusAnchor;
        if (anchor !== null && anchor.isConnected && !anchor.matches(':disabled')) return;
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

  protected requestPage(forward: boolean): void {
    this.armFocusRestore('page');
    (forward ? this.next : this.previous).emit();
  }

  protected requestFollowUpRetry(): void {
    this.armFocusRestore('retry');
    this.retry.emit();
  }

  protected requestFirstPageRetry(): void {
    this.armFocusRestore('retry');
    this.retry.emit();
  }

  /** Arms the restore for the control that asked: only a control inside this panel that actually
   * holds focus is handed off, so a pointer press that never moved focus leaves the place alone. */
  private armFocusRestore(kind: 'page' | 'retry'): void {
    const active: unknown = this.document.activeElement;
    this.focusAnchor =
      active instanceof HTMLElement && this.host.nativeElement.contains(active) ? active : null;
    this.restoreKind = kind;
    this.restoreFocusPending = this.focusAnchor !== null;
  }

  /** A removed Retry or disabled navigation control hands focus to the surviving table region. */
  private focusTarget(): HTMLElement {
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
}
