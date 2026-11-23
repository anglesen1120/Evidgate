import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  inject,
  input,
  linkedSignal,
  type TemplateRef,
  viewChild,
} from '@angular/core';
import type { AuditEntryDto } from '@evidgate/sdk';
import { HlmButton } from '@evidgate/ui/button';
import { HlmDialogImports } from '@evidgate/ui/dialog';
import { HlmTooltip } from '@evidgate/ui/tooltip';
import { AbsoluteTimeComponent } from '../../../shared/time/absolute-time.component';
import { StatusPillComponent } from '../../../shared/ui/chips/status-pill.component';
import {
  DataTableComponent,
  type DataTableColumn,
} from '../../../shared/ui/data-table/data-table.component';
import type {
  DataTableCellContext,
  DataTableColumnWidth,
} from '../../../shared/ui/data-table/data-table-column';
import { DataTableValueComponent } from '../../../shared/ui/data-table/data-table-value.component';
import { CopyableIdentifierComponent } from '../../../shared/ui/values/copyable-identifier.component';
import { AUDIT_ACTOR_TYPE_LABELS } from './actor-type-labels';
import { supportAccessOf } from './support-access';

const IDENTIFIER_WIDTH: DataTableColumnWidth = {
  min: 'calc(13ch + var(--eg-space-4) * 2)',
  preferred: 'auto',
  max: 'calc(28ch + var(--eg-space-4) * 2)',
};
const ACTION_WIDTH: DataTableColumnWidth = { min: '12ch', preferred: 'auto', max: '24ch' };
const TIME_WIDTH: DataTableColumnWidth = {
  min: 'calc(24ch + var(--eg-space-4) * 2)',
  preferred: '1%',
  max: 'calc(28ch + var(--eg-space-4) * 2)',
};
const DETAILS_WIDTH: DataTableColumnWidth = {
  min: 'calc(14ch + var(--eg-space-4) * 2)',
  preferred: '1%',
  max: 'calc(22ch + var(--eg-space-4) * 2)',
};

