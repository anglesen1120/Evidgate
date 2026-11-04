import { Directive } from '@angular/core';
import { classes } from '@evidgate/ui/utils';

@Directive({
  selector: '[hlmCardContent]',
  exportAs: 'hlmCardContent',
  host: { 'data-slot': 'card-content' },
})
export class HlmCardContent {
  constructor() {
    classes(() => 'spartan-card-content');
  }
}
