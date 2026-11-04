import { Directive } from '@angular/core';
import { BrnSelectPlaceholder } from '@spartan-ng/brain/select';
import { classes } from '@evidgate/ui/utils';

@Directive({
  selector: '[hlmSelectPlaceholder],hlm-select-placeholder',
  exportAs: 'hlmSelectPlaceholder',
  hostDirectives: [BrnSelectPlaceholder],
  host: { 'data-slot': 'select-placeholder' },
})
export class HlmSelectPlaceholder {
  constructor() {
    classes(
      () =>
        'spartan-select-placeholder flex items-center data-hidden:hidden [&_hlm-icon]:pointer-events-none [&_hlm-icon]:shrink-0',
    );
  }
}
