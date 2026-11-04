import type { BooleanInput } from '@angular/cdk/coercion';
import {
  booleanAttribute,
  ChangeDetectionStrategy,
  Component,
  computed,
  input,
} from '@angular/core';
import { BrnSelectContent, BrnSelectList } from '@spartan-ng/brain/select';
import { classes, hlm } from '@evidgate/ui/utils';
import { HlmSelectScrollDown } from './hlm-select-scroll-down';
import { HlmSelectScrollUp } from './hlm-select-scroll-up';

@Component({
  selector: 'hlm-select-content',
  exportAs: 'hlmSelectContent',
  imports: [HlmSelectScrollUp, HlmSelectScrollDown, BrnSelectList],
  changeDetection: ChangeDetectionStrategy.OnPush,
  hostDirectives: [BrnSelectContent],
  template: `
    @if (showScroll()) {
      <hlm-select-scroll-up />
    }

    <div
      brnSelectList
      [class]="_computedListboxClasses()"
      [attr.aria-label]="ariaLabel()"
      [attr.aria-labelledby]="ariaLabelledby()"
    >
      <ng-content />
    </div>

    @if (showScroll()) {
      <hlm-select-scroll-down />
    }
  `,
})
export class HlmSelectContent {
  protected readonly _computedListboxClasses = computed(() => hlm('flex flex-col'));

  public readonly showScroll = input<boolean, BooleanInput>(false, { transform: booleanAttribute });
  public readonly ariaLabel = input<string | undefined>(undefined, { alias: 'aria-label' });
  public readonly ariaLabelledby = input<string | undefined>(undefined, {
    alias: 'aria-labelledby',
  });

  constructor() {
    classes(
      () =>
        'spartan-select-content relative flex w-(--brn-select-width) overflow-x-hidden overflow-y-auto',
    );
  }
}
