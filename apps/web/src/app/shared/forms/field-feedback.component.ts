import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import type { FieldFeedback } from './form-feedback';

@Component({
  selector: 'app-field-feedback',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class:
      'grid min-w-0 gap-[var(--eg-space-2)] text-sm leading-[var(--eg-leading-sm)] wrap-anywhere',
  },
  template: `
    @if (field().hint; as hint) {
      <p class="m-0 text-muted-foreground" [id]="field().hintId">{{ hint }}</p>
    }
    <div [attr.aria-live]="field().announceInline() ? 'polite' : null" aria-atomic="true">
      @if (field().correction(); as correction) {
        <p class="m-0 text-[var(--eg-danger-fg)]" [id]="field().errorId">{{ correction }}</p>
      }
    </div>
  `,
})
export class FieldFeedbackComponent {
  readonly field = input.required<FieldFeedback>();
}
