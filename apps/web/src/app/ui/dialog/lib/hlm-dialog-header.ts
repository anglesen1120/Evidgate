import { Directive } from '@angular/core';
import { classes } from '@evidgate/ui/utils';

/** Optionally project a decorative [hlmDialogStatusIcon] beside the title and a
 * [hlmDialogSubject] beneath it. The caller supplies the equivalent status text.
 */
@Directive({
  selector: '[hlmDialogHeader],hlm-dialog-header',
  exportAs: 'hlmDialogHeader',
  host: { 'data-slot': 'dialog-header' },
})
export class HlmDialogHeader {
  constructor() {
    classes(() => 'spartan-dialog-header flex flex-col');
  }
}
