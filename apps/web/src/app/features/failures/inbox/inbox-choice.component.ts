import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { HlmLabel } from '@evidgate/ui/label';
import { HlmSelectImports } from '@evidgate/ui/select';
import { INBOX_COPY } from './failure-inbox.copy';

type Choice = keyof typeof INBOX_COPY;

@Component({
  selector: 'app-inbox-choice',
  imports: [HlmSelectImports, HlmLabel],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'grid min-w-0 gap-1' },
  template: `
    <label hlmLabel [for]="fieldId()">{{ label() }}</label>
    <hlm-select
      [value]="value()"
      [disabled]="disabled()"
      [itemToString]="display"
      (valueChange)="choose($event)"
    >
      <hlm-select-trigger
        [buttonId]="fieldId()"
        class="w-full min-w-0 whitespace-normal pointer-coarse:min-h-(--eg-control-touch)"
      >
        <hlm-select-value />
      </hlm-select-trigger>
      <hlm-select-content *hlmSelectPortal [aria-label]="label()">
        @for (key of options(); track key) {
          <hlm-select-item [value]="key">{{ copy[key] }}</hlm-select-item>
        }
      </hlm-select-content>
    </hlm-select>
  `,
})
export class InboxChoiceComponent {
  readonly fieldId = input.required<string>();
  readonly label = input.required<string>();
  readonly value = input.required<string>();
  readonly options = input.required<readonly Choice[]>();
  readonly disabled = input(false);
  readonly changed = output<Choice>();
  protected readonly copy = INBOX_COPY;
  protected readonly display = (key: unknown): string => {
    const match = this.options().find((option) => option === key);
    return match ? this.copy[match] : '';
  };
  protected choose(value: unknown): void {
    const choice = this.options().find((option) => option === value);
    if (choice !== undefined && !this.disabled()) this.changed.emit(choice);
  }
}
