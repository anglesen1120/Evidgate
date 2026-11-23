import {
  afterRenderEffect,
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  ElementRef,
  inject,
  input,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { openFullValuePopover } from '@evidgate/ui/utils';

import { escapeFormatChars } from '../values/ref-name-display';

/** Internal text renderer. Native popovers enter the top layer, outside scroll clipping. */
@Component({
  selector: 'app-data-table-value',
  styleUrl: '../../../ui/utils/lib/full-value-popover.css',
  host: {
    class:
      'eg-table-value block min-w-0 focus-within:outline-[length:var(--eg-focus-width)] focus-within:outline-solid focus-within:outline-ring focus-within:outline-offset-[var(--eg-focus-offset)]',
    '[class.eg-table-fixed-value]': 'fixedRow()',
  },
  template: `
    <span
      #valueElement
      class="value block [overflow-wrap:anywhere] focus-visible:outline-[length:var(--eg-focus-width)] focus-visible:outline-solid focus-visible:outline-ring focus-visible:outline-offset-[var(--eg-focus-offset)]"
      [class.truncate]="singleLine()"
      [class.whitespace-normal]="!singleLine() && !nowrap()"
      [class.whitespace-nowrap]="nowrap()"
      [class]="
        clipped()
          ? 'cursor-help min-h-[var(--eg-control-sm)] pointer-coarse:min-h-[var(--eg-control-touch)]'
          : ''
      "
      [class.clipped]="clipped()"
      [attr.tabindex]="clipped() ? 0 : null"
      [attr.role]="clipped() ? 'button' : null"
      [attr.aria-expanded]="clipped() ? expanded() : null"
      (focus)="show()"
      (blur)="hide()"
      (mouseenter)="show()"
      (mouseleave)="scheduleHide()"
      (click)="show()"
      (keydown.enter)="show()"
      (keydown.space)="openFromKeyboard($event)"
      (keydown.escape)="dismiss($event)"
      ><bdi>{{ displayValue() }}</bdi></span
    >
    @if (clipped()) {
      <!-- The value span already holds the whole text (clipping is visual only), so it is the
        one copy assistive technology reads. The popover is a sighted-user duplicate: hidden
        from the accessibility tree so an open popover never announces the value again. -->
      <span
        #popover
        class="full-value box-border overflow-auto rounded-(--eg-radius-lg) border border-(--eg-border-control) bg-(--eg-surface) p-[var(--eg-space-4)] font-sans text-sm text-(--eg-text) whitespace-normal [overflow-wrap:anywhere]"
        popover="manual"
        aria-hidden="true"
        (mouseenter)="cancelHide()"
        (mouseleave)="scheduleHide()"
        (keydown.escape)="dismiss($event)"
        ><bdi>{{ displayValue() }}</bdi></span
      >
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DataTableValueComponent {
  private readonly destroyRef = inject(DestroyRef);

  readonly value = input.required<string | number>();
  readonly truncate = input(false);
  readonly fixedRow = input(false);
  readonly nowrap = input(false);
  protected readonly displayValue = computed(() => {
    const value = this.value();
    return typeof value === 'string' ? escapeFormatChars(value) : value;
  });
  protected readonly singleLine = computed(() => this.truncate() || this.fixedRow());
  protected readonly expanded = signal(false);
  private readonly measuredClipped = signal(false);
  readonly clipped = computed(() => this.singleLine() && this.measuredClipped());
  private readonly valueElement = viewChild.required<ElementRef<HTMLElement>>('valueElement');
  private readonly popover = viewChild<ElementRef<HTMLElement>>('popover');
  private hideTimer: number | undefined;
  private closePanel: (() => void) | undefined;

  constructor() {
    afterRenderEffect((onCleanup) => {
      // Track value changes too: CDK recycles the same cell for another row.
      this.value();
      untracked(() => this.hide());
      if (!this.singleLine()) {
        this.measuredClipped.set(false);
        this.hide();
        return;
      }
      const element = this.valueElement().nativeElement;
      const measure = () => {
        const clipped =
          (element.clientWidth > 0 && element.scrollWidth > element.clientWidth) ||
          (element.clientHeight > 0 && element.scrollHeight > element.clientHeight);
        this.measuredClipped.set(clipped);
        if (!clipped) this.hide();
      };
      measure();
      if (typeof ResizeObserver === 'undefined') return;
      const observer = new ResizeObserver(measure);
      observer.observe(element);
      onCleanup(() => observer.disconnect());
    });
    this.destroyRef.onDestroy(() => this.hide());
  }

  show(): void {
    this.cancelHide();
    const element = this.popover()?.nativeElement;
    if (!this.clipped() || !element || this.closePanel) return;
    this.closePanel = openFullValuePopover(this.valueElement().nativeElement, element, () =>
      this.hide(),
    );
    this.expanded.set(true);
  }

  hide(): void {
    this.cancelHide();
    this.closePanel?.();
    this.closePanel = undefined;
    this.expanded.set(false);
  }

  dismiss(event: Event): void {
    event.stopPropagation();
    this.hide();
  }

  openFromKeyboard(event: Event): void {
    event.preventDefault();
    this.show();
  }

  scheduleHide(): void {
    this.cancelHide();
    // Allow the pointer to cross into the hoverable top-layer full-value panel.
    this.hideTimer = window.setTimeout(() => {
      if (document.activeElement !== this.valueElement().nativeElement) this.hide();
    }, 200);
  }

  cancelHide(): void {
    clearTimeout(this.hideTimer);
    this.hideTimer = undefined;
  }
}
