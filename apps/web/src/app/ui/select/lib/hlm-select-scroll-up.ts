import { ChangeDetectionStrategy, Component } from '@angular/core';
import { HlmIcon } from '@evidgate/ui/icon';
import { BrnSelectScrollUp } from '@spartan-ng/brain/select';
import { classes } from '@evidgate/ui/utils';

@Component({
  selector: 'hlm-select-scroll-up',
  exportAs: 'hlmSelectScrollUp',
  imports: [HlmIcon],

  changeDetection: ChangeDetectionStrategy.OnPush,
  hostDirectives: [BrnSelectScrollUp],
  template: ` <hlm-icon [name]="'lucideChevronUp'" /> `,
})
export class HlmSelectScrollUp {
  constructor() {
    classes(() => 'spartan-select-scroll-up-button sticky top-0 w-full data-hidden:hidden');
  }
}
