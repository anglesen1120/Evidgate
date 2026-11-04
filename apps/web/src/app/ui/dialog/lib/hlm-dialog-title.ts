import { Directive } from '@angular/core';
import { BrnDialogTitle } from '@spartan-ng/brain/dialog';
import { classes } from '@evidgate/ui/utils';

@Directive({
  selector: '[hlmDialogTitle]',
  exportAs: 'hlmDialogTitle',
  hostDirectives: [BrnDialogTitle],
  host: { 'data-slot': 'dialog-title' },
})
export class HlmDialogTitle {
  constructor() {
    classes(() => 'spartan-dialog-title');
  }
}
