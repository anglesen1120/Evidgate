import {
  afterRenderEffect,
  afterNextRender,
  ChangeDetectionStrategy,
  Component,
  computed,
  ElementRef,
  inject,
  Injector,
  input,
  model,
  signal,
  TemplateRef,
  viewChild,
} from '@angular/core';
import type { CaseDto, MembershipView } from '@evidgate/sdk';
import { HlmButton } from '@evidgate/ui/button';
import type { ApiError } from '../../core/http/api-error';
import { StatusPillComponent } from '../../shared/ui/chips/status-pill.component';
import { DataTableComponent } from '../../shared/ui/data-table/data-table.component';
import type {
  DataTableCellContext,
  DataTableColumn,
} from '../../shared/ui/data-table/data-table-column';
import { DataTableValueComponent } from '../../shared/ui/data-table/data-table-value.component';
import { CopyableIdentifierComponent } from '../../shared/ui/values/copyable-identifier.component';
import { escapeFormatChars } from '../../shared/ui/values/ref-name-display';
import {
  CaseRecordDialogComponent,
  type CatalogRecordMode,
  type CatalogSelection,
  type SaveCatalogOwner,
} from './case-record-dialog.component';
import { CATALOG_RECORD } from './case-record.copy';

type CaseCell = TemplateRef<DataTableCellContext<CaseDto>>;

