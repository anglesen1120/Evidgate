import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

export type FormRequirementConvention = 'mostly-required' | 'mostly-optional';

@Component({
  selector: 'app-form-required-note',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0 text-sm leading-[var(--eg-leading-sm)] text-muted-foreground' },
  template: `<p class="m-0 wrap-anywhere">{{ note() }}</p>`,
})
export class FormRequiredNoteComponent {
  readonly convention = input<FormRequirementConvention>('mostly-required');
  protected readonly note = computed(() =>
    this.convention() === 'mostly-required'
      ? $localize`:@@shared.forms.required-note.mostly-required:All fields required unless marked optional.`
      : $localize`:@@shared.forms.required-note.mostly-optional:All fields optional unless marked required.`,
  );
}

@Component({
  selector: 'app-field-requirement',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'text-sm font-normal text-muted-foreground' },
  template: `{{ marker() }}`,
})
export class FieldRequirementComponent {
  readonly convention = input<FormRequirementConvention>('mostly-required');
  readonly required = input.required<boolean>();
  protected readonly marker = computed(() => {
    if (this.convention() === 'mostly-required') {
      return this.required() ? '' : $localize`:@@shared.forms.field.optional:(optional)`;
    }
    return this.required() ? $localize`:@@shared.forms.field.required:(required)` : '';
  });
}
