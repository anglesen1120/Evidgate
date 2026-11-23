import {
  ChangeDetectionStrategy,
  Component,
  computed,
  input,
  linkedSignal,
  output,
  type TemplateRef,
  viewChild,
} from '@angular/core';
import type { AuditEntryDto } from '@evidgate/sdk';
import { HlmButton } from '@evidgate/ui/button';
import { HlmDialogImports } from '@evidgate/ui/dialog';
import type { ApiError } from '../../../core/http/api-error';
import { AbsoluteTimeComponent } from '../../../shared/time/absolute-time.component';
import { DataTableComponent, type DataTableColumn } from '../data-table/data-table.component';
import type { DataTableCellContext } from '../data-table/data-table-column';
import { CopyableIdentifierComponent } from '../values/copyable-identifier.component';
import { DashboardWidgetComponent } from './dashboard-widget.component';

@Component({
  selector: 'app-dashboard-activity',
  imports: [
    HlmButton,
    HlmDialogImports,
    AbsoluteTimeComponent,
    DataTableComponent,
    CopyableIdentifierComponent,
    DashboardWidgetComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0' },
  template: `
    <app-dashboard-widget
      [title]="title"
      [description]="description"
      [loading]="loading()"
      [error]="error()"
      [empty]="!loading() && !error() && rows().length === 0"
      [emptyTitle]="empty"
      (retry)="retry.emit()"
    >
      <ng-template #timeCell let-row><app-absolute-time [value]="row.created_at" /></ng-template>
      <ng-template #actionCell let-row>
        <button
          hlmBtn
          #trigger
          [variant]="'ghost'"
          [attr.aria-haspopup]="'dialog'"
          [attr.aria-label]="viewLabel(row.action)"
          (click)="inspected.set({ row, trigger })"
          i18n="@@shared.dashboard.activity-view"
        >
          View
        </button>
      </ng-template>
      <app-data-table
        [rows]="rows()"
        [columns]="columns()"
        [rowId]="rowId"
        [caption]="title"
        [flow]="'page'"
        [fitToContainer]="true"
      />
    </app-dashboard-widget>
    @if (inspected(); as selection) {
      <hlm-dialog
        [state]="'open'"
        [restoreFocus]="selection.trigger"
        [autoFocus]="'first-heading'"
        (closed)="inspected.set(null)"
      >
        <hlm-dialog-content *hlmDialogPortal [size]="'md'">
          <header hlmDialogHeader>
            <h2 hlmDialogTitle i18n="@@shared.dashboard.activity-details">Organization event</h2>
            <p hlmDialogDescription>
              <bdi>{{ selection.row.action }}</bdi>
            </p>
          </header>
          <dl class="grid min-w-0 gap-4 text-sm">
            <div>
              <dt i18n="@@shared.dashboard.activity-time">Time (UTC)</dt>
              <dd><app-absolute-time [value]="selection.row.created_at" /></dd>
            </div>
            <div>
              <dt i18n="@@shared.dashboard.activity-id">Event ID</dt>
              <dd>
                <app-copyable-identifier
                  [value]="selection.row.id"
                  [label]="title"
                  [truncate]="false"
                />
              </dd>
            </div>
            <div>
              <dt i18n="@@shared.dashboard.activity-actor">Actor type</dt>
              <dd>
                <bdi>{{ selection.row.actor_type }}</bdi>
              </dd>
            </div>
            @if (selection.row.object_id; as id) {
              <div>
                <dt i18n="@@shared.dashboard.activity-object">Object ID</dt>
                <dd><app-copyable-identifier [value]="id" [label]="title" [truncate]="false" /></dd>
              </div>
            }
          </dl>
        </hlm-dialog-content>
      </hlm-dialog>
    }
  `,
})
export class DashboardActivityComponent {
  readonly rows = input.required<readonly AuditEntryDto[]>();
  readonly loading = input(false);
  readonly error = input<ApiError | null>(null);
  readonly retry = output<void>();
  protected readonly title = $localize`:@@shared.dashboard.activity-title:Organization activity`;
  protected readonly description = $localize`:@@shared.dashboard.activity-description:Most recent organization events. Not filtered by project.`;
  protected readonly empty = $localize`:@@shared.dashboard.activity-empty:No organization activity recorded.`;
  protected readonly rowId = (row: AuditEntryDto): string => row.id;
  protected readonly inspected = linkedSignal<
    readonly AuditEntryDto[],
    { row: AuditEntryDto; trigger: HTMLElement } | null
  >({ source: () => this.rows(), computation: () => null });
  private readonly timeCell =
    viewChild<TemplateRef<DataTableCellContext<AuditEntryDto>>>('timeCell');
  private readonly actionCell =
    viewChild<TemplateRef<DataTableCellContext<AuditEntryDto>>>('actionCell');
  protected readonly columns = computed<readonly DataTableColumn<AuditEntryDto>[]>(() => {
    const time = this.timeCell();
    const action = this.actionCell();
    return [
      {
        id: 'action',
        header: $localize`:@@shared.dashboard.activity-action:Action`,
        role: 'identity',
        cell: (row) => row.action,
      },
      ...(time
        ? [
            {
              id: 'time',
              header: $localize`:@@shared.dashboard.activity-time:Time (UTC)`,
              role: 'date' as const,
              cell: () => '',
              cellTemplate: time,
            },
          ]
        : []),
      ...(action
        ? [
            {
              id: 'view',
              header: $localize`:@@shared.dashboard.activity-details-column:Details`,
              role: 'actions' as const,
              cell: () => '',
              width: {
                min: 'calc(14ch + var(--eg-space-4) * 2)',
                preferred: '1%',
                max: 'calc(14ch + var(--eg-space-4) * 2)',
              },
              cellTemplate: action,
            },
          ]
        : []),
    ];
  });
  protected viewLabel(action: string): string {
    return $localize`:@@shared.dashboard.activity-view-label:View event ${action}:action:`;
  }
}
