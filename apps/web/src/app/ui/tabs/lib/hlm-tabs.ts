import { Directive, input } from '@angular/core';
import { BrnTabs, type BrnTabsOrientation } from '@spartan-ng/brain/tabs';
import { classes } from '@evidgate/ui/utils';

@Directive({
  selector: '[hlmTabs],hlm-tabs',
  exportAs: 'hlmTabs',
  hostDirectives: [
    {
      directive: BrnTabs,
      inputs: ['activationMode', 'brnTabs: tab'],
      outputs: ['tabActivated'],
    },
  ],
  host: {
    'data-slot': 'tabs',
  },
})
export class HlmTabs {
  public readonly tab = input.required<string>();
  // Existing bindings remain valid, but Brain never receives a vertical orientation.
  public readonly orientation = input<'horizontal', BrnTabsOrientation>('horizontal', {
    transform: () => 'horizontal',
  });

  constructor() {
    classes(() => 'group/tabs flex min-w-0 flex-col gap-2');
  }
}
