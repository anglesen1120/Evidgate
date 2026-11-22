import { afterRenderEffect, Directive, ElementRef, inject, input } from '@angular/core';
import { BrnTabsList } from '@spartan-ng/brain/tabs';
import { classes } from '@evidgate/ui/utils';
import { type VariantProps, cva } from 'class-variance-authority';

export const listVariants = cva(
  'spartan-tabs-list group/tabs-list inline-flex w-fit min-w-0 flex-nowrap! items-center',
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

// Restrict scrolling to the strip, including when an offscreen panel is selected by its parent.
const revealOptions = { block: 'nearest', inline: 'nearest', container: 'nearest' } as const;

function supportsLocalReveal(doc: Document): boolean {
  let supported = false;
  const options = {
    block: 'nearest',
    get container() {
      supported = true;
      return 'nearest';
    },
  } as const;
  doc.createElement('span').scrollIntoView?.(options);
  return supported;
}

@Directive({
  selector: '[hlmTabsList],hlm-tabs-list',
  exportAs: 'hlmTabsList',
  hostDirectives: [BrnTabsList],
  host: {
    'data-slot': 'tabs-list',
    '[attr.data-variant]': 'variant()',
    '(focusin)': 'reveal($event.target)',
  },
})
export class HlmTabsList {
  private readonly list = inject<ElementRef<HTMLElement>>(ElementRef).nativeElement;
  private readonly brain = inject(BrnTabsList);
  private localReveal?: boolean;

  public readonly variant = input<ListVariants['variant']>('default');

  constructor() {
    classes(() => [
      listVariants({ variant: this.variant() }),
      'max-w-full overflow-x-auto overscroll-x-contain [scrollbar-width:thin] [scrollbar-color:var(--eg-border-control)_transparent] [scroll-padding-inline:var(--eg-space-2)] p-(--eg-space-2)!',
    ]);
    afterRenderEffect(() => {
      const selected = this.brain.triggers().find((trigger) => trigger.selected());
      if (selected) this.reveal((selected.elementRef as ElementRef<HTMLElement>).nativeElement);
    });
  }

  protected reveal(target: EventTarget | null): void {
    if (!(target instanceof HTMLElement) || this.list.scrollWidth <= this.list.clientWidth) return;
    this.localReveal ??= supportsLocalReveal(this.list.ownerDocument);
    if (this.localReveal) {
      target.scrollIntoView(revealOptions);
      return;
    }

    // Older engines ignore "container" and would also scroll an offscreen tab strip onto the page.
    const strip = this.list.getBoundingClientRect();
    const tab = target.getBoundingClientRect();
    const inset = parseFloat(getComputedStyle(this.list).scrollPaddingInlineStart) || 0;
    const before = tab.left - (strip.left + inset);
    const after = tab.right - (strip.right - inset);
    if (before < 0 && after <= 0) this.list.scrollLeft += before;
    else if (after > 0 && before >= 0) this.list.scrollLeft += after;
  }
}
