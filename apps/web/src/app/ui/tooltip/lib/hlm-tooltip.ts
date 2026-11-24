import { Directive } from '@angular/core';
import {
  BrnTooltip,
  type BrnTooltipPosition,
  provideBrnTooltipDefaultOptions,
} from '@spartan-ng/brain/tooltip';
import { hlm } from '@evidgate/ui/utils';
import { cva } from 'class-variance-authority';

export const DEFAULT_TOOLTIP_SVG_CLASS =
  'bg-[var(--eg-text)] fill-[var(--eg-text)] block size-2.5 translate-y-[calc(-50%-var(--eg-space-1))] rotate-45 rounded-[var(--eg-radius-sm)]';

export const DEFAULT_TOOLTIP_CONTENT_CLASSES = 'spartan-tooltip-content w-fit';

export const tooltipPositionVariants = cva('absolute', {
  variants: {
    position: {
      top: 'bottom-0 left-[calc(50%-var(--eg-space-2))] translate-y-full',
      bottom: '-top-2.5 left-[calc(50%-var(--eg-space-2))] translate-y-0 rotate-180',
      left: '-end-2.5 top-[calc(50%-var(--eg-space-2))] translate-y-0 rotate-270 rtl:-rotate-270',
      right: '-start-2.5 top-[calc(50%-var(--eg-space-2))] translate-y-0 rotate-90 rtl:-rotate-90',
    },
  },
});

const HLM_TOOLTIP_PROVIDERS = [
  provideBrnTooltipDefaultOptions({
    svgClasses: DEFAULT_TOOLTIP_SVG_CLASS,
    tooltipContentClasses: DEFAULT_TOOLTIP_CONTENT_CLASSES,
    arrowClasses: (position: BrnTooltipPosition) => hlm(tooltipPositionVariants({ position })),
  }),
];

@Directive({
  selector: '[hlmTooltip]',
  exportAs: 'hlmTooltip',
  providers: HLM_TOOLTIP_PROVIDERS,
  hostDirectives: [
    {
      directive: BrnTooltip,
      inputs: [
        'brnTooltip: hlmTooltip',
        'position',
        'hideDelay',
        'showDelay',
        'sideOffset',
        'tooltipDisabled',
      ],
    },
  ],
})
export class HlmTooltip {}
