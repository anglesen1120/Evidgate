import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { HlmButton } from '@evidgate/ui/button';
import { HlmDialogImports } from '@evidgate/ui/dialog';
import { HlmInput } from '@evidgate/ui/input';
import { HlmLabel } from '@evidgate/ui/label';
import { MERGE_COPY as c } from './studio-review-merge.copy';
import type { ReviewAction, StudioReviewMergeViewModel } from './studio-review-merge.fixture';

const actions = {
  submit: { label: c.submit, title: c.submitTitle, reason: c.submitDetails },
  merge: { label: c.merge, title: c.mergeTitle, reason: c.mergeDetails },
  approve: { label: c.approve, title: c.approveTitle, reason: c.independentRule },
  changes: { label: c.changes, title: c.changesTitle, reason: c.independentRule },
} as const;

@Component({
  selector: 'app-review-action-preview',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HlmButton, HlmDialogImports, HlmLabel, HlmInput],
  host: { class: 'inline-block min-w-0 max-w-full' },
  template: `
    <hlm-dialog>
      <button
        hlmBtn
        hlmDialogTrigger
        type="button"
        [variant]="primary() ? 'default' : 'outline'"
        class="h-auto min-h-(--eg-control-touch) max-w-full whitespace-normal"
      >
        {{ action().label }} <span class="sr-only">{{ copy.preview }}</span>
      </button>
      <hlm-dialog-content
        *hlmDialogPortal
        [showCloseButton]="false"
        class="max-h-[calc(100dvh-var(--eg-space-8))] min-w-0 overflow-y-auto wrap-anywhere"
      >
        <header hlmDialogHeader>
          <h2 hlmDialogTitle>{{ action().title }}</h2>
          <p hlmDialogDescription>{{ action().reason }}</p>
        </header>
        <dl class="m-0 grid gap-2 text-sm [&_dd]:m-0 [&_dt]:text-muted-foreground">
          @for (fact of view().identity; track fact.label) {
            <div>
              <dt>{{ fact.label }}</dt>
              <dd>{{ fact.value }}</dd>
            </div>
          }
          <div>
            <dt>{{ copy.reviewedHash }}</dt>
            <dd>
              <code>{{ view().hash }}</code>
            </dd>
          </div>
          @if (kind() === 'merge' && view().decision; as decision) {
            <div>
              <dt>{{ copy.approvedBy }}</dt>
              <dd>{{ decision.reviewer }}</dd>
            </div>
            <div>
              <dt>{{ copy.pullRequest }}</dt>
              <dd>
                <code>{{ view().references.repository }}{{ view().references.pullRequest }}</code>
              </dd>
            </div>
          }
        </dl>
        @if (kind() === 'submit' || kind() === 'changes') {
          <label hlmLabel class="grid gap-2">
            {{ kind() === 'submit' ? copy.note : copy.changesNote }}
            <textarea
              hlmInput
              rows="2"
              [required]="kind() === 'submit'"
              [attr.aria-describedby]="'merge-preview-note'"
            ></textarea>
          </label>
          <p id="merge-preview-note" class="m-0 text-sm text-muted-foreground">
            {{ copy.previewNote }}
          </p>
        }
        <p id="merge-preview-only" class="m-0 text-sm">{{ copy.previewReason }}</p>
        <footer hlmDialogFooter>
          <button hlmBtn hlmDialogClose type="button" [variant]="'outline'">
            {{ copy.close }}
          </button>
          <button
            hlmBtn
            type="button"
            data-confirm
            disabled
            [attr.aria-describedby]="'merge-preview-only'"
          >
            {{ action().label }}
          </button>
        </footer>
      </hlm-dialog-content>
    </hlm-dialog>
  `,
})
export class ReviewActionPreviewComponent {
  readonly kind = input.required<ReviewAction>();
  readonly view = input.required<StudioReviewMergeViewModel>();
  readonly primary = input(false);
  protected readonly action = computed(() => actions[this.kind()]);
  protected readonly copy = c;
}
