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
  viewChild,
} from '@angular/core';

let nextValueId = 0;

/** Internal text renderer. Native popovers enter the top layer, outside scroll clipping. */
@Component({
  selector: 'app-data-table-value',
  template: `
    <span
      #valueElement
      class="value"
      [class.truncate]="truncate()"
      [class.clipped]="clipped()"
      [attr.tabindex]="clipped() ? 0 : null"
      [attr.aria-describedby]="clipped() ? descriptionId : null"
      (focus)="show()"
      (blur)="hide()"
      (mouseenter)="show()"
      (mouseleave)="scheduleHide()"
      (click)="show()"
      (keydown.escape)="dismiss($event)"
      >{{ value() }}</span
    >
    @if (clipped()) {
      <span class="sr-only" [id]="descriptionId">{{ value() }}</span>
      <span
        #popover
        class="full-value"
        popover="manual"
        role="tooltip"
        (mouseenter)="cancelHide()"
        (mouseleave)="scheduleHide()"
        (keydown.escape)="dismiss($event)"
        >{{ value() }}</span
      >
    }
  `,
  styleUrl: './data-table-value.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DataTableValueComponent {
  readonly value = input.required<string | number>();
  readonly truncate = input(false);
  readonly descriptionId = `eg-table-value-${nextValueId++}`;
  private readonly measuredClipped = signal(false);
  readonly clipped = computed(() => this.truncate() && this.measuredClipped());
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
      if (!this.truncate()) {
        this.measuredClipped.set(false);
        this.hide();
        return;
      }
      const element = this.valueElement().nativeElement;
      const measure = () => {
        const clipped = element.clientWidth > 0 && element.scrollWidth > element.clientWidth;
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
    // Only an open popover installs a global listener, not every rendered cell.
    document.addEventListener('keydown', this.dismissOnEscape);
  }

  hide(): void {
    this.cancelHide();
    document.removeEventListener('keydown', this.dismissOnEscape);
    const element = this.popover()?.nativeElement;
    if (this.opened && element?.isConnected) element.hidePopover();
    this.opened = false;
  }

  dismiss(event: Event): void {
    event.stopPropagation();
    this.hide();
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
