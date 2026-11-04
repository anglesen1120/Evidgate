import { type NumberInput } from '@angular/cdk/coercion';
import { CdkMenu } from '@angular/cdk/menu';
import { Directive, ElementRef, inject, input, numberAttribute, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import {
  deriveMenuSideFromTransformOrigin,
  MENU_SIDE,
  type MenuSide,
} from '@spartan-ng/brain/core';
import { classes } from '@evidgate/ui/utils';

@Directive({
  selector: '[hlmDropdownMenu],hlm-dropdown-menu',
  exportAs: 'hlmDropdownMenu',
  hostDirectives: [CdkMenu],
  host: {
    'data-slot': 'dropdown-menu',
    '[attr.data-state]': '_state()',
    '[attr.data-side]': '_side()',
    '[style.--side-offset]': 'sideOffset()',
  },
})
export class HlmDropdownMenu {
  private readonly _host = inject(CdkMenu);
  private readonly _elementRef = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly _menuSide = inject(MENU_SIDE, { optional: true });

  protected readonly _state = signal('open');
  protected readonly _side = signal<MenuSide>(this._menuSide?.side() ?? 'bottom');

  public readonly sideOffset = input<number, NumberInput>(1, { transform: numberAttribute });

  constructor() {
    classes(
      () =>
        'spartan-dropdown-menu-content my-[--spacing(var(--side-offset))] overflow-x-hidden overflow-y-auto outline-none',
    );

    this.setSideFromTransformOrigin();
    this._host.closed.pipe(takeUntilDestroyed()).subscribe(() => this._state.set('closed'));
  }

  private setSideFromTransformOrigin() {
    const side = this._menuSide?.side() ?? 'bottom';
    setTimeout(() => {
      this._side.set(
        deriveMenuSideFromTransformOrigin(
          this._elementRef.nativeElement.style.transformOrigin,
          side,
        ),
      );
    });
  }
}
