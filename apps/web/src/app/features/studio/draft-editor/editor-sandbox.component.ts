import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { HlmCardImports } from '@evidgate/ui/card';
import { StatusPillComponent } from '../../../shared/ui/chips/status-pill.component';
import { HlmButton } from '@evidgate/ui/button';
import { DecisionChipComponent } from '../../../shared/ui/verdict/decision-chip.component';
import type { EditorScene, StudioDraftEditorViewModel } from './draft-editor.fixture';
import { editorCopy } from './editor-copy';

@Component({
  selector: 'app-editor-sandbox',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HlmCardImports, StatusPillComponent, HlmButton, DecisionChipComponent],
  host: { class: 'grid min-w-0 gap-3' },
  template: `
    <section hlmCard>
      <header hlmCardHeader>
        <h2 hlmCardTitle>{{ c.sandbox }}</h2>
      </header>
      <div hlmCardContent class="grid min-w-0 gap-3">
        <div class="flex flex-wrap items-center justify-between gap-2">
          <app-status-pill [tone]="'neutral'" [label]="c.sandboxOnly" />
          <app-status-pill [tone]="result().tone" [label]="result().label" [dimension]="c.status" />
        </div>
        <div class="grid gap-1 border-s-2 border-(--eg-danger-fg) ps-3">
          <strong>{{ c.original }}</strong
          ><span>{{ c.missingReceipt }}</span>
        </div>
        <dl class="m-0 grid min-w-0 gap-3 sm:grid-cols-2 [&_dd]:m-0 [&_dt]:text-muted-foreground">
          <div>
            <dt>{{ c.execution }}</dt>
            <dd>
              <code>{{ model().sandbox.execution }}</code>
            </dd>
          </div>
          <div>
            <dt>{{ c.completeness }}</dt>
            <dd>{{ c.trace }}</dd>
          </div>
          <div>
            <dt>{{ c.originalOutcome }}</dt>
            <dd><app-status-pill [tone]="'danger'" [label]="c.failedOutcome" /></dd>
          </div>
          <div>
            <dt>{{ c.decision }}</dt>
            <dd><app-decision-chip [decision]="'pending'" [compact]="true" /></dd>
          </div>
          <div>
            <dt>{{ c.artifact }}</dt>
            <dd>
              <code>{{ model().sandbox.artifact }}</code>
            </dd>
          </div>
        </dl>
        <p class="m-0 text-sm">{{ c.limits }}</p>
        <button
          hlmBtn
          type="button"
          [variant]="'outline'"
          class="justify-self-start"
          disabled
          [attr.aria-describedby]="'sandbox-preview'"
        >
          {{ c.run }}
        </button>
        <p class="m-0 text-xs text-muted-foreground" id="sandbox-preview">{{ c.previewAction }}</p>
      </div>
    </section>
    <details hlmCard class="min-w-0 p-4">
      <summary class="min-h-(--eg-control-touch) cursor-pointer content-center font-semibold">
        {{ c.script }}
      </summary>
      <pre
        class="m-0 mt-2 min-w-0 whitespace-pre-wrap rounded-md bg-(--eg-surface-raised) p-3 text-sm wrap-anywhere"
      ><code>{{ model().sandbox.script }}</code></pre>
    </details>
  `,
})
export class EditorSandboxComponent {
  readonly model = input.required<StudioDraftEditorViewModel>();
  readonly state = input.required<EditorScene>();
  protected readonly c = editorCopy;
  protected readonly result = computed(() => {
    switch (this.state()) {
      case 'passed':
        return { label: 'dry_passed', tone: 'success' } as const;
      case 'failed':
        return { label: 'dry_failed', tone: 'danger' } as const;
      case 'running':
        return { label: 'dry_running', tone: 'info' } as const;
      default:
        return { label: 'draft', tone: 'neutral' } as const;
    }
  });
}
