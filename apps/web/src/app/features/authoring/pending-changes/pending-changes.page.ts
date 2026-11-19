import { DOCUMENT } from '@angular/common';
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
  TemplateRef,
  viewChild,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { FormsModule } from '@angular/forms';
import type { AuthoringChangeListItem } from '@evidgate/sdk';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCheckbox } from '@evidgate/ui/checkbox';
import { HlmInput } from '@evidgate/ui/input';
import { HlmLabel } from '@evidgate/ui/label';
import { SessionStore } from '../../../core/session/session.store';
import { AbsoluteTimeComponent } from '../../../shared/time/absolute-time.component';
import { PageHeaderComponent } from '../../../shared/ui/page-header/page-header.component';
import { SectionHeaderComponent } from '../../../shared/ui/page-header/section-header.component';
import {
  DataTableComponent,
  type DataTableColumn,
} from '../../../shared/ui/data-table/data-table.component';
import type { DataTableCellContext } from '../../../shared/ui/data-table/data-table-column';
import { StatusPillComponent } from '../../../shared/ui/chips/status-pill.component';
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
import { PENDING_COPY, isStale, isStructural, rowBlockedReason } from './pending-changes.copy';
import { PENDING_UI, operationLabel, sourceLabel } from './pending-changes.ui-copy';
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
    HlmCheckbox,
    HlmInput,
    HlmLabel,
    PageHeaderComponent,
    SectionHeaderComponent,
    DataTableComponent,
    StatusPillComponent,
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
  private readonly selectionCell =
    viewChild.required<TemplateRef<DataTableCellContext<AuthoringChangeListItem>>>('selectionCell');
  private readonly operationCell =
    viewChild.required<TemplateRef<DataTableCellContext<AuthoringChangeListItem>>>('operationCell');
  private readonly targetCell =
    viewChild.required<TemplateRef<DataTableCellContext<AuthoringChangeListItem>>>('targetCell');
  private readonly stateCell =
    viewChild.required<TemplateRef<DataTableCellContext<AuthoringChangeListItem>>>('stateCell');
  private readonly inspection = viewChild<ElementRef<HTMLElement>>('inspection');
  private focused: HTMLElement | null = null;
  protected readonly rowId = (row: AuthoringChangeListItem) => row.change_id;
  protected readonly operationLabel = operationLabel;
  protected readonly isStructural = isStructural;
  protected readonly isStale = isStale;
  protected readonly rowBlockedReason = rowBlockedReason;
  protected readonly sourceLabel = sourceLabel;
  protected readonly columns = computed<readonly DataTableColumn<AuthoringChangeListItem>[]>(() => [
    {
      id: 'selection',
      header: this.ui.select,
      cell: () => '',
      role: 'actions',
      cellTemplate: this.selectionCell(),
    },
    {
      id: 'operation',
      header: this.ui.operation,
      cell: (row) => operationLabel(row.change_type),
      role: 'text',
      priority: 'primary',
      cellTemplate: this.operationCell(),
    },
    {
      id: 'target',
      header: this.copy.targetLabel,
      cell: (row) => this.store.targetTitles()[row.target_id] ?? row.target_id,
      role: 'identity',
      cellTemplate: this.targetCell(),
    },
    {
      id: 'author',
      header: this.copy.authorLabel,
      cell: (row) => row.author_membership_id,
      kind: 'identifier',
      overflow: 'truncate',
    },
    {
      id: 'source',
      header: this.copy.sourceLabelQueued,
      cell: (row) => sourceLabel(row.source),
      overflow: 'truncate',
    },
    {
      id: 'reason',
      header: this.copy.reasonLabel,
      cell: (row) => row.reason ?? this.copy.reasonUnset,
      overflow: 'truncate',
    },
    {
      id: 'state',
      header: this.ui.state,
      cell: (row) =>
        isStale(row) ? this.ui.stale : isStructural(row) ? this.ui.unavailable : this.ui.pending,
      role: 'status',
      cellTemplate: this.stateCell(),
    },
  ]);

  constructor() {
    afterRenderEffect(() => {
      this.store.queueLoading();
      if (!this.policy.canMutate() && this.consent()) this.consent.set(null);
      if (
        this.focused &&
        !this.focused.isConnected &&
        this.document.activeElement === this.document.body
      )
        this.inspection()?.nativeElement.focus();
    });
  }
  protected rememberFocus(event: FocusEvent): void {
    this.focused = event.target instanceof HTMLElement ? event.target : null;
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
  protected openDiscard(id: string): void {
    if (
      !this.policy.allowsMutation() ||
      !this.store.canWrite() ||
      this.busy() ||
      this.deniedWrite() ||
      !this.store.retryReady()
    )
      return;
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
      await this.store.apply();
    } else if (
      consent.ids[0] &&
      this.store.rows().some((row) => row.change_id === consent.ids[0])
    ) {
      await this.store.discard(consent.ids[0]);
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
