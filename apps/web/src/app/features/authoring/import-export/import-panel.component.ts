import { rowsDuringFetch } from '../../../shared/ui/data-table/rows-during-fetch';
import {
  afterRenderEffect,
  ChangeDetectionStrategy,
  Component,
  computed,
  ElementRef,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DOCUMENT } from '@angular/common';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCard, HlmCardContent, HlmCardHeader } from '@evidgate/ui/card';
import { HlmInput } from '@evidgate/ui/input';
import { HlmLabel } from '@evidgate/ui/label';
import { HlmSelectImports } from '@evidgate/ui/select';
import { FormErrorSummaryComponent } from '../../../shared/forms/form-error-summary.component';
import { createFormFeedback } from '../../../shared/forms/form-feedback';
import { DesktopMutationDirective } from '../../../shared/ui/narrow/desktop-mutation.directive';
import { DesktopRequiredNoticeComponent } from '../../../shared/ui/narrow/desktop-required-notice.component';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow/narrow-mutation-policy';
import { ImportHistoryTableComponent } from './import-history-table.component';
import { DataTablePagerComponent } from '../../../shared/ui/data-table/data-table-pager.component';
import { SessionStore } from '../../../core/session/session.store';
import { SectionHeaderComponent } from '../../../shared/ui/page-header/section-header.component';
import { InlineFeedbackComponent } from '../../../shared/ui/states/inline-feedback.component';
import { RequestStateComponent } from '../../../shared/ui/states/request-state.component';
import { CopyableIdentifierComponent } from '../../../shared/ui/values/copyable-identifier.component';
import { NumberValueComponent } from '../../../shared/ui/values/number-value.component';
import {
  IMPEX_COPY,
  importFieldLabel,
  importProblemTarget,
  importReason,
} from './import-export.copy';
import { IMPORT_COLUMNS } from './local-file-preview';
import { ImportPreviewComponent } from './import-preview.component';
import { ImportStore } from './import.store';

