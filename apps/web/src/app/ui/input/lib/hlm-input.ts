import { Directive } from '@angular/core';
import { BrnFieldControlDescribedBy } from '@spartan-ng/brain/field';
import { BrnInput } from '@spartan-ng/brain/input';
import { classes } from '@evidgate/ui/utils';

@Directive({
  selector: '[hlmInput]',
  exportAs: 'hlmInput',
  hostDirectives: [
    { directive: BrnInput, inputs: ['id', 'forceInvalid'] },
    { directive: BrnFieldControlDescribedBy, inputs: ['aria-describedby'] },
  ],
  host: { 'data-slot': 'input' },
})
export class HlmInput {
  constructor() {
    classes(
      () =>
        'spartan-input file:text-foreground placeholder:text-muted-foreground w-full min-w-0 file:inline-flex file:border-0 file:bg-transparent',
    );
  }
}
