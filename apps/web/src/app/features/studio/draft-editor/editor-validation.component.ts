import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { HlmCardImports } from '@evidgate/ui/card';
import { HlmButton } from '@evidgate/ui/button';
import { StatusPillComponent } from '../../../shared/ui/chips/status-pill.component';
import type { EditorScene, StudioDraftEditorViewModel } from './draft-editor.fixture';
import { editorCopy } from './editor-copy';

@Component({
  selector: 'app-editor-validation',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HlmCardImports, HlmButton, StatusPillComponent],
  host: { class: 'grid min-w-0 gap-3' },
  template: `
    <section hlmCard>
      <header hlmCardHeader class="border-b border-border">
        <h2 hlmCardTitle>{{ c.lint }}</h2>
      </header>
      <ul hlmCardContent class="m-0 grid min-w-0 list-none gap-3">
        @for (problem of model().lint; track problem.label) {
          <li class="grid min-w-0 gap-2">
            <div class="flex flex-wrap items-center justify-between gap-2">
              <strong>{{ problem.label }}</strong
              ><app-status-pill [tone]="'info'" [label]="c.rejected" />
            </div>
            <code class="rounded-md bg-(--eg-surface-raised) p-2">{{ problem.sample }}</code>
          </li>
        }
      </ul>
    </section>
    <section hlmCard>
      <header hlmCardHeader class="border-b border-border">
        <h2 hlmCardTitle>{{ c.ai }}</h2>
      </header>
      <div hlmCardContent class="grid min-w-0 gap-2">
        @switch (state()) {
          @case ('aiempty') {
            <p class="m-0" role="status">{{ c.aiEmpty }}</p>
          }
          @case ('aiout') {
            <p class="m-0" role="status">{{ c.aiOut }}</p>
          }
          @default {
            <div class="flex flex-wrap items-center justify-between gap-2">
              <app-status-pill [tone]="'info'" [label]="c.untrusted" /><code>{{
                model().ai.id
              }}</code>
            </div>
            <code class="rounded-md bg-(--eg-surface-raised) p-2">{{ model().ai.sample }}</code>
            <p class="m-0">{{ c.credit }}</p>
            <div class="flex flex-wrap gap-2">
              <button
                hlmBtn
                type="button"
                [variant]="'outline'"
                disabled
                [attr.aria-describedby]="'ai-preview'"
              >
                {{ c.accept }}
              </button>
              <button
                hlmBtn
                type="button"
                [variant]="'outline'"
                disabled
                [attr.aria-describedby]="'ai-preview'"
              >
                {{ c.reject }}
              </button>
            </div>
            <p id="ai-preview" class="m-0 text-xs text-muted-foreground">{{ c.previewAction }}</p>
          }
        }
        <p class="m-0 text-sm text-muted-foreground">{{ c.aiBlocked }}</p>
      </div>
    </section>
    <section hlmCard>
      <header hlmCardHeader class="border-b border-border">
        <h2 hlmCardTitle>{{ c.boundaries }}</h2>
      </header>
      <p hlmCardContent class="m-0 text-sm text-muted-foreground">{{ c.attribution }}</p>
    </section>
  `,
})
export class EditorValidationComponent {
  readonly model = input.required<StudioDraftEditorViewModel>();
  readonly state = input.required<EditorScene>();
  protected readonly c = editorCopy;
}
