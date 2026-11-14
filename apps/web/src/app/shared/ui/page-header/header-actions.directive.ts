import { Directive } from '@angular/core';
import { classes } from '@evidgate/ui/utils';

@Directive({
  selector: '[appPageHeaderActions], [appSectionHeaderActions]',
})
export class HeaderActionsDirective {
  constructor() {
    classes(() => 'eg-header-action');
  }
}
