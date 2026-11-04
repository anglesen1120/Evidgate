import { ChangeDetectionStrategy, Component } from '@angular/core';
import { HlmIcon } from '@evidgate/ui/icon';
import { classes } from '@evidgate/ui/utils';

@Component({
  selector: 'hlm-dropdown-menu-item-sub-indicator',
  exportAs: 'hlmDropdownMenuItemSubIndicator',
  imports: [HlmIcon],

  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hlm-icon [name]="'lucideChevronRight'" class="text-[length:--spacing(4)] rtl:rotate-180" />
  `,
})
export class HlmDropdownMenuItemSubIndicator {
  constructor() {
    classes(() => 'ms-auto flex items-center justify-center');
  }
}