@Component({
  selector: 'app-authoring-import-panel',
  imports: [
    FormsModule,
    HlmButton,
    HlmCard,
    HlmCardContent,
    HlmCardHeader,
    ImportHistoryTableComponent,
    DataTablePagerComponent,
    HlmInput,
    HlmLabel,
    HlmSelectImports,
    FormErrorSummaryComponent,
    DesktopMutationDirective,
    DesktopRequiredNoticeComponent,
    SectionHeaderComponent,
    InlineFeedbackComponent,
    RequestStateComponent,
    CopyableIdentifierComponent,
    NumberValueComponent,
    ImportPreviewComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './import-panel.component.html',
  host: { class: 'grid min-w-0 gap-(--eg-space-6)', '(focusin)': 'rememberFocus($event)' },
})
export class ImportPanelComponent {
  protected readonly store = inject(ImportStore);
  protected readonly session = inject(SessionStore);
  private readonly policy = inject(NarrowMutationPolicy);
  private readonly document = inject(DOCUMENT);
  protected readonly copy = IMPEX_COPY;
  protected readonly formats = ['csv', 'json', 'xlsx'] as const;
  protected readonly feedback = signal(createFormFeedback([]));
  protected readonly local = computed(() =>
    this.store.local.hasValue() ? this.store.local.value() : null,
  );
  protected readonly localMessage = computed(() => {
    if (this.store.local.error()) return this.copy.localUnreadable;
    switch (this.local()?.issue) {
      case 'xlsx':
        return this.copy.xlsxPreview;
      case 'display-cap':
        return this.copy.displayCap;
      case 'header':
      case 'unreadable':
        return this.copy.localUnreadable;
      default:
        return null;
    }
  });
  private readonly previewRows = viewChild(ImportPreviewComponent);
  private readonly filePicker = viewChild<ElementRef<HTMLInputElement>>('filePicker');
  private readonly snapshot = viewChild<ElementRef<HTMLElement>>('snapshot');
  private readonly desktopNotice = viewChild(DesktopRequiredNoticeComponent, { read: ElementRef });
  private focused: HTMLElement | null = null;
  protected readonly columns = IMPORT_COLUMNS;
  protected readonly capDetails = computed(() =>
    (this.store.work().error?.details ?? []).filter(
      (detail) => typeof detail.limit === 'number' || typeof detail.actual === 'number',
    ),
  );
  protected readonly headerRefused = computed(
    () => this.store.work().error?.details.some((detail) => detail.path === '/header') ?? false,
  );
  protected readonly history = computed(() => this.store.historyDisplay());
  private readonly historyScope = computed(
    () => {
      const work = this.store.work();
      return this.store.canReadHistory() && work.file && work.project
        ? { file: work.file, project: work.project, batch: work.committed?.batch_id }
        : null;
    },
    { equal: (a, b) => a?.file === b?.file && a?.project === b?.project && a?.batch === b?.batch },
  );
  protected readonly historyRows = rowsDuringFetch(
    this.historyScope,
    this.store.history.isLoading,
    () => this.history()?.page?.items ?? [],
  );
  protected readonly report = computed(
    () => this.store.work().committed ?? this.store.work().report,
  );

  constructor() {
    afterRenderEffect(() => {
      if (!this.policy.narrow()) return;
      const target = this.focused;
      if (target && !target.isConnected && this.document.activeElement === this.document.body) {
        const notice: unknown = this.desktopNotice()?.nativeElement;
        const next =
          this.snapshot()?.nativeElement ?? (notice instanceof HTMLElement ? notice : null);
        next?.focus();
      }
    });
  }

  rememberFocus(event: FocusEvent): void {
    const target = event.target;
    this.focused =
      target instanceof HTMLElement && target.closest('[data-import-mutation]') ? target : null;
  }

  chooseFile(): void {
    if (this.store.canWrite() && this.policy.allowsMutation() && !this.store.work().pending) {
      this.filePicker()?.nativeElement.click();
    }
  }

  protected readonly formatLabel = (format: string): string => format.toUpperCase();

  protected pick(event: Event): void {
    const input = event.target;
    if (!(input instanceof HTMLInputElement)) return;
    this.store.selectFile(input.files?.item(0) ?? null);
    this.feedback.set(createFormFeedback([]));
    input.value = '';
  }

  protected async submit(event?: Event, confirm = false, retry = false): Promise<void> {
    event?.preventDefault();
    if (retry) await this.store.retry();
    else if (confirm) await this.store.confirm();
    else await this.store.preview();
    const error = this.store.work().error;
    const groups = new Map<string, { label: string; messages: string[] }>();
    for (const detail of error?.details ?? []) {
      const id =
        typeof detail.index === 'number'
          ? importProblemTarget(detail.index, detail.path)
          : 'import-file-refusal';
      const label =
        typeof detail.index === 'number'
          ? $localize`:@@authoring.import-export.row-field-label:Row ${detail.index}:INDEX: — ${importFieldLabel(detail.path)}:FIELD:`
          : this.copy.file;
      const group = groups.get(id) ?? { label, messages: [] };
      group.messages.push(importReason(detail.reason));
      groups.set(id, group);
    }
    const entries = [...groups].map(([id, group]) => ({
      id,
      label: group.label,
      error: () => group.messages.join('; '),
    }));
    const feedback = createFormFeedback(entries);
    this.feedback.set(feedback);
    feedback.submit();
  }

  protected revealProblem(event: MouseEvent): void {
    if (event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey)
      return;
    const target = event.target;
    if (!(target instanceof Element)) return;
    const match = /^#import-row-(\d+)-([a-z_]+)$/.exec(
      target.closest('a')?.getAttribute('href') ?? '',
    );
    if (!match?.[1]) return;
    event.preventDefault();
    this.previewRows()?.reveal(Number(match[1]), '/' + match[2]);
  }
}
