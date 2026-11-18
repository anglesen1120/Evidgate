import { afterRenderEffect, Directive, ElementRef, inject, input, signal } from '@angular/core';
import { BrnButton } from '@spartan-ng/brain/button';
import { classes } from '@evidgate/ui/utils';
import { cva, type VariantProps } from 'class-variance-authority';
import type { ClassValue } from 'clsx';
import { injectBrnButtonConfig } from './hlm-button.token';

export const buttonVariants = cva(
  'spartan-button group/button inline-flex shrink-0 items-center justify-center whitespace-nowrap transition-all outline-none select-none data-disabled:pointer-events-none data-disabled:opacity-50 [&_hlm-icon]:pointer-events-none [&_hlm-icon]:shrink-0',
  {
    variants: {
      variant: {
        default: 'spartan-button-variant-default',
        outline: 'spartan-button-variant-outline',
        secondary: 'spartan-button-variant-secondary',
        ghost: 'spartan-button-variant-ghost',
        destructive: 'spartan-button-variant-destructive',
        link: 'spartan-button-variant-link',
      },
      size: {
        default: 'spartan-button-size-default',
        xs: 'spartan-button-size-xs',
        sm: 'spartan-button-size-sm',
        lg: 'spartan-button-size-lg',
        icon: 'spartan-button-size-icon',
        'icon-xs': 'spartan-button-size-icon-xs',
        'icon-sm': 'spartan-button-size-icon-sm',
        'icon-lg': 'spartan-button-size-icon-lg',
      },
    },
    defaultVariants: {
      variant: 'default',
      size: 'default',
    },
  },
);

export type ButtonVariants = VariantProps<typeof buttonVariants>;

@Directive({
  selector: 'button[hlmBtn], a[hlmBtn]',
  exportAs: 'hlmBtn',
  hostDirectives: [{ directive: BrnButton, inputs: ['disabled'] }],
  host: { 'data-slot': 'button' },
})
export class HlmButton {
  private readonly _button = inject(BrnButton);
  private readonly _element = inject<ElementRef<HTMLButtonElement | HTMLAnchorElement>>(ElementRef);
  private readonly _config = injectBrnButtonConfig();

  private readonly _additionalClasses = signal<ClassValue>('');

  public readonly variant = input<ButtonVariants['variant']>(this._config.variant);

  public readonly size = input<ButtonVariants['size']>(this._config.size);

  public readonly tabIndex = input<number | null>(null);

  constructor() {
    classes(() => [
      buttonVariants({ variant: this.variant(), size: this.size() }),
      this._additionalClasses(),
    ]);
    // Brain also owns tabindex; an unchanged outer host binding can lose to its updates.
    // Restore its value on cleanup, leaving buttons without an override untouched.
    afterRenderEffect({
      write: (onCleanup) => {
        const index = this.tabIndex();
        if (index === null) return;
        const disabled = this._button.disabled();
        const element = this._element.nativeElement;
        element.tabIndex = disabled ? -1 : index;
        onCleanup(() => {
          if (this._button.disabled()) element.tabIndex = -1;
          else element.removeAttribute('tabindex');
        });
      },
    });
  }

  setClass(classes: string): void {
    this._additionalClasses.set(classes);
  }
}
