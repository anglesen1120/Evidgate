import type { BooleanInput } from '@angular/cdk/coercion';
import { DialogRef } from '@angular/cdk/dialog';
import type { ComponentType } from '@angular/cdk/portal';
import { NgComponentOutlet, NgTemplateOutlet } from '@angular/common';
import {
  booleanAttribute,
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  effect,
  inject,
  input,
  Injector,
  type TemplateRef,
  ViewEncapsulation,
} from '@angular/core';
import { HlmIcon } from '@evidgate/ui/icon';
import { BrnDialogRef, injectBrnDialogContext } from '@spartan-ng/brain/dialog';
import { HlmButton } from '@evidgate/ui/button';

import { classes } from '@evidgate/ui/utils';
import { guardDialogClose } from './guard-dialog-close';
import { HlmDialogClose } from './hlm-dialog-close';

export type HlmDialogSize = 'md' | 'lg' | 'xl';

type HlmDialogContentContext = {
  $component?: ComponentType<unknown>;
  $template?: TemplateRef<unknown>;
  $dynamicComponentClass?: string;
  $showCloseButton?: boolean;
  $closeLabel?: string;
  $size?: HlmDialogSize;
  $closeGuard?: () => boolean;
};

@Component({
  selector: 'hlm-dialog-content',
  exportAs: 'hlmDialogContent',
  imports: [NgComponentOutlet, NgTemplateOutlet, HlmButton, HlmDialogClose, HlmIcon],

  // Size-specific chrome styles reach both projected children and service outlets.
  encapsulation: ViewEncapsulation.None,
  styleUrl: './hlm-dialog-content.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    'data-slot': 'dialog-content',
    '[attr.data-state]': 'state()',
    '[attr.data-size]': 'size()',
  },
  template: `
    @if (component) {
      <ng-container [ngComponentOutlet]="component" />
    } @else if (template) {
      <ng-container
        [ngTemplateOutlet]="template"
        [ngTemplateOutletContext]="templateContext"
        [ngTemplateOutletInjector]="templateInjector"
      />
    } @else {
      <ng-content />
    }

    @if (showCloseButton()) {
      <button
        hlmBtn
        [variant]="'ghost'"
        [size]="'icon-sm'"
        class="spartan-dialog-close"
        hlmDialogClose
      >
        <span class="sr-only">{{ closeLabel() }}</span>
        <hlm-icon [name]="'lucideX'" />
      </button>
    }
  `,
})
export class HlmDialogContent {
  private readonly _dialogRef = inject(BrnDialogRef);
  private readonly _overlay = inject(DialogRef).overlayRef;
  private readonly _destroyRef = inject(DestroyRef);

  public readonly templateInjector = inject(Injector);

  private readonly _dialogContext = injectBrnDialogContext<HlmDialogContentContext | null>({
    optional: true,
  });

  /** Omit size to retain the original desktop width; an explicit size opts into record chrome. */
  public readonly size = input<HlmDialogSize | undefined>(this._dialogContext?.$size);
  /** All voluntary closes ask the same predicate. Release it after save/discard. */
  public readonly closeGuard = input<() => boolean>();

  public readonly showCloseButton = input<boolean, BooleanInput>(
    this._dialogContext?.$showCloseButton ?? true,
    {
      transform: booleanAttribute,
    },
  );
  public readonly closeLabel = input<string>(
    this._dialogContext?.$closeLabel ?? $localize`:@@ui.close:Close`,
  );

  public readonly state = computed(() => this._dialogRef?.state() ?? 'closed');

  public readonly component = this._dialogContext?.$component;
  public readonly template = this._dialogContext?.$template;
  public readonly templateContext = { $implicit: this._dialogContext };
  private readonly _dynamicComponentClass = this._dialogContext?.$dynamicComponentClass;

  constructor() {
    classes(() => [
      'spartan-dialog-content relative mx-auto w-[calc(100vw-var(--eg-space-8))] outline-none sm:mx-0',
      this.size() && `spartan-dialog-${this.size()}`,
      this._dynamicComponentClass,
    ]);
    const serviceGuard = this._dialogContext?.$closeGuard;
    if (serviceGuard) {
      // The service returns a live closeable ref before the first render.
      this._destroyRef.onDestroy(guardDialogClose(this._dialogRef, this._overlay, serviceGuard));
    } else {
      effect((onCleanup) => {
        const allow = this.closeGuard();
        if (allow) onCleanup(guardDialogClose(this._dialogRef, this._overlay, allow));
      });
    }
  }
}
