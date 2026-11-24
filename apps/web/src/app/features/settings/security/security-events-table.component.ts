import { JsonPipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  input,
  linkedSignal,
  viewChild,
  type TemplateRef,
} from '@angular/core';
import { HlmDialogImports } from '@evidgate/ui/dialog';
import type { SecurityEventRow } from '../../../core/data/security-events-data.service';
import { AbsoluteTimeComponent } from '../../../shared/time/absolute-time.component';
import {
  StatusPillComponent,
  type StatusTone,
} from '../../../shared/ui/chips/status-pill.component';
import {
  DataTableComponent,
  type DataTableColumn,
} from '../../../shared/ui/data-table/data-table.component';
import type { DataTableCellContext } from '../../../shared/ui/data-table/data-table-column';
import { DataTableValueComponent } from '../../../shared/ui/data-table/data-table-value.component';
import { RowActionComponent } from '../../../shared/ui/data-table/row-action.component';
import { CopyableIdentifierComponent } from '../../../shared/ui/values/copyable-identifier.component';
import { SECURITY_SEVERITY_LABELS, securityTypeLabel } from './security-events.config';

@Component({
  selector: 'app-security-events-table',
  imports: [
    JsonPipe,
    RowActionComponent,
    HlmDialogImports,
    AbsoluteTimeComponent,
    StatusPillComponent,
    DataTableComponent,
    DataTableValueComponent,
    CopyableIdentifierComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0' },
  template: `
    <ng-template #eventCell let-row let-stacked="stacked">
      <div class="grid min-w-0">
        <app-data-table-value
          class="font-semibold"
          [value]="typeLabel(row.type)"
          [truncate]="!stacked"
        />
        <app-data-table-value
          class="font-mono text-xs text-muted-foreground"
          [value]="row.id"
          [truncate]="!stacked"
        />
        @if (row.type === 'support_access') {
          <app-status-pill [tone]="'warning'" [label]="copy.support" />
        }
      </div>
    </ng-template>
    <ng-template #severityCell let-row>
      <app-status-pill [tone]="severityTone(row.severity)" [label]="severityLabel(row.severity)" />
    </ng-template>
    <ng-template #timeCell let-row>
      <app-absolute-time class="text-xs tabular-nums whitespace-normal" [value]="row.created_at" />
    </ng-template>
    <ng-template #requestCell let-row let-stacked="stacked">
      @if (row.request_id) {
        <app-data-table-value
          class="max-w-[12ch] font-mono text-xs"
          [value]="row.request_id"
          [truncate]="!stacked"
        />
      } @else {
        <span class="text-xs text-muted-foreground">{{ copy.unavailable }}</span>
      }
    </ng-template>
    <ng-template #actorCell let-row let-stacked="stacked">
      <div class="grid min-w-0">
        @if (row.actor.kind) {
          <app-data-table-value class="text-xs" [value]="row.actor.kind" [truncate]="!stacked" />
        }
        @if (row.actor.id) {
          <app-copyable-identifier
            [value]="row.actor.id"
            [label]="copy.actor"
            [truncate]="!stacked"
            [compact]="!stacked"
          />
        } @else {
          <span>{{ copy.unavailable }}</span>
        }
      </div>
    </ng-template>
    <ng-template #detailsCell let-row>
      <button
        appRowAction
        #trigger
        [label]="copy.view + ' · ' + typeLabel(row.type) + ' · ' + row.id"
        type="button"
        data-security-view
        [attr.aria-haspopup]="'dialog'"
        [attr.aria-label]="copy.view + ' · ' + typeLabel(row.type) + ' · ' + row.id"
        (click)="inspected.set({ row, trigger: trigger.nativeElement })"
      ></button>
    </ng-template>
    @if (inspected(); as selection) {
      @let row = selection.row;
      <hlm-dialog
        [state]="'open'"
        [restoreFocus]="selection.trigger"
        [autoFocus]="'first-heading'"
        (closed)="inspected.set(null)"
      >
        <hlm-dialog-content *hlmDialogPortal [size]="'lg'" data-security-dialog>
          <header hlmDialogHeader>
            <h2 hlmDialogTitle>{{ typeLabel(row.type) }}</h2>
            <div class="flex flex-wrap items-center gap-2">
              <app-status-pill
                [tone]="severityTone(row.severity)"
                [label]="severityLabel(row.severity)"
              /><app-absolute-time [value]="row.created_at" />
            </div>
          </header>
          <dl class="hlm-field-grid text-sm">
            <div class="hlm-field-wide">
              <dt class="text-muted-foreground">{{ copy.id }}</dt>
              <dd class="m-0">
                <app-copyable-identifier [value]="row.id" [label]="copy.id" [truncate]="false" />
              </dd>
            </div>
            <div>
              <dt class="text-muted-foreground">{{ copy.type }}</dt>
              <dd class="m-0 wrap-anywhere">
                <bdi>{{ row.type }}</bdi>
              </dd>
            </div>
            <div [class.hlm-field-wide]="!!row.actor.id">
              <dt class="text-muted-foreground">{{ copy.actor }}</dt>
              <dd class="m-0">
                <bdi>{{ row.actor.kind }}</bdi>
                @if (row.actor.id) {
                  <app-copyable-identifier
                    [value]="row.actor.id"
                    [label]="copy.actor"
                    [truncate]="false"
                  />
                } @else {
                  {{ copy.unavailable }}
                }
              </dd>
            </div>
            <div [class.hlm-field-wide]="!!row.project_id">
              <dt class="text-muted-foreground">{{ copy.project }}</dt>
              <dd class="m-0">
                @if (row.project_id) {
                  <app-copyable-identifier
                    [value]="row.project_id"
                    [label]="copy.project"
                    [truncate]="false"
                  />
                } @else {
                  {{ copy.noProject }}
                }
              </dd>
            </div>
            <div [class.hlm-field-wide]="!!row.request_id">
              <dt class="text-muted-foreground">{{ copy.request }}</dt>
              <dd class="m-0">
                @if (row.request_id) {
                  <app-copyable-identifier
                    [value]="row.request_id"
                    [label]="copy.request"
                    [truncate]="false"
                  />
                } @else {
                  {{ copy.unavailable }}
                }
              </dd>
            </div>
            @if (row.type === 'support_access') {
              <div class="hlm-field-wide">
                <dt class="text-muted-foreground">{{ copy.support }}</dt>
                <dd class="m-0 wrap-anywhere">{{ copy.supportDescription }}</dd>
              </div>
            }
            <div class="hlm-field-wide">
              <dt class="text-muted-foreground">{{ copy.metadata }}</dt>
              <dd class="m-0">
                <pre
                  class="m-0 max-w-full whitespace-pre-wrap wrap-anywhere text-xs"
                ><bdi>{{ row.details | json }}</bdi></pre>
              </dd>
            </div>
          </dl>
        </hlm-dialog-content>
      </hlm-dialog>
    }
    <app-data-table
      class="[&>.caption]:sr-only [&>.scroll]:rounded-none [&>.scroll]:border-0"
      [rowHeight]="'compact'"
      [columns]="columns()"
      [rows]="rows()"
      [rowId]="rowId"
      [caption]="copy.caption"
      [fitToContainer]="true"
    />
  `,
})
export class SecurityEventsTableComponent {
  readonly rows = input.required<readonly SecurityEventRow[]>();
  protected readonly inspected = linkedSignal<
    readonly SecurityEventRow[],
    { row: SecurityEventRow; trigger: HTMLElement } | null
  >({
    source: this.rows,
    computation: () => null,
  });
  protected readonly typeLabel = securityTypeLabel;
  protected readonly rowId = (row: SecurityEventRow): string => row.id;
  protected readonly copy = {
    caption: $localize`:@@settings.security.caption:Recent security activity · newest first`,
    type: $localize`:@@settings.security.type:Event type`,
    severity: $localize`:@@settings.security.severity:Severity`,
    time: $localize`:@@settings.security.time:Time (UTC)`,
    actor: $localize`:@@settings.security.actor:Principal`,
    view: $localize`:@@settings.row.view:View`,
    details: $localize`:@@settings.security.details:Inspect event`,
    id: $localize`:@@settings.security.id:Event id`,
    project: $localize`:@@settings.security.project:Project id`,
    request: $localize`:@@settings.security.request:Request id`,
    metadata: $localize`:@@settings.security.metadata:Event metadata`,
    noProject: $localize`:@@settings.security.no-project:No project`,
    unavailable: $localize`:@@settings.security.unavailable:Not available`,
    support: $localize`:@@settings.security.support:Support access`,
    supportDescription: $localize`:@@settings.security.support-description:Flagged platform staff access. Recorded grant metadata is listed below; expiry is not supplied by this endpoint.`,
  };
  private readonly eventCell =
    viewChild<TemplateRef<DataTableCellContext<SecurityEventRow>>>('eventCell');
  private readonly severityCell =
    viewChild<TemplateRef<DataTableCellContext<SecurityEventRow>>>('severityCell');
  private readonly timeCell =
    viewChild<TemplateRef<DataTableCellContext<SecurityEventRow>>>('timeCell');
  private readonly requestCell =
    viewChild<TemplateRef<DataTableCellContext<SecurityEventRow>>>('requestCell');
  private readonly actorCell =
    viewChild<TemplateRef<DataTableCellContext<SecurityEventRow>>>('actorCell');
  private readonly detailsCell =
    viewChild<TemplateRef<DataTableCellContext<SecurityEventRow>>>('detailsCell');
  protected readonly columns = computed<readonly DataTableColumn<SecurityEventRow>[]>(() => {
    const event = this.eventCell(),
      severity = this.severityCell(),
      time = this.timeCell(),
      request = this.requestCell(),
      actor = this.actorCell(),
      details = this.detailsCell();
    if (!event || !severity || !time || !actor || !request || !details) return [];
    return [
      {
        id: 'event',
        header: this.copy.type,
        role: 'identity',
        width: { min: '14ch', preferred: 'auto', max: '36ch' },
        cell: (row) => securityTypeLabel(row.type),
        cellTemplate: event,
      },
      {
        id: 'severity',
        header: this.copy.severity,
        role: 'status',
        width: { min: '9ch', preferred: '1%', max: '24ch' },
        cell: (row) => row.severity,
        cellTemplate: severity,
      },
      {
        id: 'actor',
        header: this.copy.actor,
        role: 'identity',
        kind: 'identifier',
        width: { min: '10ch', preferred: 'auto', max: '26ch' },
        cell: (row) => row.actor.id ?? '',
        cellTemplate: actor,
      },
      {
        id: 'time',
        header: this.copy.time,
        role: 'date',
        width: { min: '13ch', preferred: '16%', max: '26ch' },
        cell: (row) => row.created_at,
        cellTemplate: time,
      },
      {
        id: 'request',
        header: this.copy.request,
        role: 'text',
        width: { min: '8ch', preferred: '10%', max: '14ch' },
        cell: (row) => row.request_id ?? this.copy.unavailable,
        cellTemplate: request,
      },
      {
        id: 'details',
        header: this.copy.details,
        role: 'actions',
        width: { min: '15ch', preferred: '1%', max: '24ch' },
        cell: () => this.copy.details,
        cellTemplate: details,
      },
    ];
  });
  protected severityLabel(value: string): string {
    return Object.hasOwn(SECURITY_SEVERITY_LABELS, value)
      ? SECURITY_SEVERITY_LABELS[value as keyof typeof SECURITY_SEVERITY_LABELS]
      : value;
  }
  protected severityTone(value: string): StatusTone {
    return value === 'critical'
      ? 'danger'
      : value === 'warning'
        ? 'warning'
        : value === 'info'
          ? 'info'
          : 'neutral';
  }
}
