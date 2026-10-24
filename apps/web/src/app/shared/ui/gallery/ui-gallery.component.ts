import { ChangeDetectionStrategy, Component } from '@angular/core';

import { LabelChip, MissingReasonList, WaiverBanner, type MissingReason } from '../annotations';
import {
  HashChipComponent,
  RevisionChipComponent,
  RunStatePillComponent,
  StatusPillComponent,
  type RunState,
  type StatusTone,
} from '../chips';
import { DataTableComponent, type DataTableColumn } from '../data-table';
import {
  DATA_TABLE_FIXTURE_COLUMNS,
  dataTableFixtureRows,
  type DataTableFixtureRow,
} from '../data-table/data-table.fixture';
import { DecisionChipComponent, EvidenceBadgeComponent, OutcomePillComponent } from '../verdict';

/** Render-only specimens. T4 owns mounting this page on a dev-only route. */
@Component({
  selector: 'app-ui-gallery',
  standalone: true,
  imports: [
    DecisionChipComponent,
    EvidenceBadgeComponent,
    OutcomePillComponent,
    HashChipComponent,
    RevisionChipComponent,
    RunStatePillComponent,
    StatusPillComponent,
    LabelChip,
    MissingReasonList,
    WaiverBanner,
    DataTableComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './ui-gallery.component.html',
  styleUrl: './ui-gallery.component.css',
})
export class UiGalleryComponent {
  protected readonly runStates: readonly RunState[] = [
    'created',
    'queued',
    'running',
    'collecting',
    'complete',
    'incomplete',
    'canceled',
  ];
  protected readonly statusSamples: readonly { tone: StatusTone; label: string }[] = [
    { tone: 'success', label: $localize`:@@shared.gallery.status-success:Available` },
    { tone: 'info', label: $localize`:@@shared.gallery.status-info:Processing` },
    { tone: 'warning', label: $localize`:@@shared.gallery.status-warning:Needs attention` },
    { tone: 'danger', label: $localize`:@@shared.gallery.status-danger:Blocked` },
    { tone: 'neutral', label: $localize`:@@shared.gallery.status-neutral:Not started` },
  ];
  protected readonly statusDimension = $localize`:@@shared.gallery.status-dimension:Example status`;
  protected readonly longHash =
    'sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef';
  protected readonly originalFailure = $localize`:@@shared.gallery.original-failure:Attempt #1: the checkout button could not be found.`;
  protected readonly reasons: readonly MissingReason[] = [
    {
      code: 'artifact_missing',
      text: $localize`:@@shared.gallery.reason-linked:Review the missing artifact example`,
      href: '#gallery-hashes',
    },
    {
      code: 'producer_missing',
      text: $localize`:@@shared.gallery.reason-plain:The declared producer has not supplied evidence.`,
    },
  ];
  protected readonly tableColumns: readonly DataTableColumn<DataTableFixtureRow>[] =
    DATA_TABLE_FIXTURE_COLUMNS.map((column) => ({
      ...column,
      header: this.columnHeader(column.id),
    }));
  protected readonly smallRows = dataTableFixtureRows(12);
  protected readonly virtualRows = dataTableFixtureRows(250);
  protected readonly emptyRows: readonly DataTableFixtureRow[] = [];
  protected readonly rowId = (row: DataTableFixtureRow) => row.id;
  protected readonly smallCaption = $localize`:@@shared.gallery.table-small:Evidence examples: 12 rows, default density`;
  protected readonly virtualCaption = $localize`:@@shared.gallery.table-virtual:Evidence examples: 250 rows, virtual scrolling`;
  protected readonly emptyCaption = $localize`:@@shared.gallery.table-empty:No evidence examples, compact density`;

  private columnHeader(id: string): string {
    switch (id) {
      case 'hash':
        return $localize`:@@shared.gallery.column-hash:Evidence identity`;
      case 'note':
        return $localize`:@@shared.gallery.column-note:Context`;
      case 'attempts':
        return $localize`:@@shared.gallery.column-attempts:Attempts`;
      case 'mutation':
        return $localize`:@@shared.gallery.column-mutation:Mutation`;
      default:
        return id;
    }
  }
}
