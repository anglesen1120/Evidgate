import {
  ChangeDetectionStrategy,
  Component,
  computed,
  ElementRef,
  inject,
  input,
  signal,
  type TemplateRef,
  viewChild,
} from '@angular/core';
import type { AuthoringVersionListItemDto } from '@evidgate/sdk';
import { HlmButton } from '@evidgate/ui/button';
import { HlmDialogImports } from '@evidgate/ui/dialog';
import { AbsoluteTimeComponent } from '../../../shared/time/absolute-time.component';
import { RevisionChipComponent } from '../../../shared/ui/chips/revision-chip.component';
import {
  DataTableComponent,
  type DataTableCellContext,
  type DataTableColumn,
} from '../../../shared/ui/data-table';
import { RowActionComponent } from '../../../shared/ui/data-table/row-action.component';
import { CopyableIdentifierComponent } from '../../../shared/ui/values/copyable-identifier.component';
import { escapeFormatChars } from '../../../shared/ui/values/ref-name-display';
import { IMPEX_COPY } from './import-export.copy';

type Version = AuthoringVersionListItemDto;
type Cell = TemplateRef<DataTableCellContext<Version>>;

function template(cellTemplate: Cell | undefined) {
  return cellTemplate ? { cellTemplate } : {};
}

@Component({
  selector: 'app-import-history-table',
  imports: [
    AbsoluteTimeComponent,
    RevisionChipComponent,
    CopyableIdentifierComponent,
    DataTableComponent,
    RowActionComponent,
    HlmButton,
    HlmDialogImports,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0' },
  templateUrl: './import-history-table.component.html',
})
export class ImportHistoryTableComponent {
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  readonly rows = input.required<readonly Version[]>();
  protected readonly selectedId = signal<string | null>(null);
  private opener: HTMLElement | null = null;
  protected readonly selected = computed(() =>
    this.rows().find((row) => row.version_id === this.selectedId()),
  );
  protected readonly copy = IMPEX_COPY;
  protected readonly rowId = (row: Version) => row.version_id;
  protected readonly utc = $localize`:@@authoring.import-export.version-time:Created at (UTC)`;
  protected readonly hash = $localize`:@@authoring.ai-settings.hash:Content hash`;
  protected readonly run = $localize`:@@authoring.import-export.ai-run:AI run`;
  private readonly revision = viewChild<Cell>('revision');
  private readonly entity = viewChild<Cell>('entity');
  private readonly author = viewChild<Cell>('author');
  private readonly time = viewChild<Cell>('time');
  private readonly action = viewChild<Cell>('action');
  protected readonly columns = computed<readonly DataTableColumn<Version>[]>(() => [
    {
      id: 'version',
      header: this.copy.version,
      role: 'number',
      align: 'end',
      cell: (row) => row.version_no,
      ...template(this.revision()),
    },
    {
      id: 'entity',
      header: this.copy.entity,
      role: 'identity',
      priority: 'primary',
      cell: (row) => row.entity_id,
      ...template(this.entity()),
    },
    { id: 'source', header: this.copy.source, role: 'text', cell: (row) => row.source },
    { id: 'change', header: this.copy.change, role: 'text', cell: (row) => row.change_kind },
    {
      id: 'author',
      header: this.copy.author,
      role: 'identity',
      priority: 'secondary',
      cell: (row) => row.author ?? this.copy.notSupplied,
      ...template(this.author()),
    },
    {
      id: 'reason',
      header: this.copy.reason,
      role: 'text',
      cell: (row) => row.reason ?? this.copy.notSupplied,
    },
    {
      id: 'time',
      header: this.utc,
      role: 'date',
      cell: (row) => row.created_at,
      ...template(this.time()),
    },
    {
      id: 'actions',
      header: $localize`:@@authoring.import-export.version-actions:Actions`,
      role: 'actions',
      cell: () => '',
      ...template(this.action()),
    },
  ]);

  protected name(row: Version): string {
    return $localize`:@@authoring.import-export.view-version:View version ${row.version_no}:VERSION: of ${escapeFormatChars(row.entity_id)}:ENTITY:`;
  }
  protected inspect(row: Version, opener: HTMLElement): void {
    this.opener = opener;
    this.selectedId.set(row.version_id);
  }
  protected restoreFocus(): void {
    const opener = this.opener;
    const root = this.host.nativeElement;
    const key = opener?.getAttribute('data-import-version-view');
    const target = opener?.isConnected
      ? opener
      : (key &&
          root.querySelector<HTMLElement>(`[data-import-version-view="${CSS.escape(key)}"]`)) ||
        root.querySelector<HTMLElement>('[data-import-version-view]') ||
        root.closest('section')?.querySelector<HTMLElement>('#import-history-title');
    if (target && !target.hasAttribute('data-import-version-view')) target.tabIndex = -1;
    target?.focus();
    this.opener = null;
  }
}
