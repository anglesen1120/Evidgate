import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { HlmCardImports } from '@evidgate/ui/card';
import { StatusPillComponent } from '../../../shared/ui/chips/status-pill.component';
import { HlmLabel } from '@evidgate/ui/label';
import { HlmInput } from '@evidgate/ui/input';
import type { StudioDraftEditorViewModel } from './draft-editor.fixture';
import { editorCopy } from './editor-copy';

@Component({
  selector: 'app-editor-versions',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HlmCardImports, StatusPillComponent, HlmLabel, HlmInput],
  host: { class: 'grid min-w-0 gap-3' },
  template: `
    <section hlmCard>
      <header hlmCardHeader>
        <h2 hlmCardTitle>{{ c.versions }}</h2>
      </header>
      <div hlmCardContent class="grid min-w-0 gap-4">
        <ul class="m-0 grid list-none gap-2 p-0" [attr.aria-label]="c.revisionState">
          @for (revision of model().revisions; track revision.id + revision.version) {
            <li class="flex flex-wrap items-center gap-2" [attr.data-revision]="revision.version">
              <code>{{ revision.id }} {{ revision.version }}</code>
              <app-status-pill
                [tone]="revision.status === 'approved' ? 'success' : 'neutral'"
                [label]="c[revision.status]"
              />
              @if (revision.status === 'approved') {
                <span class="text-sm text-muted-foreground">{{ c.immutable }}</span>
              }
            </li>
          }
        </ul>
        <p class="m-0 text-sm text-muted-foreground">{{ c.separate }}</p>
        <div class="grid gap-2">
          <h3 class="m-0 text-base font-semibold">{{ c.steps }}</h3>
          <p class="m-0 text-sm">{{ c.stepHint }}</p>
          <ol class="m-0 grid list-decimal gap-1 ps-6">
            @for (step of model().discountSteps; track step.number) {
              <li>
                @if (step.expected; as expected) {
                  <details open>
                    <summary
                      class="min-h-(--eg-control-touch) cursor-pointer content-center font-semibold"
                    >
                      {{ step.action }}
                    </summary>
                    <div class="grid min-w-0 gap-1">
                      <label hlmLabel for="editor-step-result">{{ c.expected }}</label>
                      <textarea
                        hlmInput
                        id="editor-step-result"
                        rows="2"
                        readonly
                        class="h-auto min-w-0 w-full"
                        [value]="expected"
                      ></textarea>
                    </div>
                  </details>
                } @else {
                  <span class="text-muted-foreground">{{ c.stepUnknown }}</span>
                }
              </li>
            }
          </ol>
        </div>
      </div>
    </section>
  `,
})
export class EditorVersionsComponent {
  readonly model = input.required<StudioDraftEditorViewModel>();
  protected readonly c = editorCopy;
}
