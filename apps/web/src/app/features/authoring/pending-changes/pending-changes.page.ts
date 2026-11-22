import { DOCUMENT, NgTemplateOutlet } from '@angular/common';
import {
  afterRenderEffect,
  ChangeDetectionStrategy,
  Component,
  computed,
  ElementRef,
  inject,
  linkedSignal,
  resource,
  signal,
  viewChild,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { FormsModule } from '@angular/forms';
import type { AuthoringChangeListItem } from '@evidgate/sdk';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCard } from '@evidgate/ui/card';
import { HlmCheckbox } from '@evidgate/ui/checkbox';
import { HlmDropdownMenuImports } from '@evidgate/ui/dropdown-menu';
import { HlmIcon } from '@evidgate/ui/icon';
import { HlmInput } from '@evidgate/ui/input';
import { HlmLabel } from '@evidgate/ui/label';
import { SessionStore } from '../../../core/session/session.store';
import { AbsoluteTimeComponent } from '../../../shared/time/absolute-time.component';
import { PageHeaderComponent } from '../../../shared/ui/page-header/page-header.component';
import { SectionHeaderComponent } from '../../../shared/ui/page-header/section-header.component';
import { RevisionChipComponent } from '../../../shared/ui/chips/revision-chip.component';
import { RequestStateComponent } from '../../../shared/ui/states/request-state.component';
import { ForbiddenPatternComponent } from '../../../shared/ui/states/forbidden-pattern.component';
import { InlineFeedbackComponent } from '../../../shared/ui/states/inline-feedback.component';
import { ConflictDialogComponent } from '../../../shared/ui/conflict/conflict-dialog.component';
import { RetryAfterComponent } from '../../../shared/ui/conflict/retry-after.component';
import { CopyableIdentifierComponent } from '../../../shared/ui/values/copyable-identifier.component';
import { NumberValueComponent } from '../../../shared/ui/values/number-value.component';
import { DesktopMutationDirective } from '../../../shared/ui/narrow/desktop-mutation.directive';
import { DesktopRequiredNoticeComponent } from '../../../shared/ui/narrow/desktop-required-notice.component';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow/narrow-mutation-policy';
import { PendingChangesStore } from './pending-changes.store';
import { PENDING_COPY } from './pending-changes.copy';
import { PENDING_UI, operationLabel } from './pending-changes.ui-copy';
import { PendingChangeTableComponent } from './pending-change-table.component';
import { PendingConfirmComponent } from './pending-confirm.component';

interface Consent {
  readonly kind: 'apply' | 'discard';
  readonly project: string | null;
  readonly ids: readonly string[];
  readonly note: string;
  readonly changes: readonly AuthoringChangeListItem[];
  readonly destructive: boolean;
}

@Component({
  selector: 'app-pending-changes-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [PendingChangesStore],
  imports: [
    RouterLink,
    FormsModule,
    HlmButton,
    HlmCard,
    HlmIcon,
    NgTemplateOutlet,
    HlmCheckbox,
    HlmInput,
    HlmLabel,
    PageHeaderComponent,
    SectionHeaderComponent,
    PendingChangeTableComponent,
    HlmDropdownMenuImports,
    RevisionChipComponent,
    RequestStateComponent,
    ForbiddenPatternComponent,
    InlineFeedbackComponent,
    ConflictDialogComponent,
    RetryAfterComponent,
    CopyableIdentifierComponent,
    NumberValueComponent,
    AbsoluteTimeComponent,
    DesktopMutationDirective,
    DesktopRequiredNoticeComponent,
    PendingConfirmComponent,
  ],
  templateUrl: './pending-changes.page.html',
  styleUrl: './pending-changes.page.css',
  host: { '(focusin)': 'rememberFocus($event)', '[attr.data-pending-phase]': 'status()' },
})
export class PendingChangesPage {
  protected readonly store = inject(PendingChangesStore);
  protected readonly policy = inject(NarrowMutationPolicy);
  private readonly session = inject(SessionStore);
  private readonly route = inject(ActivatedRoute);
  private readonly document = inject(DOCUMENT);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  protected readonly copy = PENDING_COPY;
  protected readonly ui = PENDING_UI;
  private readonly routeData = toSignal(this.route.data);
  protected readonly ownsPageHeading = computed(() => this.routeData()?.['pageHeadingLevel'] === 1);
  protected readonly load = resource({
    params: () => (this.store.canRead() ? this.session.currentProject()?.id : undefined),
    loader: async ({ params }) => {
      await this.store.init(params);
      if (this.store.queueStatus() === 'ready') await this.store.loadChecklistFacts();
      return params;
    },
  });
  protected readonly status = computed<'loading' | 'ready' | 'error'>(() =>
    this.store.queueStatus() === 'ready'
      ? 'ready'
      : this.store.queueStatus() === 'error'
        ? 'error'
        : 'loading',
  );
  protected readonly blockedCount = computed(
    () => this.store.rowsWithBlocks().filter((entry) => entry.blockedReason !== null).length,
  );
  protected readonly description = computed(() =>
    this.status() === 'ready' && this.store.canRead() && !this.store.forbidden()
      ? `${this.ui.loaded}: ${this.store.rows().length} · ${this.copy.selectionSummary}: ${this.store.selectedCount()}`
      : this.copy.description,
  );
  protected readonly deniedWrite = computed(() =>
    [this.store.applyError(), this.store.discardError()].some(
      (error) => error?.kind === 'forbidden',
    ),
  );
  protected readonly busy = computed(
    () => this.store.applying() || this.store.discardingId() !== null,
  );
  protected readonly readBusy = computed(() => this.store.queueLoading());
  protected readonly blocked = computed(
    () =>
      this.store.applyBlockedReason() !== null ||
      this.busy() ||
      this.readBusy() ||
      !this.store.retryReady() ||
      this.store.queueStale() ||
      this.deniedWrite(),
  );
  protected readonly consent = signal<Consent | null>(null);
  protected readonly dismissedConflict = linkedSignal({
    source: () => this.store.conflict(),
    computation: () => false,
  });
  protected readonly conflictOpen = computed(
    () => this.store.canRead() && this.store.conflict() !== null && !this.dismissedConflict(),
  );
  protected readonly conflictFields = computed(() => [
    {
      id: 'pending-selection',
      label: this.ui.exactIds,
      local: this.store.selectedIds().join('\n'),
      server: null,
    },
    {
      id: 'pending-local-reason',
      label: this.copy.reasonLabel,
      local: this.store.localReason() || this.copy.reasonUnset,
      server: null,
    },
    ...(this.store.conflict()?.pointers ?? []).map((pointer, index) => ({
      id: `pointer-${index}`,
      label: pointer.path ?? this.copy.conflictTitle,
      local: null,
      server: pointer.reason,
    })),
  ]);
  protected readonly writeError = computed(
    () => this.store.applyError() ?? this.store.discardError(),
  );
  protected readonly retryFailure = computed(() => {
    const error = this.writeError() ?? this.store.queueError();
    return error?.kind === 'rate_limited' || error?.kind === 'unavailable' ? error : null;
  });
  private readonly inspection = viewChild<ElementRef<HTMLElement>>('inspection');
  private focused: HTMLElement | null = null;
  /** A mutation removed or disabled the control that opened its confirmation. */
  private restoreFocus = false;
  protected readonly operationLabel = operationLabel;
  protected readonly moreLabel = $localize`:@@authoring.pending-changes.more-actions:More actions`;

