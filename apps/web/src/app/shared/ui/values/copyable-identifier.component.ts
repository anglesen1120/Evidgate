import {
  afterRenderEffect,
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  ElementRef,
  effect,
  inject,
  input,
  signal,
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
import { HlmIcon } from '@evidgate/ui/icon';
import type { BrnDialogRef } from '@spartan-ng/brain/dialog';

import { CopyValueButtonComponent, type CopyValueButtonSize } from './copy-value-button.component';
import { middleTruncateParts } from './middle-truncate';
import { escapeFormatChars } from './ref-name-display';

@Component({
  selector: 'app-copyable-identifier',
  imports: [
    HlmButton,
    HlmDialogClose,
    HlmDialogFooter,
    HlmDialogTitle,
    HlmIcon,
    CopyValueButtonComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="identifier" [class.compact]="compact()">
      <code
        [class.preview]="showPreview()"
        [class.full-value]="!showPreview()"
        [attr.aria-hidden]="showPreview() ? 'true' : null"
      >
        @if (showPreview()) {
          <bdi
            #previewValue
            class="preview-value"
            [class.preview-elided]="previewParts().trailing !== ''"
          >
            <span class="preview-head" [textContent]="previewParts().leading"></span>
            @if (previewParts().trailing) {
              <span>…</span>
              <span class="preview-tail"
                ><span [textContent]="previewParts().trailing"></span
              ></span>
            }
          </bdi>
        } @else {
          <bdi [textContent]="displayValue()"></bdi>
        }
      </code>
      @if (showPreview()) {
        <span class="sr-only identifier-accessible">
          <bdi [textContent]="accessibleValue()"></bdi>
        </span>
      }
      <span class="actions">
        @if (showFullAction()) {
          <button
            hlmBtn
            type="button"
            [attr.aria-haspopup]="'dialog'"
            [variant]="'ghost'"
            [size]="'sm'"
            [attr.aria-label]="showLabel()"
            [attr.title]="compact() ? showLabel() : null"
            [class.icon-action]="compact()"
            (click)="showFull()"
          >
            @if (compact()) {
              <hlm-icon class="action-icon" [name]="'lucideMaximize2'" />
            } @else {
              {{ showText }}
            }
          </button>
        }
        <app-copy-value-button
          #copyAction
          [value]="value()"
          [label]="label()"
          [size]="copySize()"
          [iconOnly]="compact()"
        />
      </span>
    </div>
    <ng-template #fullValue>
      <h2 hlmDialogTitle class="full-value-heading">{{ label() }}</h2>
      <code class="full-value"><bdi [textContent]="displayValue()"></bdi></code>
      <hlm-dialog-footer>
        <button hlmBtn type="button" [variant]="'outline'" hlmDialogClose>{{ closeText }}</button>
        <app-copy-value-button [value]="value()" [label]="label()" [size]="copySize()" />
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
    .identifier.compact,
    .compact .actions {
      flex-wrap: nowrap;
    }
    .compact .actions {
      flex: none;
    }
    .preview-value {
      display: block;
      max-inline-size: 32ch;
      overflow: hidden;
    }
    .compact .preview-value {
      max-inline-size: 16ch;
    }
    .preview-value:not(.preview-elided) {
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .preview-elided {
      display: grid;
      grid-template-columns: minmax(0, 1fr) auto minmax(0, 1fr);
    }
    .preview-head,
    .preview-tail {
      min-inline-size: 0;
      overflow: hidden;
      white-space: nowrap;
    }
    /* End-align the suffix so clipping removes its inner edge, not its last glyph. */
    .preview-tail {
      display: flex;
      justify-content: flex-end;
    }
    .preview-tail > span {
      flex: none;
    }
    .icon-action {
      inline-size: var(--eg-control-sm);
      padding: var(--eg-space-2);
      white-space: nowrap;
    }
    .action-icon {
      inline-size: var(--eg-space-5);
      block-size: var(--eg-space-5);
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
      text-overflow: clip;
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
  private readonly dialogs = inject(HlmDialogService);

  readonly value = input.required<string>();
  readonly label = input.required<string>();
  readonly truncate = input(true);
  /** Compact scan rows recover the full value in the dialog instead of expanding. */
  readonly compact = input(false);
  protected readonly showPreview = computed(() => this.compact() || this.truncate());
  /** Applies the copy target size in both the scan row and its full-value dialog. */
  readonly copySize = input<CopyValueButtonSize>('sm');
  private readonly fullValue = viewChild.required<TemplateRef<unknown>>('fullValue');
  private readonly previewValue = viewChild<ElementRef<HTMLElement>>('previewValue');
  private readonly copyAction = viewChild.required<
    CopyValueButtonComponent,
    ElementRef<HTMLElement>
  >('copyAction', { read: ElementRef });
  private readonly measuredClipped = signal(false);
  private dialog: BrnDialogRef<unknown> | undefined;
  protected readonly showText = $localize`:@@shared.values.show-full:Show full`;
  protected readonly closeText = $localize`:@@shared.values.close:Close`;
  protected readonly displayValue = computed(() => escapeFormatChars(this.value()));
  protected readonly previewParts = computed(() =>
    middleTruncateParts(this.displayValue(), this.compact() ? 16 : 32),
  );
  protected readonly showFullAction = computed(
    () =>
      this.showPreview() &&
      (!this.compact() || this.previewParts().trailing !== '' || this.measuredClipped()),
  );
  protected readonly accessibleValue = computed(() => `${this.label()}: ${this.displayValue()}`);
  protected readonly showLabel = computed(
    () => $localize`:@@shared.values.show-full-label:Show full ${this.label()}:LABEL:`,
  );

  constructor() {
    afterRenderEffect((onCleanup) => {
      const element = this.previewValue()?.nativeElement;
      if (!this.compact() || !element || this.previewParts().trailing) {
        this.measuredClipped.set(false);
        return;
      }
      const measure = () =>
        this.measuredClipped.set(
          element.clientWidth > 0 && element.scrollWidth > element.clientWidth,
        );
      measure();
      if (typeof ResizeObserver === 'undefined') return;
      const observer = new ResizeObserver(measure);
      observer.observe(element);
      onCleanup(() => observer.disconnect());
    });
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
      // A fitting compact preview may lose its disclosure while the dialog is open.
      restoreFocus:
        this.compact() && !this.previewParts().trailing
          ? (this.copyAction().nativeElement.querySelector<HTMLButtonElement>('button') ?? true)
          : true,
    });
  }
}
