import { Directive } from '@angular/core';
import { classes } from '@evidgate/ui/utils';

@Directive({
  selector: '[hlmAlertAction]',
  exportAs: 'hlmAlertAction',
  host: {
    'data-slot': 'alert-action',
  },
})
export class HlmAlertAction {
  constructor() {
    classes(() => 'spartan-alert-action');
  }
}
