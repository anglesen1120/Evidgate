import { NgTemplateOutlet } from '@angular/common';
import {
  afterNextRender,
  afterRenderEffect,
  ChangeDetectionStrategy,
  Component,
  computed,
  ElementRef,
  inject,
  Injector,
  linkedSignal,
  signal,
  viewChild,
  type TemplateRef,
} from '@angular/core';
import type { AuthoringVersionListItemDto } from '@evidgate/sdk';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCardTitle } from '@evidgate/ui/card';
import { HlmDialogImports } from '@evidgate/ui/dialog';
import { HlmInput } from '@evidgate/ui/input';
import { HlmLabel } from '@evidgate/ui/label';
import { RequestStateComponent } from '../../../shared/ui/states/request-state.component';
import { CopyableIdentifierComponent } from '../../../shared/ui/values/copyable-identifier.component';
import { AbsoluteTimeComponent } from '../../../shared/time/absolute-time.component';
import {
  DataTableComponent,
  type DataTableColumn,
  type DataTableCellContext,
} from '../../../shared/ui/data-table';
import { DataTableValueComponent } from '../../../shared/ui/data-table/data-table-value.component';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow';
import { errorOf, REASON_UNAVAILABLE, WRITE_REASON } from './editor.model';
import { EditorStore } from './editor.store';
import { TimelineStore } from './timeline.store';
import { VersionDiffComponent } from './version-diff.component';

