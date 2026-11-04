import { CdkMenu } from '@angular/cdk/menu';
import { Directive, ElementRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import {
  deriveMenuSideFromTransformOrigin,
  MENU_SIDE,
  type MenuSide,
} from '@spartan-ng/brain/core';
import { classes } from '@evidgate/ui/utils';

@Directive({
  selector: '[hlmDropdownMenuSub],hlm-dropdown-menu-sub',
  exportAs: 'hlmDropdownMenuSub',
  hostDirectives: [CdkMenu],
  host: {
    'data-slot': 'dropdown-menu-sub',
    '[attr.data-state]': '_state()',
    '[attr.data-side]': '_side()',
  },
})
export class HlmDropdownMenuSub {
  private readonly _host = inject(CdkMenu);
  private readonly _elementRef = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly _menuSide = inject(MENU_SIDE, { optional: true });

  protected readonly _state = signal('open');
  protected readonly _side = signal<MenuSide>(this._menuSide?.side() ?? 'right');

  constructor() {
    this.setSideFromTransformOrigin();
    this._host.closed.pipe(takeUntilDestroyed()).subscribe(() => this._state.set('closed'));

    classes(() => 'spartan-dropdown-menu-sub-content w-auto');
  }

  private setSideFromTransformOrigin() {
    const side = this._menuSide?.side() ?? 'right';
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
