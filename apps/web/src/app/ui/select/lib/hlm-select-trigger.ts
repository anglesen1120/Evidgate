import type { BooleanInput } from '@angular/cdk/coercion';
import {
  booleanAttribute,
  ChangeDetectionStrategy,
  Component,
  computed,
  input,
} from '@angular/core';
import { HlmIcon } from '@evidgate/ui/icon';
import { BrnFieldControlDescribedBy } from '@spartan-ng/brain/field';
import { BrnSelectTrigger } from '@spartan-ng/brain/select';
import { hlm } from '@evidgate/ui/utils';
import type { ClassValue } from 'clsx';

@Component({
  selector: 'hlm-select-trigger',
  exportAs: 'hlmSelectTrigger',
  imports: [HlmIcon, BrnSelectTrigger, BrnFieldControlDescribedBy],

  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <button
      brnSelectTrigger
      brnFieldControlDescribedBy
      [forceInvalid]="forceInvalid()"
      [id]="buttonId()"
      [class]="_computedClass()"
      [aria-invalid]="ariaInvalidInput()"
      [aria-describedby]="ariaDescribedBy()"
      [attr.data-size]="size()"
      [attr.data-slot]="'select-trigger'"
    >
      <ng-content />
      <hlm-icon [name]="'lucideChevronDown'" class="spartan-select-trigger-icon ms-auto" />
    </button>
  `,
})
export class HlmSelectTrigger {
  private static _id = 0;

  public readonly userClass = input<ClassValue>('', { alias: 'class' });
  protected readonly _computedClass = computed(() =>
    hlm(
      'spartan-select-trigger flex w-fit items-center justify-between whitespace-nowrap outline-none disabled:cursor-not-allowed disabled:opacity-50 *:data-[slot=select-value]:line-clamp-1 *:data-[slot=select-value]:flex *:data-[slot=select-value]:items-center [&_hlm-icon]:pointer-events-none [&_hlm-icon]:shrink-0',
      this.userClass(),
    ),
  );

  public readonly buttonId = input<string>(`hlm-select-trigger-${HlmSelectTrigger._id++}`);
  public readonly ariaDescribedBy = input<string | null>(null, { alias: 'aria-describedby' });

  public readonly size = input<'default' | 'sm'>('default');

  public readonly forceInvalid = input<boolean, BooleanInput>(false, {
    transform: booleanAttribute,
  });

  public readonly ariaInvalidInput = input<boolean | undefined, BooleanInput>(undefined, {
    transform: (v: BooleanInput) => (v === '' || v === undefined ? undefined : booleanAttribute(v)),
    alias: 'aria-invalid',
  });
}
