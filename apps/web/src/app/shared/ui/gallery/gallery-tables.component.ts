import { ChangeDetectionStrategy, Component } from '@angular/core';
import { HlmBadge } from '@evidgate/ui/badge';
import { HlmCard, HlmCardContent, HlmCardHeader } from '@evidgate/ui/card';
import {
  HlmTable,
  HlmTableColumn,
  HlmTableContainer,
  HlmTd,
  HlmTh,
  HlmTr,
} from '@evidgate/ui/table';

import { HashChipComponent, StatusPillComponent } from '../chips';
import { DataTableRecordsComponent, type DataTableColumn } from '../data-table';

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
    HlmTable,
    HlmTableColumn,
    HlmTableContainer,
    HlmTd,
    HlmTh,
    HlmTr,
    HashChipComponent,
    StatusPillComponent,
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
      <div hlmTableContainer class="min-w-0 overflow-x-auto">
        <table hlmTable class="w-full">
          <caption
            class="px-(--eg-space-4) py-(--eg-space-3) text-start text-muted-foreground"
            i18n="@@shared.gallery.roles.caption"
          >
            Role-bound Helm columns with a long status label and a machine identifier
          </caption>
          <thead>
            <tr hlmTr>
              <th hlmTh scope="col" [hlmTableColumn]="'identity'" [columnPriority]="'primary'">
                {{ identityLabel }}
              </th>
              <th hlmTh scope="col" [hlmTableColumn]="'status'" [columnPriority]="'primary'">
                {{ statusLabel }}
              </th>
              <th hlmTh scope="col" [hlmTableColumn]="'number'">{{ attemptsLabel }}</th>
              <th hlmTh scope="col" [hlmTableColumn]="'date'">{{ dateLabel }}</th>
            </tr>
          </thead>
          <tbody>
            @for (record of specimens; track record.id) {
              <tr hlmTr>
                <td hlmTd [hlmTableColumn]="'identity'" [columnPriority]="'primary'">
                  <app-hash-chip [value]="record.hash" [abbreviate]="18" />
                </td>
                <td hlmTd [hlmTableColumn]="'status'" [columnPriority]="'primary'">
                  <app-status-pill
                    [tone]="'info'"
                    [label]="longStatusLabel"
                    [detail]="longStatusDetail"
                  />
                </td>
                <td hlmTd [hlmTableColumn]="'number'">{{ record.attempts }}</td>
                <td hlmTd [hlmTableColumn]="'date'">{{ record.recordedAt }}</td>
              </tr>
            }
          </tbody>
        </table>
      </div>
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
