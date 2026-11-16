import {
  ChangeDetectionStrategy,
  Component,
  computed,
  type TemplateRef,
  viewChild,
} from '@angular/core';
import { HlmBadge } from '@evidgate/ui/badge';
import { HlmCard, HlmCardContent, HlmCardHeader } from '@evidgate/ui/card';

import { HashChipComponent, StatusPillComponent } from '../chips';
import {
  DataTableComponent,
  DataTableRecordsComponent,
  type DataTableCellContext,
  type DataTableColumn,
} from '../data-table';

interface GalleryRecord {
  readonly id: string;
  readonly hash: string;
  readonly note: string;
  readonly attempts: number;
  readonly recordedAt: string;
}

const RECORD_COUNT = 201;

function records(count: number): readonly GalleryRecord[] {
  return Array.from({ length: count }, (_, index) => ({
    id: `gallery-record-${index}`,
    hash: `sha256:gallery-${index}-0123456789abcdef0123456789abcdef`,
    note: `Recorded attempt ${index}: ${'Supplied Vietnamese / English evidence context. '.repeat(3)}`,
    attempts: index,
    recordedAt: `2026-11-${String((index % 28) + 1).padStart(2, '0')}T15:10:00Z`,
  }));
}

/** Render-only specimens for column roles, long in-cell chips and stacked narrow records. */
@Component({
  selector: 'app-gallery-tables',
  imports: [
    HlmBadge,
    HlmCard,
    HlmCardContent,
    HlmCardHeader,
    HashChipComponent,
    StatusPillComponent,
    DataTableComponent,
    DataTableRecordsComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0' },
  template: `
    <section
      class="grid min-w-0 gap-3 border-0 border-t border-solid border-border py-4"
      aria-labelledby="gallery-column-roles"
    >
      <h2
        id="gallery-column-roles"
        class="eg-type-section-title m-0"
        i18n="@@shared.gallery.roles.title"
      >
        Column roles and long in-cell chips
      </h2>
      <app-data-table
        [columns]="roleColumns()"
        [rows]="specimens"
        [rowId]="recordId"
        [caption]="roleCaption"
        [flow]="'page'"
        [fitToContainer]="true"
        [attr.data-demo]="'gallery-column-roles'"
      >
        <ng-template #hashCell let-row>
          <app-hash-chip [value]="row.hash" [abbreviate]="18" />
        </ng-template>
        <ng-template #statusCell>
          <app-status-pill [tone]="'info'" [label]="longStatusLabel" [detail]="longStatusDetail" />
        </ng-template>
      </app-data-table>
      <section hlmCard class="min-w-0" aria-labelledby="gallery-role-badge">
        <div hlmCardHeader>
          <h3
            id="gallery-role-badge"
            class="eg-type-subsection-title m-0"
            i18n="@@shared.gallery.roles.badge"
          >
            Permission and role labels
          </h3>
        </div>
        <div hlmCardContent class="grid min-w-0 gap-2">
          <span hlmBadge [variant]="'secondary'">{{ longRoleLabel }}</span>
          <span hlmBadge [variant]="'outline'">{{ shortRoleLabel }}</span>
        </div>
      </section>
    </section>

    <section
      class="grid min-w-0 gap-3 border-0 border-t border-solid border-border py-4"
      aria-labelledby="gallery-stacked-records"
    >
      <h2
        id="gallery-stacked-records"
        class="eg-type-section-title m-0"
        i18n="@@shared.gallery.records.title"
      >
        Stacked records for narrow screens
      </h2>
      <p class="m-0 text-muted-foreground" i18n="@@shared.gallery.records.description">
        The standalone renderer keeps every supplied fact, label and full value; it never hides a
        column or invents a total.
      </p>
      <app-data-table-records
        [columns]="recordColumns"
        [rows]="stackedRecords"
        [rowId]="recordId"
        [caption]="stackedCaption"
      />
    </section>
  `,
})
export class GalleryTablesComponent {
  protected readonly identityLabel = $localize`:@@shared.gallery.roles.identity:Evidence identity`;
  protected readonly roleCaption = $localize`:@@shared.gallery.roles.caption:Role-bound Helm columns with a long status label and a machine identifier`;
  protected readonly hashCell =
    viewChild<TemplateRef<DataTableCellContext<GalleryRecord>>>('hashCell');
  protected readonly statusCell =
    viewChild<TemplateRef<DataTableCellContext<GalleryRecord>>>('statusCell');
  /** Role-bound columns with intrinsic budgets, so narrow containers reflow instead of hiding one. */
  protected readonly roleColumns = computed<readonly DataTableColumn<GalleryRecord>[]>(() => {
    const hash = this.hashCell();
    const status = this.statusCell();
    return [
      {
        id: 'hash',
        header: this.identityLabel,
        cell: (record) => record.hash,
        role: 'identity',
        kind: 'identifier',
        truncate: true,
        width: { min: '18ch', preferred: '26ch', max: '32ch' },
        ...(hash ? { cellTemplate: hash } : {}),
      },
      {
        id: 'status',
        header: this.statusLabel,
        cell: () => this.longStatusLabel,
        role: 'status',
        width: { preferred: '1%' },
        ...(status ? { cellTemplate: status } : {}),
      },
      {
        id: 'attempts',
        header: this.attemptsLabel,
        cell: (record) => record.attempts,
        role: 'number',
        align: 'end',
        width: { preferred: '1%' },
      },
      {
        id: 'recordedAt',
        header: this.dateLabel,
        cell: (record) => record.recordedAt,
        role: 'date',
        width: { preferred: '1%' },
      },
    ];
  });
  protected readonly statusLabel = $localize`:@@shared.gallery.roles.status:Status`;
  protected readonly attemptsLabel = $localize`:@@shared.gallery.roles.attempts:Attempts`;
  protected readonly dateLabel = $localize`:@@shared.gallery.roles.date:Recorded (UTC)`;
  protected readonly longStatusLabel = $localize`:@@shared.gallery.roles.long-status:Review blocked until supplied evidence is complete`;
  protected readonly longStatusDetail = $localize`:@@shared.gallery.roles.long-status-detail:The complete explanation stays outside the compact chip so the table row keeps its height.`;
  protected readonly longRoleLabel = $localize`:@@shared.gallery.roles.long-role:Project administrator and evidence approver for this environment`;
  protected readonly shortRoleLabel = $localize`:@@shared.gallery.roles.short-role:Reviewer`;
  protected readonly stackedCaption = $localize`:@@shared.gallery.records.caption:Recorded examples: every supplied fact, already-loaded rows`;
  protected readonly recordId = (record: GalleryRecord) => record.id;
  protected readonly recordColumns: readonly DataTableColumn<GalleryRecord>[] = [
    {
      id: 'hash',
      header: $localize`:@@shared.gallery.roles.identity:Evidence identity`,
      cell: (record) => record.hash,
      role: 'identity',
      kind: 'identifier',
      truncate: true,
    },
    {
      id: 'note',
      header: $localize`:@@shared.gallery.roles.note:Context`,
      cell: (record) => record.note,
      role: 'text',
    },
    {
      id: 'attempts',
      header: $localize`:@@shared.gallery.roles.attempts:Attempts`,
      cell: (record) => record.attempts,
      role: 'number',
      align: 'end',
    },
    {
      id: 'recordedAt',
      header: $localize`:@@shared.gallery.roles.date:Recorded (UTC)`,
      cell: (record) => record.recordedAt,
      role: 'date',
    },
  ];
  protected readonly specimens = records(2);
  protected readonly stackedRecords = records(RECORD_COUNT);
}
