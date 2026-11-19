import { DOCUMENT } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  afterRenderEffect,
  computed,
  inject,
  viewChild,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCheckbox } from '@evidgate/ui/checkbox';
import { HlmInput } from '@evidgate/ui/input';
import { HlmLabel } from '@evidgate/ui/label';
import { HlmSelectImports } from '@evidgate/ui/select';
import { DesktopMutationDirective } from '../../../shared/ui/narrow/desktop-mutation.directive';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow/narrow-mutation-policy';
import { DesktopRequiredNoticeComponent } from '../../../shared/ui/narrow/desktop-required-notice.component';
import { SectionHeaderComponent } from '../../../shared/ui/page-header/section-header.component';
import { InlineFeedbackComponent } from '../../../shared/ui/states/inline-feedback.component';
import { RequestStateComponent } from '../../../shared/ui/states/request-state.component';
import { ExportConfirmDialogComponent } from './export-confirm-dialog.component';
import { ExportStore } from './export.store';
import { EXPORT_COPY } from './export.copy';
import type { AuthoringDownload } from './import-export-data.service';
import { IMPEX_COPY } from './import-export.copy';
import { IMPORT_COLUMNS } from './local-file-preview';

/** Keep the object URL alive long enough for Safari and Firefox to start the download. */
const REVOKE_AFTER_MS = 30_000;

/** Saves one server file under the name the server chose, then releases its URL much later. */
export function downloadExportFile(document: Document, file: AuthoringDownload): void {
  const url = URL.createObjectURL(file.blob);
  const link = document.createElement('a');
  link.href = url;
  link.download = file.filename;
  link.hidden = true;
  document.body.append(link);
  try {
    link.click();
  } finally {
    link.remove();
    setTimeout(() => URL.revokeObjectURL(url), REVOKE_AFTER_MS);
  }
}

@Component({
  selector: 'app-authoring-export-panel',
  imports: [
    FormsModule,
    HlmButton,
    HlmCheckbox,
    HlmInput,
    HlmLabel,
    HlmSelectImports,
    DesktopMutationDirective,
    DesktopRequiredNoticeComponent,
    SectionHeaderComponent,
    InlineFeedbackComponent,
    RequestStateComponent,
    ExportConfirmDialogComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './export-panel.component.html',
  host: { class: 'grid min-w-0 gap-(--eg-space-6)' },
})
export class ExportPanelComponent {
  private readonly document = inject(DOCUMENT);
  protected readonly policy = inject(NarrowMutationPolicy);
  protected readonly store = inject(ExportStore);
  protected readonly copy = IMPEX_COPY;
  protected readonly exportCopy = EXPORT_COPY;
  private readonly notice = viewChild<ElementRef<HTMLElement>>('exportNarrowNotice');
  protected readonly formats = ['csv', 'json', 'xlsx'] as const;
  protected readonly columns = IMPORT_COLUMNS;
  protected readonly kinds = [
    { value: 'nodes', label: this.copy.nodes },
    { value: 'items', label: this.copy.items },
    { value: 'cases', label: this.copy.cases },
  ] as const;
  protected readonly included = computed(() =>
    this.kinds
      .filter((kind) => this.store.draft().kinds.includes(kind.value))
      .map((kind) => kind.label)
      .join(', '),
  );

  constructor() {
    // The scope card unmounts with the viewport policy, so a focused control would hand focus to
    // the body; move it to the notice that states the scope is now read-only.
    let allowed = this.policy.canMutate();
    afterRenderEffect({
      write: () => {
        const next = this.policy.canMutate();
        const orphaned = allowed && !next && this.focusWasLost();
        allowed = next;
        if (orphaned) this.notice()?.nativeElement.focus();
      },
    });
  }

  protected readonly formatLabel = (format: string): string =>
    format === 'xlsx' ? this.exportCopy.xlsxOption : format.toUpperCase();

  private focusWasLost(): boolean {
    const active = this.document.activeElement;
    return active === null || active === this.document.body;
  }

  protected toggleKind(kind: 'nodes' | 'items' | 'cases', checked: boolean): void {
    const selected = this.store.draft().kinds;
    this.store.edit({
      kinds: checked
        ? [...selected.filter((value) => value !== kind), kind]
        : selected.filter((value) => value !== kind),
    });
  }

  protected editNode(event: Event): void {
    if (event.target instanceof HTMLInputElement) this.store.edit({ nodeId: event.target.value });
  }

  protected async download(): Promise<void> {
    const file = await this.store.confirmDownload();
    if (file) downloadExportFile(this.document, file);
  }
}
