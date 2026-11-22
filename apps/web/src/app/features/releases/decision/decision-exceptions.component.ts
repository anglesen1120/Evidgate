import { DatePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  ElementRef,
  inject,
  input,
  LOCALE_ID,
  type TemplateRef,
  viewChild,
} from '@angular/core';
import { patchState, signalStore, withMethods, withState } from '@ngrx/signals';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCard } from '@evidgate/ui/card';
import { HlmDialog, HlmDialogImports } from '@evidgate/ui/dialog';
import { shortDateFormat } from '../../../shared/time/short-date-format';
import { WaiverBanner } from '../../../shared/ui/annotations';
import { StatusPillComponent } from '../../../shared/ui/chips';
import {
  DataTableComponent,
  type DataTableCellContext,
  type DataTableColumn,
} from '../../../shared/ui/data-table';
import { DesktopMutationDirective } from '../../../shared/ui/narrow';
import { CopyableIdentifierComponent } from '../../../shared/ui/values/copyable-identifier.component';
import { ShellIconComponent } from '../../../shell/layout/shell-icon.component';
import { DecisionPreview } from './decision-preview.component';
import { decisionCopy } from './decision-review.copy';
import {
  runFacts,
  type ExceptionRecord,
  type ReleaseDecisionViewModel,
} from './decision-review.fixture';

const RecordSelection = signalStore(
  withState<{ id: ExceptionRecord['id'] | null }>({ id: null }),
  withMethods((store) => ({
    select(id: ExceptionRecord['id']) {
      patchState(store, { id });
    },
  })),
);

type Cell = TemplateRef<DataTableCellContext<ExceptionRecord>>;

@Component({
  selector: 'app-decision-exceptions',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    DatePipe,
    DataTableComponent,
    CopyableIdentifierComponent,
    ShellIconComponent,
    HlmDialogImports,
    HlmButton,
    HlmCard,
    WaiverBanner,
    StatusPillComponent,
    DecisionPreview,
    DesktopMutationDirective,
  ],
  providers: [RecordSelection],
  templateUrl: './decision-exceptions.component.html',
  styles: `
    .exception-dialog {
      inline-size: min(560px, calc(100vw - 2 * var(--eg-space-5)));
      max-inline-size: 100vw;
      max-block-size: calc(100dvh - 2 * var(--eg-space-5));
      grid-template-rows: auto minmax(0, 1fr) auto;
      overflow: hidden;
    }
    @media (width < 600px) {
      .exception-dialog {
        inline-size: 100vw;
        block-size: 100dvh;
        max-block-size: 100dvh;
        border: 0;
        border-radius: 0;
        padding-block: max(env(safe-area-inset-top), var(--eg-space-5))
          max(env(safe-area-inset-bottom), var(--eg-space-5));
        padding-inline: max(env(safe-area-inset-left), var(--eg-space-5))
          max(env(safe-area-inset-right), var(--eg-space-5));
      }
    }
  `,
  host: { class: 'block min-w-0 wrap-anywhere' },
})
export class DecisionExceptions {
  protected readonly selection = inject(RecordSelection);
  private readonly locale = inject(LOCALE_ID);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  readonly view = input.required<ReleaseDecisionViewModel>();
  protected readonly copy = decisionCopy;
  protected readonly facts = runFacts;
  protected readonly dateFormat = shortDateFormat(this.locale, true);
  protected readonly rowId = (record: ExceptionRecord) => record.id;
  private readonly dialog = viewChild.required(HlmDialog);
  private readonly recordCell = viewChild.required<Cell>('recordCell');
  private readonly statusCell = viewChild.required<Cell>('statusCell');
  private readonly scopeCell = viewChild.required<Cell>('scopeCell');
  private readonly expiryCell = viewChild.required<Cell>('expiryCell');
  private readonly viewCell = viewChild.required<Cell>('viewCell');
  protected readonly columns = computed<readonly DataTableColumn<ExceptionRecord>[]>(() => [
    {
      id: 'ref',
      header: this.copy.reference,
      role: 'identity',
      overflow: 'wrap',
      cell: this.rowId,
      cellTemplate: this.recordCell(),
    },
    {
      id: 'status',
      header: this.copy.status,
      role: 'status',
      cell: (r) => r.status,
      cellTemplate: this.statusCell(),
    },
    {
      id: 'scope',
      header: this.copy.scopeOwner,
      priority: 'primary',
      cell: (r) => r.scope,
      cellTemplate: this.scopeCell(),
    },
    {
      id: 'expiry',
      header: this.copy.expires,
      role: 'date',
      cell: (r) => r.expiry,
      cellTemplate: this.expiryCell(),
    },
    {
      id: 'view',
      header: this.copy.actions,
      role: 'actions',
      width: { min: 'max(10ch, calc(var(--eg-control-touch) + 4 * var(--eg-space-4)))' },
      cell: () => this.copy.view,
      cellTemplate: this.viewCell(),
    },
  ]);
  protected readonly current = computed(() =>
    this.view().records.find((r) => r.id === (this.selection.id() ?? this.view().initialRecord)),
  );

  protected openRecord(record: ExceptionRecord, opener: HTMLButtonElement): void {
    this.selection.select(record.id);
    // Pointer activation does not focus a button in every browser. Give CDK the exact opener.
    opener.focus({ preventScroll: true });
    this.dialog().open();
  }

  protected restoreFocusAfterReflow(): void {
    const host = this.host.nativeElement;
    if (!host.isConnected || host.contains(host.ownerDocument.activeElement)) return;
    // Container reflow can replace a table row with its stacked counterpart while the dialog is open.
    const opener = host.querySelector<HTMLElement>(`[data-view="${this.selection.id()}"]`);
    const fallback = opener ?? host.querySelector<HTMLElement>('[data-view]');
    (fallback ?? host.querySelector<HTMLElement>('h2[tabindex]'))?.focus();
  }
}
