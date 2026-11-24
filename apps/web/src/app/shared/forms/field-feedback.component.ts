import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { HlmIcon } from '@evidgate/ui/icon';
import type { FieldFeedback } from './form-feedback';

@Component({
  selector: 'app-field-feedback',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HlmIcon],
  host: {
    class:
      'grid min-w-0 gap-[var(--eg-space-2)] text-sm leading-[var(--eg-leading-sm)] wrap-anywhere',
  },
  template: `
    <div [attr.aria-live]="field().announceInline() ? 'polite' : null" aria-atomic="true">
      @if (field().correction(); as correction) {
        <p
          class="m-0 flex items-start gap-(--eg-space-2) text-(--eg-danger-fg)"
          [id]="field().errorId"
        >
          <hlm-icon [name]="'lucideTriangleAlert'" class="size-(--eg-space-5) shrink-0" />
          <span>{{ correction }}</span>
        </p>
      }
    </div>
  `,
})
export class FieldFeedbackComponent {
  readonly field = input.required<FieldFeedback>();
}