  constructor() {
    afterRenderEffect(() => {
      this.store.queueLoading();
      const empty = this.store.rows().length === 0;
      const restore = this.restoreFocus;
      this.restoreFocus = false;
      if (!this.policy.canMutate() && this.consent()) this.consent.set(null);
      const remembered = this.focused;
      if (remembered === null) return;
      if (restore) {
        if (!empty && remembered.isConnected) remembered.focus();
        if (
          this.document.activeElement !== remembered ||
          !remembered.isConnected ||
          remembered.matches(':disabled')
        )
          this.focusRemaining();
        return;
      }
      if (!remembered.isConnected && this.document.activeElement === this.document.body)
        this.focusRemaining();
    });
  }
  private focusRemaining(): void {
    (
      this.host.nativeElement.querySelector<HTMLElement>('[data-pending-preview]') ??
      this.inspection()?.nativeElement
    )?.focus();
  }
  protected rememberFocus(event: FocusEvent): void {
    const target = event.target;
    if (target instanceof HTMLElement && !target.closest('[role="alertdialog"]'))
      this.focused = target;
  }
  protected openApply(): void {
    if (!this.policy.allowsMutation() || this.blocked()) return;
    const ids = this.store.applyRequest()?.ids ?? this.store.selectedIds();
    const selected = new Set(ids);
    const changes =
      this.store.applyRequest()?.rows ??
      this.store.rows().filter((row) => selected.has(row.change_id));
    this.consent.set({
      kind: 'apply',
      project: this.store.projectId(),
      ids: [...ids],
      note: this.store.localReason(),
      changes,
      destructive: changes.some((row) => row.change_type === 'delete'),
    });
  }
  protected openDiscard(id: string, opener: HTMLButtonElement): void {
    if (
      !this.policy.allowsMutation() ||
      this.store.discardBlocked() ||
      this.busy() ||
      this.deniedWrite() ||
      !this.store.retryReady()
    )
      return;
    opener.focus();
    this.consent.set({
      kind: 'discard',
      project: this.store.projectId(),
      ids: [id],
      note: '',
      changes: [],
      destructive: true,
    });
  }
  protected async confirm(): Promise<void> {
    const consent = this.consent();
    this.consent.set(null);
    if (
      !consent ||
      consent.project !== this.store.projectId() ||
      !this.policy.allowsMutation() ||
      !this.store.canWrite() ||
      this.busy() ||
      this.deniedWrite() ||
      !this.store.retryReady()
    )
      return;
    if (consent.kind === 'apply') {
      const ids = this.store.applyRequest()?.ids ?? this.store.selectedIds();
      if (this.blocked() || consent.ids.join(',') !== ids.join(',')) return;
      if (await this.store.apply()) this.restoreFocus = true;
    } else if (
      consent.ids[0] &&
      !this.store.discardBlocked() &&
      this.store.rows().some((row) => row.change_id === consent.ids[0])
    ) {
      if (await this.store.discard(consent.ids[0])) this.restoreFocus = true;
    }
  }
  protected async reloadConflict(): Promise<void> {
    if (!this.store.retryReady()) return;
    await this.store.reload();
    if (!this.store.queueError()) this.dismissedConflict.set(true);
  }
  protected async retry(): Promise<void> {
    if (!this.store.retryReady() || this.busy()) return;
    if (this.store.applyError()) this.openApply();
    else await this.store.reload();
  }
}
