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

    <div brnSelectList [class]="_computedListboxClasses()" [attr.aria-label]="listLabel()">
      <ng-content />
    </div>

    @if (showScroll()) {
      <hlm-select-scroll-down />
    }
  `,
})
export class HlmSelectContent {
  protected readonly _computedListboxClasses = computed(() => hlm('flex flex-col'));

  /** Accessible name of the listbox; the popup content has no visible label of its own. */
  public readonly listLabel = input<string | null>(null);

  public readonly showScroll = input<boolean, BooleanInput>(false, { transform: booleanAttribute });

  constructor() {
    classes(
      () =>
        'spartan-select-content relative flex w-(--brn-select-width) overflow-x-hidden overflow-y-auto',
    );
  }
}
