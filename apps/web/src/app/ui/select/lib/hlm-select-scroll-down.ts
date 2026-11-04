import { ChangeDetectionStrategy, Component } from '@angular/core';
import { HlmIcon } from '@evidgate/ui/icon';
import { BrnSelectScrollDown } from '@spartan-ng/brain/select';
import { classes } from '@evidgate/ui/utils';

@Component({
  selector: 'hlm-select-scroll-down',
  exportAs: 'hlmSelectScrollDown',
  imports: [HlmIcon],

  changeDetection: ChangeDetectionStrategy.OnPush,
  hostDirectives: [BrnSelectScrollDown],
  template: ` <hlm-icon [name]="'lucideChevronDown'" /> `,
})
export class HlmSelectScrollDown {
  constructor() {
    classes(() => 'spartan-select-scroll-down-button sticky bottom-0 w-full data-hidden:hidden');
  }
}
