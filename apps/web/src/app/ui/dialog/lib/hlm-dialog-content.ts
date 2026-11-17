import type { BooleanInput } from '@angular/cdk/coercion';
import type { ComponentType } from '@angular/cdk/portal';
import { NgComponentOutlet, NgTemplateOutlet } from '@angular/common';
import {
  booleanAttribute,
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  Injector,
  type TemplateRef,
} from '@angular/core';
import { HlmIcon } from '@evidgate/ui/icon';
import { BrnDialogRef, injectBrnDialogContext } from '@spartan-ng/brain/dialog';
import { HlmButton } from '@evidgate/ui/button';

import { classes } from '@evidgate/ui/utils';
import { HlmDialogClose } from './hlm-dialog-close';

type HlmDialogContentContext = {
  $component?: ComponentType<unknown>;
  $template?: TemplateRef<unknown>;
  $dynamicComponentClass?: string;
  $showCloseButton?: boolean;
  $closeLabel?: string;
};

@Component({
  selector: 'hlm-dialog-content',
  exportAs: 'hlmDialogContent',
  imports: [NgComponentOutlet, NgTemplateOutlet, HlmButton, HlmDialogClose, HlmIcon],

  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    'data-slot': 'dialog-content',
    '[attr.data-state]': 'state()',
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

  public readonly templateInjector = inject(Injector);

  private readonly _dialogContext = injectBrnDialogContext<HlmDialogContentContext | null>({
    optional: true,
  });

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
      this._dynamicComponentClass,
    ]);
  }
}
