import { JsonPipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  input,
  viewChild,
  type TemplateRef,
} from '@angular/core';
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
import { CopyableIdentifierComponent } from '../../../shared/ui/values/copyable-identifier.component';
import { SECURITY_SEVERITY_LABELS, securityTypeLabel } from './security-events.config';

@Component({
  selector: 'app-security-events-table',
  imports: [
    JsonPipe,
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
      <div class="grid min-w-0 gap-2">
        <app-data-table-value [value]="typeLabel(row.type)" [truncate]="!stacked" />
        @if (row.type === 'support_access') {
          <app-status-pill [tone]="'warning'" [label]="copy.support" />
        }
      </div>
    </ng-template>
    <ng-template #severityCell let-row>
      <app-status-pill [tone]="severityTone(row.severity)" [label]="severityLabel(row.severity)" />
    </ng-template>
    <ng-template #timeCell let-row let-stacked="stacked">
      <app-absolute-time [value]="row.created_at" [class.whitespace-nowrap]="!stacked" />
    </ng-template>
    <ng-template #actorCell let-row let-stacked="stacked">
      <div class="grid min-w-0 gap-1">
        @if (row.actor.kind) {
          <app-data-table-value [value]="row.actor.kind" [truncate]="!stacked" />
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
      <details class="min-w-0">
        <summary
          class="min-h-[var(--eg-control-touch)] cursor-pointer content-center text-sm hover:underline focus-visible:outline-[var(--eg-focus-width)] focus-visible:outline-ring"
        >
          {{ copy.details }}
        </summary>
        <dl class="m-0 grid min-w-0 gap-3 text-sm">
          <div>
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
          <div>
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
          <div>
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
            <div>
              <dt class="text-muted-foreground">{{ copy.support }}</dt>
              <dd class="m-0 wrap-anywhere">{{ copy.supportDescription }}</dd>
            </div>
          }
          <div>
            <dt class="text-muted-foreground">{{ copy.metadata }}</dt>
            <dd class="m-0">
              <pre
                class="m-0 max-w-full whitespace-pre-wrap wrap-anywhere text-xs"
              ><bdi>{{ row.details | json }}</bdi></pre>
            </dd>
          </div>
        </dl>
      </details>
    </ng-template>
    <app-data-table
      [columns]="columns()"
      [rows]="rows()"
      [rowId]="rowId"
      [caption]="copy.caption"
      [flow]="'page'"
      [fitToContainer]="true"
    />
  `,
})
export class SecurityEventsTableComponent {
  readonly rows = input.required<readonly SecurityEventRow[]>();
  protected readonly typeLabel = securityTypeLabel;
  protected readonly rowId = (row: SecurityEventRow): string => row.id;
  protected readonly copy = {
    caption: $localize`:@@settings.security.caption:Recent security activity · newest first`,
    type: $localize`:@@settings.security.type:Event type`,
    severity: $localize`:@@settings.security.severity:Severity`,
    time: $localize`:@@settings.security.time:Time (UTC)`,
    actor: $localize`:@@settings.security.actor:Principal`,
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
  private readonly actorCell =
    viewChild<TemplateRef<DataTableCellContext<SecurityEventRow>>>('actorCell');
  private readonly detailsCell =
    viewChild<TemplateRef<DataTableCellContext<SecurityEventRow>>>('detailsCell');
  protected readonly columns = computed<readonly DataTableColumn<SecurityEventRow>[]>(() => {
    const event = this.eventCell(),
      severity = this.severityCell(),
      time = this.timeCell(),
      actor = this.actorCell(),
      details = this.detailsCell();
    if (!event || !severity || !time || !actor || !details) return [];
    return [
      {
        id: 'time',
        header: this.copy.time,
        role: 'date',
        width: { min: '18ch', preferred: '1%', max: '26ch' },
        cell: (row) => row.created_at,
        cellTemplate: time,
      },
      {
        id: 'event',
        header: this.copy.type,
        role: 'identity',
        priority: 'primary',
        width: { min: '20ch', preferred: 'auto', max: '36ch' },
        cell: (row) => securityTypeLabel(row.type),
        cellTemplate: event,
      },
      {
        id: 'severity',
        header: this.copy.severity,
        role: 'status',
        width: { min: '12ch', preferred: '1%', max: '24ch' },
        cell: (row) => row.severity,
        cellTemplate: severity,
      },
      {
        id: 'actor',
        header: this.copy.actor,
        role: 'identity',
        kind: 'identifier',
        width: { min: '12ch', preferred: 'auto', max: '28ch' },
        cell: (row) => row.actor.id ?? '',
        cellTemplate: actor,
      },
      {
        id: 'details',
        header: this.copy.details,
        role: 'actions',
        width: { min: '16ch', preferred: '1%', max: '24ch' },
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
