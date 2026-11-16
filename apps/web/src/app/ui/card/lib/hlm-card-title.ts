import { Directive, input } from '@angular/core';
import { classes } from '@evidgate/ui/utils';

export type HlmCardTitleSize = 'default' | 'page' | 'section' | 'subsection';

const titleClasses: Record<HlmCardTitleSize, string> = {
  default: 'spartan-card-title',
  page: 'spartan-card-title eg-type-page-title',
  section: 'spartan-card-title eg-type-section-title',
  subsection: 'spartan-card-title eg-type-subsection-title',
};

@Directive({
  selector: '[hlmCardTitle]',
  exportAs: 'hlmCardTitle',
  host: {
    'data-slot': 'card-title',
    '[attr.data-title-size]': 'size()',
  },
})
export class HlmCardTitle {
  readonly size = input<HlmCardTitleSize>('default');

  constructor() {
    classes(() => titleClasses[this.size()]);
  }
}
