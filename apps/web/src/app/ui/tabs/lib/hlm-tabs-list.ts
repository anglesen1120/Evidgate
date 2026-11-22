import { Directive, input } from '@angular/core';
import { BrnTabsList } from '@spartan-ng/brain/tabs';
import { classes } from '@evidgate/ui/utils';
import { type VariantProps, cva } from 'class-variance-authority';

export const listVariants = cva(
  'spartan-tabs-list group/tabs-list inline-flex w-fit items-center group-data-[orientation=vertical]/tabs:h-fit group-data-[orientation=vertical]/tabs:flex-col',
  {
    variants: {
      variant: {
        default: 'spartan-tabs-list-default',
        line: 'spartan-tabs-list-line',
      },
    },
    defaultVariants: {
      variant: 'default',
    },
  },
);
type ListVariants = VariantProps<typeof listVariants>;

@Directive({
  selector: '[hlmTabsList],hlm-tabs-list',
  exportAs: 'hlmTabsList',
  hostDirectives: [BrnTabsList],
  host: {
    'data-slot': 'tabs-list',
    '[attr.data-variant]': 'variant()',
  },
})
export class HlmTabsList {
  public readonly variant = input<ListVariants['variant']>('default');

  constructor() {
    classes(() => listVariants({ variant: this.variant() }));
  }
}
