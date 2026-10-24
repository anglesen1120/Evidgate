import { ChangeDetectionStrategy, Component, signal } from '@angular/core';

import type { ApiError } from '../../../core/http/api-error';
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
import { ConflictDialogComponent, RetryAfterComponent, type ConflictField } from '../conflict';
import {
  EmptyPatternComponent,
  ErrorPatternComponent,
  ForbiddenPatternComponent,
  LoadingPatternComponent,
  NotFoundPatternComponent,
  RequestStateComponent,
} from '../states';
import { DecisionChipComponent, EvidenceBadgeComponent, OutcomePillComponent } from '../verdict';

/** Isolated specimens; only explicit commands change local demo state. No API calls. */
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
    LoadingPatternComponent,
    EmptyPatternComponent,
    ErrorPatternComponent,
    ForbiddenPatternComponent,
    NotFoundPatternComponent,
    ConflictDialogComponent,
    RetryAfterComponent,
    RequestStateComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './ui-gallery.component.html',
  styleUrl: './ui-gallery.component.css',
})
export class UiGalleryComponent {
  protected readonly conflictOpen = signal(false);
  protected readonly conflictFields: readonly ConflictField[] = [
    {
      id: 'title',
      label: $localize`:@@shared.gallery.conflict-field:Requirement title`,
      local: $localize`:@@shared.gallery.conflict-draft:Checkout requires a verified email`,
      server: null,
    },
  ];
  protected readonly reloadedFields: readonly ConflictField[] = [
    {
      ...this.conflictFields[0]!,
      server: $localize`:@@shared.gallery.conflict-server:Checkout requires email verification`,
    },
  ];
  protected readonly comparedFields = signal(this.conflictFields);
  protected readonly emptyTitle = $localize`:@@shared.gallery.empty-title:No evidence yet`;
  protected readonly emptyDescription = $localize`:@@shared.gallery.empty-description:Open the feature tree to inspect the cases awaiting evidence.`;
  protected readonly emptyActionLabel = $localize`:@@shared.gallery.empty-action:Open feature tree`;
  protected readonly networkError: ApiError = {
    kind: 'network',
    status: 0,
    code: 'network_error',
    message: $localize`:@@shared.gallery.network-message:The request could not reach the server. Your evidence has not been replaced.`,
    requestId: null,
    retryable: true,
    details: [],
    retryAfterSeconds: null,
  };
  protected readonly retryCount = signal(0);

  protected openConflict(): void {
    this.comparedFields.set(this.conflictFields);
    this.conflictOpen.set(true);
  }

  protected compareReloadedExample(): void {
    // Explicitly requested, already-loaded fixture; never pretend a network reload succeeded.
    this.comparedFields.set(this.reloadedFields);
  }

  protected recordRetry(): void {
    this.retryCount.update((count) => count + 1);
  }

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