@Component({
  selector: 'app-audit-table',
  imports: [
    AbsoluteTimeComponent,
    DataTableComponent,
    DataTableValueComponent,
    CopyableIdentifierComponent,
    HlmButton,
    HlmTooltip,
    HlmDialogImports,
    StatusPillComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0' },
  template: `
    <ng-template #timeCell let-row let-stacked="stacked">
      <app-absolute-time [value]="row.created_at" [class.whitespace-nowrap]="!stacked" />
    </ng-template>
    <ng-template #actorCell let-row let-stacked="stacked">
      <div
        class="grid min-w-0"
        data-audit-row
        [attr.data-audit-support]="supportOf(row) !== null ? 'true' : 'false'"
      >
        <div class="flex min-w-0 flex-wrap items-center gap-1">
          <app-status-pill [tone]="'neutral'" [label]="actorLabel(row.actor_type)" />
          @if (supportOf(row)) {
            <app-status-pill data-audit-support-chip [tone]="'warning'" [label]="copy.support" />
          }
        </div>
        @if (row.actor_id) {
          <app-copyable-identifier
            class="text-xs text-muted-foreground"
            [value]="row.actor_id"
            [label]="copy.actorId"
            [truncate]="!stacked"
            [compact]="!stacked"
          />
        }
      </div>
    </ng-template>
    <ng-template #objectCell let-row let-stacked="stacked">
      <div class="grid min-w-0">
        @if (row.object_type) {
          <app-data-table-value
            class="font-semibold"
            [value]="row.object_type"
            [truncate]="!stacked"
          />
        }
        @if (row.object_id) {
          <app-copyable-identifier
            class="text-xs text-muted-foreground"
            [value]="row.object_id"
            [label]="copy.objectId"
            [truncate]="!stacked"
            [compact]="!stacked"
          />
        }
        @if (!row.object_type && !row.object_id) {
          <span>{{ copy.absent }}</span>
        }
      </div>
    </ng-template>
    <ng-template #detailsCell let-row let-stacked="stacked">
      <button
        hlmBtn
        #trigger
        [variant]="'outline'"
        type="button"
        data-audit-view
        [attr.aria-haspopup]="'dialog'"
        class="min-h-(--eg-control-touch)"
        [class.whitespace-nowrap]="!stacked"
        [attr.aria-label]="copy.view + ' · ' + row.action"
        (click)="inspected.set({ row, trigger })"
      >
        {{ copy.view }}
      </button>
    </ng-template>
    @if (inspected(); as selection) {
      @let row = selection.row;
      <hlm-dialog
        [state]="'open'"
        [restoreFocus]="selection.trigger"
        [autoFocus]="'first-heading'"
        (closed)="inspected.set(null)"
      >
        <hlm-dialog-content *hlmDialogPortal [size]="'md'" data-audit-dialog>
          <header hlmDialogHeader>
            <h2 hlmDialogTitle>{{ copy.details }}</h2>
            <p hlmDialogDescription>
              <bdi>{{ row.action }}</bdi> · <app-absolute-time [value]="row.created_at" />
            </p>
          </header>
          <dl class="hlm-field-grid text-sm">
            @if (row.request_id) {
              <div class="hlm-field-wide">
                <dt class="text-xs text-muted-foreground">{{ copy.request }}</dt>
                <dd class="m-0">
                  <app-copyable-identifier
                    [value]="row.request_id"
                    [label]="copy.request"
                    [truncate]="false"
                  />
                </dd>
              </div>
            }
            @if (supportOf(row); as access) {
              <div class="hlm-field-wide">
                <dt class="text-xs text-muted-foreground">{{ copy.support }}</dt>
                <dd class="m-0">
                  <button
                    hlmBtn
                    [variant]="'ghost'"
                    type="button"
                    class="support-access h-auto min-h-[var(--eg-control-touch)] max-w-full whitespace-normal p-0"
                    [hlmTooltip]="supportDescription(access.grantId)"
                    [position]="'top'"
                  >
                    <app-status-pill [tone]="'warning'" [label]="copy.support" />
                  </button>
                  <span class="block wrap-anywhere text-xs text-muted-foreground">
                    {{ supportDescription(access.grantId) }}
                  </span>
                  @if (access.grantId; as grantId) {
                    <app-copyable-identifier
                      [value]="grantId"
                      [label]="copy.supportGrant"
                      [truncate]="false"
                    />
                  }
                </dd>
              </div>
            }
            <div [class.hlm-field-wide]="!!row.actor_id">
              <dt class="text-xs text-muted-foreground">{{ copy.actor }}</dt>
              <dd class="m-0">
                {{ actorLabel(row.actor_type) }}
                @if (row.actor_id) {
                  <app-copyable-identifier
                    [value]="row.actor_id"
                    [label]="copy.actorId"
                    [truncate]="false"
                  />
                }
              </dd>
            </div>
            @if (row.object_type || row.object_id) {
              <div [class.hlm-field-wide]="!!row.object_id">
                <dt class="text-xs text-muted-foreground">{{ copy.object }}</dt>
                <dd class="m-0">
                  <bdi>{{ row.object_type }}</bdi>
                  @if (row.object_id) {
                    <app-copyable-identifier
                      [value]="row.object_id"
                      [label]="copy.objectId"
                      [truncate]="false"
                    />
                  }
                </dd>
              </div>
            }
          </dl>
        </hlm-dialog-content>
      </hlm-dialog>
    }
    <app-data-table
      class="[&_td]:py-(--eg-space-1)! [&_td.cdk-column-action]:font-mono [&_td.cdk-column-action]:font-semibold"
      [columns]="columns()"
      [rows]="rows()"
      [rowId]="rowId"
      [caption]="copy.caption"
      [rowHeight]="'compact'"
      [flow]="tableFlow"
      [fitToContainer]="true"
    />
  `,
})
export class AuditTableComponent {
  private readonly element = inject<ElementRef<HTMLElement>>(ElementRef);

  readonly rows = input.required<readonly AuditEntryDto[]>();
  protected readonly inspected = linkedSignal<
    readonly AuditEntryDto[],
    { row: AuditEntryDto; trigger: HTMLButtonElement } | null
  >({
    source: this.rows,
    computation: () => null,
  });
  protected readonly supportOf = supportAccessOf;
  protected readonly copy = {
    caption: $localize`:@@settings.audit.caption:Audit activity`,
    time: $localize`:@@settings.audit.time:Time (UTC)`,
    actor: $localize`:@@settings.audit.actor:Actor`,
    action: $localize`:@@settings.audit.action:Action`,
    actorId: $localize`:@@settings.audit.actor-id:Audit actor id`,
    objectId: $localize`:@@settings.audit.object-id:Audit object id`,
    supportGrant: $localize`:@@settings.audit.support-grant:Support grant id`,
    object: $localize`:@@settings.audit.object:Object`,
    request: $localize`:@@settings.audit.request:Request id`,
    support: $localize`:@@settings.audit.support:Support access`,
    view: $localize`:@@settings.row.view:View`,
    details: $localize`:@@settings.audit.details:Event details`,
    absent: $localize`:@@settings.audit.absent:—`,
  };
  private readonly timeCell =
    viewChild<TemplateRef<DataTableCellContext<AuditEntryDto>>>('timeCell');
  private readonly actorCell =
    viewChild<TemplateRef<DataTableCellContext<AuditEntryDto>>>('actorCell');
  private readonly objectCell =
    viewChild<TemplateRef<DataTableCellContext<AuditEntryDto>>>('objectCell');
  private readonly detailsCell =
    viewChild<TemplateRef<DataTableCellContext<AuditEntryDto>>>('detailsCell');
  protected readonly rowId = (row: AuditEntryDto): string => row.id;
  protected readonly tableFlow = 'page' as const;
  protected readonly columns = computed<readonly DataTableColumn<AuditEntryDto>[]>(() => {
    const time = this.timeCell();
    const actor = this.actorCell();
    const object = this.objectCell();
    const details = this.detailsCell();
    if (!time || !actor || !object || !details) return [];
    return [
      {
        id: 'actor',
        header: this.copy.actor,
        role: 'identity',
        kind: 'identifier',
        priority: 'secondary',
        width: IDENTIFIER_WIDTH,
        cell: (row) => row.actor_id ?? '',
        cellTemplate: actor,
      },
      {
        id: 'action',
        header: this.copy.action,
        role: 'identity',
        priority: 'primary',
        overflow: 'truncate',
        width: ACTION_WIDTH,
        cell: (row) => row.action,
      },
      {
        id: 'object',
        header: this.copy.object,
        role: 'identity',
        kind: 'identifier',
        priority: 'secondary',
        width: IDENTIFIER_WIDTH,
        cell: (row) => row.object_id ?? '',
        cellTemplate: object,
      },
      {
        id: 'time',
        header: this.copy.time,
        role: 'date',
        width: TIME_WIDTH,
        cell: (row) => row.created_at,
        cellTemplate: time,
      },
      {
        id: 'details',
        header: this.copy.details,
        role: 'actions',
        priority: 'secondary',
        width: DETAILS_WIDTH,
        cell: () => this.copy.details,
        cellTemplate: details,
      },
    ];
  });

  protected actorLabel(actorType: string): string {
    return Object.hasOwn(AUDIT_ACTOR_TYPE_LABELS, actorType)
      ? AUDIT_ACTOR_TYPE_LABELS[actorType as keyof typeof AUDIT_ACTOR_TYPE_LABELS]
      : actorType;
  }

  protected supportDescription(grantId: string | null): string {
    return grantId
      ? $localize`:@@settings.audit.support-description:Platform staff access under support grant ${grantId}:grantId:`
      : $localize`:@@settings.audit.support-unrecorded:Platform staff access (grant id not recorded on this row)`;
  }

  resetScroll(): void {
    const region = this.element.nativeElement.querySelector<HTMLElement>('[hlmTableContainer]');
    if (region) {
      region.scrollLeft = 0;
      region.scrollTop = 0;
    }
  }
}
