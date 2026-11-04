import { Directive } from '@angular/core';
import { classes } from '@evidgate/ui/utils';

@Directive({
  selector: '[hlmSelectValuesContent],hlm-select-values-content',
  exportAs: 'hlmSelectValuesContent',
})
export class HlmSelectValuesContent {
  constructor() {
    classes(() => 'spartan-select-values-content flex');
  }
}
