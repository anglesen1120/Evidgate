import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCardImports } from '@evidgate/ui/card';
import { HlmInput } from '@evidgate/ui/input';
import { HlmLabel } from '@evidgate/ui/label';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow/narrow-mutation-policy';
import { editorCopy } from './editor-copy';
import type {
  DefinitionField,
  EditorScene,
  StudioDraftEditorViewModel,
} from './draft-editor.fixture';
import { EditorPreviewStore } from './editor-preview.store';

@Component({
  selector: 'app-editor-definition',
  imports: [FormsModule, HlmCardImports, HlmInput, HlmLabel, HlmButton],
  providers: [EditorPreviewStore],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'grid min-w-0 gap-3' },
  templateUrl: './editor-definition.component.html',
})
export class EditorDefinitionComponent {
  private readonly edits = inject(EditorPreviewStore);
  private readonly viewport = inject(NarrowMutationPolicy);
  readonly model = input.required<StudioDraftEditorViewModel>();
  readonly state = input.required<EditorScene>();
  protected readonly c = editorCopy;
  protected readonly nonBlank = /\S/;
  protected readonly locked = computed(
    () =>
      !this.viewport.canMutate() ||
      ['403', '412', '429', 'busy', 'running', 'readonly', 'published', 'merged'].includes(
        this.state(),
      ),
  );
  protected readonly fields = computed(() =>
    this.model().fields.map((field) => {
      const original =
        field.id === 'assertion' && this.state() === 'invalid'
          ? ''
          : field.id === 'assertion' && this.state() === 'passed'
            ? this.model().reviewed.assertion
            : field.value;
      return { ...field, original, value: this.edits.edits()[field.id] ?? original };
    }),
  );
  protected readonly status = computed(() =>
    this.state() === 'busy'
      ? this.c.saving
      : this.edits.dirty() || ['dirty', 'invalid', '412'].includes(this.state())
        ? this.c.dirty
        : this.c.saved,
  );
  protected change(id: DefinitionField, original: string, event: Event): void {
    if (this.locked() || !this.viewport.allowsMutation()) return;
    const target = event.target;
    if (target instanceof HTMLInputElement || target instanceof HTMLTextAreaElement) {
      this.edits.change(id, target.value, original);
    }
  }
}
