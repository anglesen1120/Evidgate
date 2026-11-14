import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  effect,
  inject,
  input,
  TemplateRef,
  viewChild,
} from '@angular/core';
import { HlmButton } from '@evidgate/ui/button';
import {
  HlmDialogClose,
  HlmDialogFooter,
  HlmDialogService,
  HlmDialogTitle,
} from '@evidgate/ui/dialog';
import type { BrnDialogRef } from '@spartan-ng/brain/dialog';

import { CopyValueButtonComponent } from './copy-value-button.component';
import { middleTruncate } from './middle-truncate';

@Component({
  selector: 'app-copyable-identifier',
  imports: [HlmButton, HlmDialogClose, HlmDialogFooter, HlmDialogTitle, CopyValueButtonComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="identifier">
      <code
        [class.preview]="truncate()"
        [class.full-value]="!truncate()"
        [attr.aria-hidden]="truncate() ? 'true' : null"
        >{{ preview() }}</code
      >
      @if (truncate()) {
        <span class="sr-only identifier-accessible">{{ accessibleValue() }}</span>
      }
      <span class="actions">
        @if (truncate()) {
          <button
            hlmBtn
            type="button"
            [attr.aria-haspopup]="'dialog'"
            [variant]="'ghost'"
            [size]="'sm'"
            [attr.aria-label]="showLabel()"
            (click)="showFull()"
          >
            {{ showText }}
          </button>
        }
        <app-copy-value-button [value]="value()" [label]="label()" />
      </span>
    </div>
    <ng-template #fullValue>
      <h2 hlmDialogTitle class="full-value-heading">{{ label() }}</h2>
      <code class="full-value">{{ value() }}</code>
      <hlm-dialog-footer>
        <button hlmBtn type="button" [variant]="'outline'" hlmDialogClose>{{ closeText }}</button>
        <app-copy-value-button [value]="value()" [label]="label()" />
      </hlm-dialog-footer>
    </ng-template>
  `,
  styles: `
    :host {
      display: block;
      min-inline-size: 0;
      max-inline-size: 100%;
    }
    .identifier,
    .actions {
      display: flex;
      align-items: center;
      gap: var(--eg-space-2);
      min-inline-size: 0;
      max-inline-size: 100%;
    }
    .identifier {
      flex-wrap: wrap;
    }
    .actions {
      flex: 0 1 auto;
      flex-wrap: wrap;
    }
    code {
      font-family: var(--eg-font-mono);
      color: inherit;
      user-select: text;
    }
    .preview {
      flex: 1 1 0;
      min-inline-size: 0;
      overflow: hidden;
      white-space: nowrap;
      text-overflow: ellipsis;
    }
    .full-value {
      display: block;
      min-inline-size: 0;
      max-inline-size: 100%;
      white-space: pre-wrap;
      overflow-wrap: anywhere;
    }
    .identifier > .full-value {
      flex: 1 1 100%;
    }
    .full-value-heading {
      font-size: var(--eg-text-lg);
      line-height: var(--eg-leading-lg);
    }
    button {
      block-size: auto;
      min-block-size: var(--eg-control-sm);
      max-inline-size: 100%;
      white-space: normal;
    }
    @media (pointer: coarse) {
      .identifier,
      .actions {
        gap: var(--eg-space-3);
      }
      button {
        min-block-size: var(--eg-control-touch);
        min-inline-size: var(--eg-control-touch);
      }
    }
  `,
})
export class CopyableIdentifierComponent {
  readonly value = input.required<string>();
  readonly label = input.required<string>();
  readonly truncate = input(true);
  private readonly fullValue = viewChild.required<TemplateRef<unknown>>('fullValue');
  private readonly dialogs = inject(HlmDialogService);
  private dialog: BrnDialogRef<unknown> | undefined;
  protected readonly showText = $localize`:@@shared.values.show-full:Show full`;
  protected readonly closeText = $localize`:@@shared.values.close:Close`;
  protected readonly preview = computed(() =>
    this.truncate() ? middleTruncate(this.value()) : this.value(),
  );
  protected readonly accessibleValue = computed(() => `${this.label()}: ${this.value()}`);
  protected readonly showLabel = computed(
    () => $localize`:@@shared.values.show-full-label:Show full ${this.label()}:LABEL:`,
  );

  constructor() {
    effect(() => {
      this.value();
      this.label();
      this.dialog?.forceClose();
      this.dialog = undefined;
    });
    // Owner teardown cannot wait for another render or retain a recycled row's provenance.
    inject(DestroyRef).onDestroy(() => this.dialog?.forceClose());
  }

  protected showFull(): void {
    this.dialog?.forceClose();
    this.dialog = this.dialogs.open(this.fullValue(), {
      ariaLabel: this.label(),
      showCloseButton: false,
    });
  }
}
