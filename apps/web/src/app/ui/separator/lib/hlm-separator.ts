import { Directive } from '@angular/core';
import { BrnSeparator } from '@spartan-ng/brain/separator';
import { classes } from '@evidgate/ui/utils';

export const hlmSeparatorClass =
  'inline-flex shrink-0 bg-[var(--eg-border)] data-horizontal:h-[var(--eg-border-width)] data-horizontal:w-full data-vertical:w-[var(--eg-border-width)] data-vertical:self-stretch';

@Directive({
  selector: '[hlmSeparator],hlm-separator',
  exportAs: 'hlmSeparator',
  hostDirectives: [{ directive: BrnSeparator, inputs: ['orientation', 'decorative'] }],
  host: {
    'data-slot': 'separator',
  },
})
export class HlmSeparator {
  constructor() {
    classes(() => hlmSeparatorClass);
  }
}
