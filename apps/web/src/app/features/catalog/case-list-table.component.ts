import {
  afterRenderEffect,
  ChangeDetectionStrategy,
  Component,
  computed,
  ElementRef,
  inject,
  input,
  output,
  signal,
  TemplateRef,
  viewChild,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import type { CaseDto, MembershipView } from '@evidgate/sdk';
import { HlmButton } from '@evidgate/ui/button';
import { HlmLabel } from '@evidgate/ui/label';
import { HlmSelectImports } from '@evidgate/ui/select';
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

type CaseCell = TemplateRef<DataTableCellContext<CaseDto>>;

@Component({
  selector: 'app-case-list-table',
  imports: [
    DataTableComponent,
    DataTableValueComponent,
    CopyableIdentifierComponent,
    StatusPillComponent,
    FormsModule,
    HlmButton,
    HlmLabel,
    HlmSelectImports,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class:
      'block min-w-0 [&_[data-row-id]:focus-visible]:outline-[length:var(--eg-focus-width)] [&_[data-row-id]:focus-visible]:outline-solid [&_[data-row-id]:focus-visible]:outline-ring [&_[data-row-id]:focus-visible]:-outline-offset-(--eg-focus-offset)',
  },
  template: `
    <app-data-table
      [columns]="columns()"
      [rows]="rows()"
      [rowId]="rowId"
      [caption]="copy.caption"
      [fitToContainer]="true"
      [fixedHeightCells]="true"
      [flow]="'page'"
    />
    <ng-template #identity let-row let-stacked="stacked">
      @if (virtualRows()) {
        <app-data-table-value [value]="identitySummary(row)" [fixedRow]="true" />
      } @else {
        <div class="grid min-w-0 gap-(--eg-space-2)" data-catalog-identity>
          <bdi class="min-w-0 font-(weight:--eg-weight-semibold)">
            <app-data-table-value [value]="display(row.name)" [truncate]="!stacked" />
          </bdi>
          <app-copyable-identifier
            [value]="row.annotation_id"
            [label]="copy.stableId"
            [truncate]="!stacked"
            [copySize]="'lg'"
          />
          <app-copyable-identifier
            [value]="row.id"
            [label]="copy.id"
            [truncate]="!stacked"
            [copySize]="'lg'"
          />
        </div>
      }
    </ng-template>
    <ng-template #framework let-row let-stacked="stacked">
      <bdi
        ><app-data-table-value
          [value]="display(row.framework)"
          [truncate]="!stacked"
          [fixedRow]="virtualRows()"
      /></bdi>
    </ng-template>
    <ng-template #status let-row>
      <app-status-pill
        [label]="statusLabel(row.status)"
        [tone]="row.status === 'missing' ? 'warning' : 'neutral'"
      />
    </ng-template>
    <ng-template #owner let-row let-stacked="stacked">
      <div
        class="min-w-0 items-center gap-(--eg-space-2)"
        [class.grid]="!virtualRows()"
        [class.flex]="virtualRows()"
        [attr.data-catalog-owner]="row.id"
        [attr.aria-busy]="ownerPending()(row.id)"
      >
        <div class="min-w-0 flex-1">
          <label hlmLabel class="sr-only" [for]="ownerId(row.id)">{{ ownerLabel(row) }}</label>
          <hlm-select
            [ngModel]="row.owner_membership_id ?? ''"
            (ngModelChange)="chooseOwner(row, $event)"
            [itemToString]="memberLabel"
            [disabled]="
              !canAssign() || !membersReady() || !activeMembers().length || ownerPending()(row.id)
            "
          >
            <hlm-select-trigger
              [buttonId]="ownerId(row.id)"
              class="min-h-(--eg-control-touch) w-full min-w-0 max-w-full"
              [aria-describedby]="ownerDescription(row.id)"
            >
              <hlm-select-value [placeholder]="copy.unassigned" />
            </hlm-select-trigger>
            <hlm-select-content *hlmSelectPortal [aria-label]="ownerLabel(row)">
              @if (row.owner_membership_id && !activeOwner(row.owner_membership_id)) {
                <hlm-select-item [value]="row.owner_membership_id" [disabled]="true">
                  {{ memberLabel(row.owner_membership_id) }}
                </hlm-select-item>
              }
              @for (member of activeMembers(); track member.id) {
                <hlm-select-item
                  [value]="member.id"
                  class="min-h-(--eg-control-touch) whitespace-normal [overflow-wrap:anywhere]"
                >
                  <bdi>{{ display(member.invited_email) }}</bdi>
                </hlm-select-item>
              }
            </hlm-select-content>
          </hlm-select>
        </div>
        @if (row.owner_membership_id && !members().has(row.owner_membership_id)) {
          <app-copyable-identifier
            [value]="row.owner_membership_id"
            [label]="copy.owner"
            [truncate]="!stacked"
            [compact]="virtualRows()"
            [copySize]="'lg'"
          />
        } @else if (!canAssign() || !membersReady()) {
          <app-data-table-value
            class="min-w-0 flex-1"
            [value]="memberLabel(row.owner_membership_id)"
            [truncate]="!stacked"
            [fixedRow]="virtualRows()"
          />
        }
        @if (ownerPending()(row.id)) {
          <span
            [id]="ownerId(row.id) + '-pending'"
            class="shrink-0 text-muted-foreground"
            role="status"
            data-catalog-owner-pending
            >{{ copy.saving }}</span
          >
        }
        @if (ownerFailure()(row.id); as failure) {
          <div
            [id]="ownerId(row.id) + '-error'"
            class="min-w-0 flex-1 text-destructive"
            role="alert"
            data-catalog-owner-error
          >
            <app-data-table-value
              [value]="ownerFailureText(failure)"
              [truncate]="virtualRows()"
              [fixedRow]="virtualRows()"
            />
          </div>
          <button
            hlmBtn
            type="button"
            [variant]="'outline'"
            class="min-h-(--eg-control-touch) max-w-full shrink-0"
            [class.whitespace-normal]="!virtualRows()"
            [class.whitespace-nowrap]="virtualRows()"
            (click)="reloadOwners.emit()"
          >
            {{ copy.reload }}
          </button>
        }
      </div>
    </ng-template>
  `,
})
export class CaseListTableComponent {
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  readonly rows = input.required<readonly CaseDto[]>();
  readonly members = input.required<ReadonlyMap<string, MembershipView>>();
  readonly canAssign = input(false);
  readonly membersReady = input(false);
  readonly ownerPending = input<(caseId: string) => boolean>(() => false);
  readonly ownerFailure = input<(caseId: string) => ApiError | null>(() => null);
  readonly ownerChange = output<{ caseId: string; membershipId: string }>();
  readonly reloadOwners = output<void>();
  private readonly table = viewChild(DataTableComponent<CaseDto>);
  private readonly focusTarget = signal<string | null>(null);
  protected readonly virtualRows = computed(() => this.table()?.virtual() ?? false);
  protected readonly activeMembers = computed(() =>
    [...this.members().values()].filter((member) => member.status === 'active'),
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
    unknownOwner: $localize`:@@catalog.owner-unknown:Member details unavailable`,
  };
  protected readonly statusLabel = (status: CaseDto['status']) => this.copy[status];
  protected readonly columns = computed<readonly DataTableColumn<CaseDto>[]>(() => [
    {
      id: 'identity',
      header: this.copy.identity,
      cell: (row) => row.annotation_id,
      role: 'identity',
      kind: 'name',
      priority: 'primary',
      overflow: 'truncate',
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
  ]);

  constructor() {
    afterRenderEffect(() => {
      const id = this.focusTarget();
      const table = this.table();
      this.rows();
      table?.windowStart();
      table?.viewport();
      if (!id || !table) return;
      const target = [
        ...this.host.nativeElement.querySelectorAll<HTMLElement>('[data-row-id]'),
      ].find((row) => row.dataset['rowId'] === id);
      if (!target) {
        table.revealRow(this.rows().findIndex((row) => row.id === id));
        return;
      }
      target.tabIndex = -1;
      target.focus();
      target.scrollIntoView({ block: 'nearest' });
      this.focusTarget.set(null);
    });
  }

  focusRow(caseId: string): void {
    this.focusTarget.set(caseId);
    this.table()?.revealRow(this.rows().findIndex((row) => row.id === caseId));
  }

  protected ownerId(caseId: string): string {
    return `catalog-owner-${caseId}`;
  }

  protected ownerLabel(row: CaseDto): string {
    const name = this.display(row.name);
    return $localize`:@@catalog.owner-for:Owner for ${name}:name:`;
  }

  protected identitySummary(row: CaseDto): string {
    return this.display(`${row.name} · ${row.annotation_id} · ${row.id}`);
  }

  protected activeOwner(membershipId: string): boolean {
    return this.members().get(membershipId)?.status === 'active';
  }

  protected ownerDescription(caseId: string): string {
    const ids = ['catalog-owner-help'];
    if (this.ownerPending()(caseId)) ids.push(this.ownerId(caseId) + '-pending');
    if (this.ownerFailure()(caseId)) ids.push(this.ownerId(caseId) + '-error');
    return ids.join(' ');
  }

  protected chooseOwner(row: CaseDto, value: unknown): void {
    if (!this.canAssign() || !this.membersReady() || this.ownerPending()(row.id)) return;
    if (typeof value !== 'string' || !this.activeOwner(value) || value === row.owner_membership_id)
      return;
    this.ownerChange.emit({ caseId: row.id, membershipId: value });
  }

  protected ownerFailureText(failure: ApiError): string {
    if (failure.kind === 'forbidden') return this.copy.denied;
    if (failure.kind === 'conflict' || failure.kind === 'precondition_failed')
      return this.copy.conflict;
    return this.copy.failed;
  }
}
