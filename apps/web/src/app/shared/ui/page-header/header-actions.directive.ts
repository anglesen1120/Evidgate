import { Directive } from '@angular/core';
import { classes } from '@evidgate/ui/utils';

@Directive({
  selector: '[appPageHeaderActions], [appSectionHeaderActions]',
})
export class HeaderActionsDirective {
  constructor() {
    // Independent Helm directives can register their nowrap classes after this one.
    classes(() => 'eg-header-action box-border h-auto! whitespace-normal!');
  }
}
