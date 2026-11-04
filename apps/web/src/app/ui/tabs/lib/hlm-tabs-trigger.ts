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
    classes(() => [
      `spartan-tabs-trigger focus-visible:border-ring focus-visible:ring-ring focus-visible:outline-ring text-muted-foreground hover:text-foreground relative inline-flex h-[calc(100%-var(--eg-border-width))] flex-1 items-center justify-center whitespace-nowrap transition-all group-data-[orientation=vertical]/tabs:w-full group-data-[orientation=vertical]/tabs:justify-start focus-visible:ring-2 focus-visible:outline-1 disabled:pointer-events-none disabled:opacity-50 [&_hlm-icon]:pointer-events-none [&_hlm-icon]:shrink-0`,
      'group-data-[variant=line]/tabs-list:bg-transparent group-data-[variant=line]/tabs-list:data-active:bg-transparent dark:group-data-[variant=line]/tabs-list:data-active:border-transparent dark:group-data-[variant=line]/tabs-list:data-active:bg-transparent',
      'data-active:bg-background dark:data-active:text-foreground dark:data-active:border-input dark:data-active:bg-input/30 data-active:text-foreground',
      'after:bg-foreground after:absolute after:opacity-0 after:transition-opacity group-data-[orientation=horizontal]/tabs:after:inset-x-0 group-data-[orientation=horizontal]/tabs:after:-bottom-1 group-data-[orientation=horizontal]/tabs:after:h-0.5 group-data-[orientation=vertical]/tabs:after:inset-y-0 group-data-[orientation=vertical]/tabs:after:-right-1 group-data-[orientation=vertical]/tabs:after:w-0.5 group-data-[variant=line]/tabs-list:data-active:after:opacity-100',
    ]);
  }
}
