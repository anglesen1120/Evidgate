import { DOCUMENT } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCheckbox } from '@evidgate/ui/checkbox';
import { HlmInput } from '@evidgate/ui/input';
import { HlmLabel } from '@evidgate/ui/label';
import { HlmSelectImports } from '@evidgate/ui/select';
import { DesktopMutationDirective } from '../../../shared/ui/narrow/desktop-mutation.directive';
import { SectionHeaderComponent } from '../../../shared/ui/page-header/section-header.component';
import { InlineFeedbackComponent } from '../../../shared/ui/states/inline-feedback.component';
import { RequestStateComponent } from '../../../shared/ui/states/request-state.component';
import { ExportConfirmDialogComponent } from './export-confirm-dialog.component';
import { ExportStore } from './export.store';
import { IMPEX_COPY } from './import-export.copy';
import { IMPORT_COLUMNS } from './local-file-preview';

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
  protected readonly store = inject(ExportStore);
  protected readonly copy = IMPEX_COPY;
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
    if (!file) return;
    const url = URL.createObjectURL(file.blob);
    const link = this.document.createElement('a');
    link.href = url;
    link.download = file.filename;
    link.hidden = true;
    this.document.body.append(link);
    try {
      link.click();
    } finally {
      link.remove();
      // Let the browser consume the blob navigation before releasing its URL.
      setTimeout(() => URL.revokeObjectURL(url), 0);
    }
  }
}
