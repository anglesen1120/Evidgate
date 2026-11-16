import { Directive } from '@angular/core';
import { classes } from '@evidgate/ui/utils';

@Directive({
  selector: '[hlmDialogFooter],hlm-dialog-footer',
  exportAs: 'hlmDialogFooter',
  host: { 'data-slot': 'dialog-footer' },
})
export class HlmDialogFooter {
  constructor() {
    // Stacked actions follow the same order as keyboard focus.
    classes(() => 'spartan-dialog-footer flex flex-col gap-2 sm:flex-row sm:justify-end');
  }
}
