import { ChangeDetectionStrategy, Component, ElementRef, inject, input } from '@angular/core';
import { buttonVariants } from '@evidgate/ui/button';
import { HlmIcon, type HlmIconName } from '@evidgate/ui/icon';
import { HLM_TOOLTIP_PROVIDERS } from '@evidgate/ui/tooltip';
import { BrnButton } from '@spartan-ng/brain/button';
import { BrnTooltip } from '@spartan-ng/brain/tooltip';

@Component({
  selector: 'button[appRowAction], a[appRowAction]',
  imports: [HlmIcon],
  hostDirectives: [
    { directive: BrnButton, inputs: ['disabled'] },
    { directive: BrnTooltip, inputs: ['brnTooltip: label'] },
  ],
  providers: HLM_TOOLTIP_PROVIDERS,
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    '[class]': 'classes',
    '[attr.type]': 'isButton ? "button" : null',
    '[attr.aria-label]': 'tooltip.brnTooltip()',
    '[class.row-action-danger]': 'tone() === "danger"',
  },
  template: `<hlm-icon class="size-(--eg-space-5)" [name]="icon()" />`,
  styles: `
    :host(.row-action-danger) {
      color: var(--eg-danger-fg);
    }
    :host(.row-action-danger:hover) {
      background: var(--eg-danger-bg);
    }
  `,
})
export class RowActionComponent {
  private readonly element = inject<ElementRef<HTMLElement>>(ElementRef);
  protected readonly tooltip = inject(BrnTooltip);
  readonly nativeElement = this.element.nativeElement;
  protected readonly classes = buttonVariants({ variant: 'ghost', size: 'icon-sm' });
  protected readonly isButton = this.element.nativeElement.tagName === 'BUTTON';
  readonly icon = input<HlmIconName>('lucideEye');
  readonly tone = input<'neutral' | 'danger'>('neutral');
}
