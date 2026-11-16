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

import { escapeFormatChars } from '../values/ref-name-display';

/** Internal text renderer. Native popovers enter the top layer, outside scroll clipping. */
@Component({
  selector: 'app-data-table-value',
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
        class="full-value box-border m-auto max-w-[calc(100%-var(--eg-space-5)*2)] max-h-[calc(100%-var(--eg-space-5)*2)] overflow-auto rounded-sm border border-input bg-card p-[var(--eg-space-4)] font-sans text-sm text-foreground whitespace-normal [overflow-wrap:anywhere]"
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
  private readonly destroyRef = inject(DestroyRef);
  private hideTimer: number | undefined;
  private opened = false;
  private readonly dismissOnEscape = (event: KeyboardEvent) => {
    if (event.key === 'Escape') this.hide();
  };

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
    if (!this.clipped() || !element || this.opened) return;
    element.showPopover();
    this.opened = true;
    this.expanded.set(true);
    // Only an open popover installs a global listener, not every rendered cell.
    document.addEventListener('keydown', this.dismissOnEscape);
  }

  hide(): void {
    this.cancelHide();
    document.removeEventListener('keydown', this.dismissOnEscape);
    const element = this.popover()?.nativeElement;
    if (this.opened && element?.isConnected) element.hidePopover();
    this.opened = false;
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
