import { formatNumber, NgTemplateOutlet } from '@angular/common';
import {
  afterNextRender,
  ChangeDetectionStrategy,
  Component,
  computed,
  ElementRef,
  inject,
  Injector,
  input,
  LOCALE_ID,
  TemplateRef,
  viewChild,
} from '@angular/core';
import type { AuthoringChangeListItem } from '@evidgate/sdk';
import { HlmButton } from '@evidgate/ui/button';
import { HlmDialogImports } from '@evidgate/ui/dialog';
import { AbsoluteTimeComponent } from '../../../shared/time/absolute-time.component';
import { StatusPillComponent } from '../../../shared/ui/chips/status-pill.component';
import {
  DataTableComponent,
  type DataTableColumn,
  type DataTableCellContext,
} from '../../../shared/ui/data-table';
import { RowActionComponent } from '../../../shared/ui/data-table/row-action.component';
import { CopyableIdentifierComponent } from '../../../shared/ui/values/copyable-identifier.component';
import { NumberValueComponent } from '../../../shared/ui/values/number-value.component';
import { PENDING_COPY, isStale, isStructural, rowBlockedReason } from './pending-changes.copy';
import { PendingChangesStore } from './pending-changes.store';
import { PENDING_UI, operationLabel } from './pending-changes.ui-copy';

type ChangeCell = TemplateRef<DataTableCellContext<AuthoringChangeListItem>>;

@Component({
  selector: 'app-pending-change-table',
  imports: [
    HlmButton,
    HlmDialogImports,
    NgTemplateOutlet,
    DataTableComponent,
    RowActionComponent,
    StatusPillComponent,
    CopyableIdentifierComponent,
    NumberValueComponent,
    AbsoluteTimeComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0' },
  templateUrl: './pending-change-table.component.html',
  styleUrl: './pending-change-table.component.css',
})
export class PendingChangeTableComponent {
  protected readonly store = inject(PendingChangesStore);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);
  private readonly locale = inject(LOCALE_ID);
  readonly rows = input.required<readonly AuthoringChangeListItem[]>();
  readonly caption = input.required<string>();
  readonly selection = input<ChangeCell>();
  readonly more = input<ChangeCell>();
  protected readonly orderedCaption = computed(() => {
    const caption = this.caption();
    return $localize`:@@authoring.pending-changes.ordered-caption:${caption}:caption: — Oldest first (server order)`;
  });
  protected readonly ui = PENDING_UI;
  protected readonly copy = PENDING_COPY;
  protected readonly rowId = (row: AuthoringChangeListItem) => row.change_id;
  protected readonly operationLabel = operationLabel;
  protected readonly rowBlockedReason = rowBlockedReason;
  protected readonly active = computed(() => {
    const row = this.store.previewRow();
    return this.store.canRead() && row && this.rows().includes(row) ? row : null;
  });
  private readonly state = viewChild<ChangeCell>('state');
  private readonly identity = viewChild<ChangeCell>('identity');
  private readonly actions = viewChild<ChangeCell>('actions');
  private readonly table = viewChild(DataTableComponent<AuthoringChangeListItem>);
  protected readonly footer = computed(() => {
    const count = this.rows().length;
    const total = formatNumber(count, this.locale);
    const table = this.table();
    if (!count || !table || count > table.virtualThreshold()) return `${this.ui.loaded}: ${total}`;
    const first = formatNumber(1, this.locale);
    return $localize`:@@shared.data-table.loaded-range:Loaded records ${first}:start:–${total}:end: of ${total}:loaded:`;
  });
  private opener: { button: HTMLElement; id: string; index: number } | null = null;
  protected readonly columns = computed<readonly DataTableColumn<AuthoringChangeListItem>[]>(() => {
    const selection = this.selection();
    const identity = this.identity(),
      state = this.state(),
      actions = this.actions();
    if (!identity || !state || !actions) return [];
    return [
      ...(selection
        ? [
            {
              id: 'selection',
              header: $localize`:@@authoring.pending-changes.select-column:Select`,
              cell: () => '',
              role: 'actions' as const,
              cellTemplate: selection,
            },
          ]
        : []),
      {
        id: 'operation',
        header: this.ui.operation,
        cell: (row) => operationLabel(row.change_type),
        role: 'identity',
        cellTemplate: identity,
      },
      {
        id: 'state',
        header: this.ui.state,
        role: 'status',
        cell: (row) => this.stateLabel(row),
        cellTemplate: state,
      },
      {
        id: 'reason',
        header: $localize`:@@authoring.review-context.reason:Change reason`,
        cell: (row) => row.reason ?? this.copy.reasonUnset,
        role: 'text',
        priority: 'primary',
        overflow: 'truncate',
      },
      {
        id: 'actions',
        header: this.ui.actions,
        role: 'actions',
        cell: () => this.ui.view,
        virtualWidth: this.more() ? '16ch' : '10ch',
        cellTemplate: actions,
      },
    ];
  });
  protected stateLabel(row: AuthoringChangeListItem): string {
    if (isStale(row)) return this.ui.stale;
    if (isStructural(row)) return this.ui.unavailable;
    switch (row.state) {
      case 'pending':
        return this.ui.pending;
      case 'applied':
        return $localize`:@@authoring.review-context.applied:Applied`;
      case 'discarded':
        return $localize`:@@authoring.review-context.discarded:Discarded`;
      default:
        return row.state;
    }
  }
  protected inspect(row: AuthoringChangeListItem, button: HTMLElement): void {
    this.opener = { button, id: row.change_id, index: this.rows().indexOf(row) };
    this.store.setPreview(row.change_id);
  }
  protected restoreOpener(): void {
    const opener = this.opener;
    this.opener = null;
    this.store.setPreview(null);
    if (!opener || !this.store.canRead()) return;
    if (opener.button.isConnected && opener.button.dataset['pendingChangeId'] === opener.id) {
      opener.button.focus();
      return;
    }
    const rows = this.rows();
    const current = rows.findIndex((row) => row.change_id === opener.id);
    const index = current >= 0 ? current : Math.min(opener.index, rows.length - 1);
    if (index < 0) return;
    this.table()?.revealRow(index);
    afterNextRender(
      () => {
        const buttons =
          this.host.nativeElement.querySelectorAll<HTMLButtonElement>('[data-pending-preview]');
        (
          [...buttons].find((item) => item.dataset['pendingChangeId'] === rows[index]?.change_id) ??
          buttons[0]
        )?.focus();
      },
      { injector: this.injector },
    );
  }
}