@Component({
  selector: 'app-case-list-table',
  imports: [
    DataTableComponent,
    DataTableValueComponent,
    CopyableIdentifierComponent,
    StatusPillComponent,
    CaseRecordDialogComponent,
    HlmButton,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class:
      'block min-w-0 [&_[data-row-id]:focus-visible]:outline-[length:var(--eg-focus-width)] [&_[data-row-id]:focus-visible]:outline-solid [&_[data-row-id]:focus-visible]:outline-ring [&_[data-row-id]:focus-visible]:-outline-offset-(--eg-focus-offset)',
  },
  templateUrl: './case-list-table.component.html',
})
export class CaseListTableComponent {
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);
  readonly rows = input.required<readonly CaseDto[]>();
  readonly members = input.required<ReadonlyMap<string, MembershipView>>();
  readonly canAssign = input(false);
  readonly membersReady = input(false);
  readonly ownerPending = input<(caseId: string) => boolean>(() => false);
  readonly ownerFailure = input<(caseId: string) => ApiError | null>(() => null);
  readonly saveOwner = input<SaveCatalogOwner | null>(null);
  readonly ownerHelp = input('');
  readonly selection = model<CatalogSelection | null>(null);
  private readonly dialog = viewChild(CaseRecordDialogComponent);
  private origin: {
    id: string;
    mode: CatalogRecordMode;
    element: HTMLElement;
    index: number;
  } | null = null;
  private returnAction: CatalogRecordMode | null = null;
  protected readonly recordCopy = CATALOG_RECORD;
  protected readonly selectedRow = computed(() => {
    const id = this.selection()?.id;
    return id ? this.rows().find((row) => row.id === id) : undefined;
  });
  protected readonly failureText = computed(() => {
    const row = this.selectedRow();
    const failure = row && this.ownerFailure()(row.id);
    return failure ? this.ownerFailureText(failure) : null;
  });
  readonly reloadOwner = input<(id: string) => Promise<void>>();
  private readonly table = viewChild(DataTableComponent<CaseDto>);
  private readonly focusTarget = signal<string | null>(null);
  private focusAllowed: () => boolean = () => true;
  protected readonly virtualRows = computed(() => this.table()?.virtual() ?? false);
  protected readonly activeMembers = computed(() =>
    [...this.members().values()].filter((member) => member.status === 'active'),
  );
  protected readonly eligible = computed(
    () => this.canAssign() && this.membersReady() && this.activeMembers().length > 0,
  );
  protected readonly memberLabel = (value: unknown) => {
    if (typeof value !== 'string' || value === '') return this.copy.unassigned;
    return this.display(this.members().get(value)?.invited_email ?? this.copy.unknownOwner);
  };
  protected readonly rowId = (row: CaseDto) => row.id;
  protected readonly display = escapeFormatChars;
  private readonly identity = viewChild.required<CaseCell>('identity');
  private readonly framework = viewChild.required<CaseCell>('framework');
  private readonly status = viewChild.required<CaseCell>('status');
  private readonly owner = viewChild.required<CaseCell>('owner');
  private readonly labels = viewChild.required<CaseCell>('labels');
  private readonly actions = viewChild.required<CaseCell>('actions');
  protected readonly copy = {
    caption: $localize`:@@catalog.loaded-table-caption:Loaded test identities`,
    identity: $localize`:@@catalog.column-identity:Identity / name`,
    stableId: $localize`:@@catalog.stable-id:Stable identity`,
    id: $localize`:@@catalog.case-id:Case ID`,
    framework: $localize`:@@catalog.framework:Framework`,
    status: $localize`:@@catalog.status:Status`,
    owner: $localize`:@@catalog.owner-label:Owner`,
    unassigned: $localize`:@@catalog.unassigned:Unassigned`,
    active: $localize`:@@catalog.active:Active`,
    missing: $localize`:@@catalog.missing:Missing`,
    archived: $localize`:@@catalog.archived:Archived`,
    saving: $localize`:@@catalog.owner-saving:Saving owner…`,
    reload: $localize`:@@catalog.owner-reload:Reload to retry`,
    denied: $localize`:@@catalog.owner-denied:Owner permission was refused. The previous owner is restored. Check permission, then reload to retry.`,
    conflict: $localize`:@@catalog.owner-conflict:The case changed. The previous owner is restored. Reload the list, then choose the owner again.`,
    failed: $localize`:@@catalog.owner-failed:The owner could not be saved. The previous owner is restored. Reload to retry.`,
    reloadRequired: $localize`:@@catalog.owner-reload-required:Owner change was not confirmed. Reload the list to see the stored owner.`,
    unknownOwner: $localize`:@@catalog.owner-unknown:Member details unavailable`,
  };
  protected readonly statusLabel = (status: CaseDto['status']) => this.copy[status];
  protected readonly columns = computed<readonly DataTableColumn<CaseDto>[]>(() => [
    {
      id: 'identity',
      header: this.copy.identity,
      cell: (row) => row.name,
      role: 'identity',
      kind: 'name',
      priority: 'primary',
      overflow: 'truncate',
      virtualWidth: 'calc(48ch + var(--eg-space-4) * 2)',
      cellTemplate: this.identity(),
    },
    {
      id: 'framework',
      header: this.copy.framework,
      cell: (row) => row.framework,
      role: 'text',
      priority: 'secondary',
      overflow: 'truncate',
      cellTemplate: this.framework(),
    },
    {
      id: 'status',
      header: this.copy.status,
      cell: (row) => this.statusLabel(row.status),
      role: 'status',
      priority: 'primary',
      overflow: 'truncate',
      cellTemplate: this.status(),
    },
    {
      id: 'owner',
      header: this.copy.owner,
      cell: (row) => this.memberLabel(row.owner_membership_id),
      role: 'identity',
      kind: 'email',
      priority: 'secondary',
      overflow: 'truncate',
      cellTemplate: this.owner(),
    },
    {
      id: 'labels',
      header: this.recordCopy.labels,
      cell: (row) => this.display(row.labels.join(', ')) || this.recordCopy.missingValue,
      role: 'text',
      priority: 'secondary',
      overflow: 'truncate',
      cellTemplate: this.labels(),
    },
    {
      id: 'actions',
      header: this.recordCopy.actions,
      cell: () => this.recordCopy.view,
      role: 'actions',
      priority: 'primary',
      virtualWidth: 'calc(20ch + var(--eg-space-4) * 2)',
      cellTemplate: this.actions(),
    },
  ]);

  constructor() {
    afterRenderEffect(() => {
      const id = this.focusTarget();
      const table = this.table();
      this.rows();
      table?.windowStart();
      table?.viewport();
      if (!id || !table) return;
      const index = this.rows().findIndex((row) => row.id === id);
      if (index < 0 || !this.focusAllowed()) {
        this.focusTarget.set(null);
        return;
      }
      const target = [
        ...this.host.nativeElement.querySelectorAll<HTMLElement>('[data-row-id]'),
      ].find((row) => row.dataset['rowId'] === id);
      if (!target) {
        table.revealRow(index);
        return;
      }
      const action = this.returnAction;
      this.returnAction = null;
      const focus = action
        ? (target.querySelector<HTMLElement>(
            action === 'edit' ? '[data-catalog-edit]' : '[data-catalog-view]',
          ) ?? target.querySelector<HTMLElement>('[data-catalog-view]'))
        : target;
      if (focus === target) target.tabIndex = -1;
      focus?.focus();
      focus?.scrollIntoView({ block: 'nearest' });
      this.focusTarget.set(null);
    });
  }

  focusRow(caseId: string, allowed: () => boolean = () => true): void {
    const index = this.rows().findIndex((row) => row.id === caseId);
    this.focusAllowed = allowed;
    this.focusTarget.set(index < 0 ? null : caseId);
    if (index >= 0) this.table()?.revealRow(index);
  }

  protected viewName(row: CaseDto): string {
    return $localize`:@@catalog.case-view.view-name:View ${this.display(row.name || row.id)}:name:`;
  }
  protected editName(row: CaseDto): string {
    return $localize`:@@catalog.case-view.edit-name:Edit owner for ${this.display(row.name || row.id)}:name:`;
  }
  protected open(row: CaseDto, mode: CatalogRecordMode, element: HTMLElement): void {
    if (mode === 'edit' && (!this.eligible() || this.ownerPending()(row.id))) return;
    this.requestLeave(() => {
      const index = this.rows().findIndex((candidate) => candidate.id === row.id);
      if (index < 0 || (mode === 'edit' && !this.eligible())) return;
      this.origin = { id: row.id, mode, element, index };
      this.selection.set({ id: row.id, mode });
    });
  }
  protected switchToEdit(id: string): void {
    if (this.eligible()) this.selection.set({ id, mode: 'edit' });
  }
  protected close(): void {
    this.restoreOpener();
    this.selection.set(null);
  }
  requestLeave(leave: () => void): boolean {
    const dialog = this.dialog();
    if (dialog) return dialog.requestLeave(leave);
    leave();
    return true;
  }
  restoreOpener(): void {
    const origin = this.origin;
    const id = origin?.id ?? this.selection()?.id;
    this.origin = null;
    if (!id) return;
    afterNextRender(
      () => {
        if (this.selection()) return;
        if (
          origin?.element.isConnected &&
          origin.element.closest('[data-row-id]')?.getAttribute('data-row-id') === id
        ) {
          origin.element.focus();
          return;
        }
        const row =
          this.rows().find((r) => r.id === id) ??
          this.rows()[Math.min(origin?.index ?? 0, this.rows().length - 1)];
        if (row) {
          this.returnAction = row.id === id ? (origin?.mode ?? 'view') : 'view';
          this.focusRow(row.id);
        } else {
          const heading =
            this.host.nativeElement.ownerDocument.getElementById('catalog-results-heading');
          if (heading) {
            heading.tabIndex = -1;
            heading.focus();
          }
        }
      },
      { injector: this.injector },
    );
  }

  protected ownerFailureText(failure: ApiError): string {
    if (failure.code === 'owner_outcome_unknown') return this.copy.reloadRequired;
    if (failure.kind === 'forbidden' || failure.kind === 'csrf_failed') return this.copy.denied;
    if (
      failure.kind === 'conflict' ||
      failure.kind === 'precondition_failed' ||
      failure.kind === 'idempotency_conflict'
    )
      return this.copy.conflict;
    if (failure.kind === 'validation') return this.copy.failed;
    return this.copy.reloadRequired;
  }
}
