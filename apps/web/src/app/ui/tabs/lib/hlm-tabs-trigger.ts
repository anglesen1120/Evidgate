import { Directive, input } from '@angular/core';
import { BrnTabsTrigger } from '@spartan-ng/brain/tabs';
import { classes } from '@evidgate/ui/utils';

@Directive({
  selector: '[hlmTabsTrigger]',
  exportAs: 'hlmTabsTrigger',
  hostDirectives: [
    { directive: BrnTabsTrigger, inputs: ['brnTabsTrigger: hlmTabsTrigger', 'disabled'] },
  ],
  host: {
    'data-slot': 'tabs-trigger',
  },
})
export class HlmTabsTrigger {
  public readonly triggerFor = input.required<string>({ alias: 'hlmTabsTrigger' });
  constructor() {
    classes(
      () =>
        'spartan-tabs-trigger relative inline-flex flex-none! items-center justify-center whitespace-nowrap! [max-inline-size:none] disabled:pointer-events-none [&_hlm-icon]:pointer-events-none [&_hlm-icon]:shrink-0',
    );
  }
}
