import { computed } from '@angular/core';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';
import type { DefinitionField } from './draft-editor.fixture';

type Edits = Partial<Record<DefinitionField, string>>;
export const EditorPreviewStore = signalStore(
  withState<{ edits: Edits }>({ edits: {} }),
  withComputed(({ edits }) => ({ dirty: computed(() => Object.keys(edits()).length > 0) })),
  withMethods((store) => ({
    change(field: DefinitionField, value: string, original: string): void {
      patchState(store, ({ edits }) => {
        const next = { ...edits };
        if (value === original) delete next[field];
        else next[field] = value;
        return { edits: next };
      });
    },
  })),
);