@Component({
  selector: 'app-editor-timeline',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    HlmButton,
    HlmCardTitle,
    HlmDialogImports,
    HlmInput,
    HlmLabel,
    RequestStateComponent,
    CopyableIdentifierComponent,
    AbsoluteTimeComponent,
    DataTableComponent,
    DataTableValueComponent,
    NgTemplateOutlet,
    VersionDiffComponent,
  ],
  providers: [TimelineStore],
  templateUrl: './timeline.component.html',
  host: { class: 'block min-w-0' },
})
export class TimelineComponent {
  protected readonly store = inject(TimelineStore);
  protected readonly editor = inject(EditorStore);
  protected readonly viewport = inject(NarrowMutationPolicy);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);
  protected readonly filter = signal('');
  protected readonly confirmation = linkedSignal<
    { narrow: boolean; selected: string | undefined },
    string | null
  >({
    source: () => ({ narrow: this.viewport.narrow(), selected: this.store.selected() }),
    computation: (source, previous) =>
      source.narrow || source.selected !== previous?.source.selected
        ? null
        : (previous?.value ?? null),
  });
  protected readonly errorOf = errorOf;
  protected readonly reasonUnavailable = REASON_UNAVAILABLE;
  protected readonly writeReason = WRITE_REASON;
  protected readonly historyTitle = $localize`:@@authoring.editor.timeline-tab:Timeline`;
  protected readonly noMatch = $localize`:@@authoring.editor.history-no-match:No loaded versions match this filter`;
  protected readonly empty = $localize`:@@authoring.editor.history-empty:No versions recorded`;
  protected readonly noReason = $localize`:@@authoring.editor.no-reason:No reason recorded`;
  protected readonly noAuthor = $localize`:@@authoring.editor.no-author:Author membership unavailable`;
  protected readonly versionLabel = $localize`:@@authoring.editor.version-id:Version id`;
  protected readonly authorLabel = $localize`:@@authoring.editor.author-membership:Author membership`;
  protected readonly etagLabel = $localize`:@@authoring.editor.version-etag:Snapshot ETag`;
  protected readonly contentLabel = $localize`:@@authoring.editor.snapshot-content:Stored snapshot`;
  protected readonly rows = computed(() => {
    const filter = this.filter().toLocaleLowerCase();
    return this.store
      .rows()
      .filter((row) => (row.reason ?? '').toLocaleLowerCase().includes(filter));
  });
  protected readonly historyStatus = computed(() => {
    const error = errorOf(this.store.history.error());
    if (
      error?.kind === 'forbidden' ||
      error?.kind === 'not_found' ||
      error?.kind === 'unauthenticated'
    )
      return 'error';
    return this.store.rows().length
      ? 'ready'
      : this.store.history.isLoading()
        ? 'loading'
        : this.store.history.error()
          ? 'error'
          : 'ready';
  });
  protected readonly blocked = computed(
    () =>
      !this.editor.canWrite() ||
      !this.viewport.canMutate() ||
      this.store.restoring() ||
      !this.store.retryWait.ready() ||
      this.editor.locked(),
  );
  private readonly heading = viewChild<ElementRef<HTMLElement>>('timelineHeading');
  private readonly restoreStatus = viewChild<ElementRef<HTMLElement>>('restoreStatus');
  private readonly table = viewChild(DataTableComponent<AuthoringVersionListItemDto>);
  private readonly metadata =
    viewChild<TemplateRef<DataTableCellContext<AuthoringVersionListItemDto>>>('metadata');
  private readonly actions =
    viewChild<TemplateRef<DataTableCellContext<AuthoringVersionListItemDto>>>('actions');
  private opener: HTMLElement | null = null;
  private restoredJustNow = false;
  private readonly restoredFocus = signal<number | null>(null);
  protected readonly virtualRows = computed(() => this.table()?.virtual() ?? false);
  protected readonly rowId = (row: AuthoringVersionListItemDto) => row.version_id;
  protected readonly columns = computed<readonly DataTableColumn<AuthoringVersionListItemDto>[]>(
    () => {
      const metadata = this.metadata();
      const actions = this.actions();
      return [
        { id: 'version', header: this.versionLabel, cell: (row) => row.version_no, role: 'number' },
        {
          id: 'metadata',
          header: this.contentLabel,
          cell: (row) => row.reason ?? this.noReason,
          role: 'text',
          priority: 'primary',
          ...(metadata ? { cellTemplate: metadata } : {}),
        },
        {
          id: 'actions',
          header: $localize`:@@authoring.editor.inspect-column:Inspection`,
          cell: () => '',
          role: 'actions',
          ...(actions ? { cellTemplate: actions } : {}),
        },
      ];
    },
  );
  private dialogWasOpen = false;
  constructor() {
    afterRenderEffect(() => {
      const version = this.restoredFocus();
      const loading = this.store.history.isLoading();
      const rows = this.store.rows();
      if (version !== null && !loading) this.focusAppended(version, rows);
      const open = !!this.confirmation();
      if (this.viewport.narrow() && this.dialogWasOpen) this.heading()?.nativeElement.focus();
      this.dialogWasOpen = open;
    });
  }
  private focusAppended(version: number, rows: readonly AuthoringVersionListItemDto[]): void {
    const host = this.host.nativeElement;
    const row = rows.find((entry) => entry.version_no === version);
    if (!row) {
      // The appended row never arrived, so the visible refresh status is the reachable outcome.
      if (!this.store.refreshRequired()) return;
      this.restoreStatus()?.nativeElement.focus();
      this.restoredFocus.set(null);
      return;
    }
    this.table()?.revealRow(rows.findIndex((entry) => entry.version_id === row.version_id));
    const headings = [...host.querySelectorAll<HTMLElement>('[data-version-heading]')];
    const target =
      headings.find((element) => element.dataset['versionHeading'] === String(version)) ??
      [...host.querySelectorAll<HTMLElement>('[data-row-id]')].find(
        (element) => element.dataset['rowId'] === row.version_id,
      );
    if (!target) return;
    target.focus();
    this.restoredFocus.set(null);
  }
  protected source(source: string): string {
    switch (source) {
      case 'manual':
        return $localize`:@@authoring.editor.source-manual:Manual`;
      case 'ai':
        return $localize`:@@authoring.editor.source-ai:AI`;
      case 'import':
        return $localize`:@@authoring.editor.source-import:Import`;
      default:
        return source;
    }
  }
  protected changeKind(kind: string): string {
    switch (kind) {
      case 'create':
        return $localize`:@@authoring.editor.change-create:Created`;
      case 'update':
        return $localize`:@@authoring.editor.change-update:Updated`;
      case 'delete':
        return $localize`:@@authoring.editor.change-delete:Deleted`;
      case 'restore':
        return $localize`:@@authoring.editor.change-restore:Restored`;
      case 'import':
        return $localize`:@@authoring.editor.source-import:Import`;
      default:
        return kind;
    }
  }
  protected snapshot(value: unknown): string {
    return JSON.stringify(value, null, 2);
  }
  protected openRestore(event: MouseEvent): void {
    const version = this.store.versionDetail();
    if (!version || this.blocked() || !this.viewport.allowsMutation()) return;
    this.opener = event.currentTarget instanceof HTMLElement ? event.currentTarget : null;
    this.confirmation.set(version.view.version_id);
  }
  hasOpenConfirmation(): boolean {
    return this.confirmation() !== null;
  }
  protected closed(): void {
    // A successful restore moves focus to the appended row; only Cancel and Escape return here.
    if (this.restoredJustNow) {
      this.restoredJustNow = false;
      return;
    }
    if (this.viewport.narrow()) this.heading()?.nativeElement.focus();
    else if (this.opener?.isConnected) this.opener.focus();
  }
  private holdFocusDuringRestore(): void {
    if (!this.store.restoring()) return;
    // Confirmation disables itself while the request runs; keep focus on the dialog progress
    // status so the control the visitor activated never drops focus to the document body.
    afterNextRender(
      () => {
        if (this.store.restoring()) this.restoreStatus()?.nativeElement.focus();
      },
      { injector: this.injector },
    );
  }
  protected async restore(): Promise<void> {
    const id = this.confirmation();
    if (!id || this.blocked() || !this.viewport.allowsMutation()) return;
    const attempt = this.store.restore(id);
    this.holdFocusDuringRestore();
    if (await attempt) {
      this.restoredJustNow = true;
      this.restoredFocus.set(this.store.appendedVersion());
      this.confirmation.set(null);
    }
  }
}
