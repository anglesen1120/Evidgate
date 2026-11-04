import { ChangeDetectionStrategy, Component } from '@angular/core';
import { HlmIcon } from '@evidgate/ui/icon';
import { classes } from '@evidgate/ui/utils';

@Component({
  selector: 'hlm-dropdown-menu-checkbox-indicator',
  exportAs: 'hlmDropdownMenuCheckboxIndicator',
  imports: [HlmIcon],

  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { 'data-slot': 'dropdown-menu-checkbox-item-indicator' },
  template: ` <hlm-icon [name]="'lucideCheck'" /> `,
})
export class HlmDropdownMenuCheckboxIndicator {
  constructor() {
    classes(
      () =>
        'spartan-dropdown-menu-item-indicator pointer-events-none opacity-0 group-data-checked/dropdown-menu-checkbox:opacity-100',
    );
  }
}
