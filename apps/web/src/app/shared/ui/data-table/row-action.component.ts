import {
  afterRenderEffect,
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  ElementRef,
  inject,
  input,
  Renderer2,
  signal,
  viewChild,
} from '@angular/core';
import { buttonVariants } from '@evidgate/ui/button';
import { HlmIcon, type HlmIconName } from '@evidgate/ui/icon';
import { openFullValuePopover } from '@evidgate/ui/utils';
import { BrnButton } from '@spartan-ng/brain/button';
import { escapeFormatChars } from '../values/ref-name-display';

const ROW_ACTION_CLASSES = buttonVariants({ variant: 'ghost', size: 'icon-sm' });

@Component({
  selector: 'button[appRowAction], a[appRowAction]',
  imports: [HlmIcon],
  hostDirectives: [{ directive: BrnButton, inputs: ['disabled'] }],
  styleUrl: '../../../ui/utils/lib/full-value-popover.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    '[class]': 'classes',
    'data-slot': 'button',
    '[attr.type]': 'isButton ? "button" : null',
    '[attr.aria-label]': 'safeLabel()',
    '[class.row-action-danger]': 'tone() === "danger"',
    '(focus)': 'showTooltip()',
    '(blur)': 'hideTooltip()',
    '(pointerenter)': 'hover($event)',
    '(pointerleave)': 'leave()',
    '(click)': 'hideTooltip()',
    '(keydown.escape)': 'dismiss($event)',
  },
  template: `
    <hlm-icon class="size-(--eg-space-5)" [name]="icon()" />
    <span
      #tooltip
      popover="manual"
      class="spartan-tooltip-content w-fit"
      [id]="tooltipId"
      [attr.role]="expanded() ? 'tooltip' : null"
      (pointerenter)="showTooltip()"
      (pointerleave)="leave()"
      ><bdi>{{ safeLabel() }}</bdi></span
    >
  `,
  styles: `
    :host {
      border-radius: var(--eg-radius-sm);
    }
    :host(.row-action-danger) {
      color: var(--eg-danger-fg);
    }
    :host(.row-action-danger:hover) {
      background: var(--eg-danger-bg);
    }
  `,
})
export class RowActionComponent {
  private readonly element = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly renderer = inject(Renderer2);
  private readonly destroyRef = inject(DestroyRef);
  private readonly tooltip = viewChild.required<ElementRef<HTMLElement>>('tooltip');
  private closePanel: (() => void) | undefined;
  readonly nativeElement = this.element.nativeElement;
  protected readonly tooltipId = `row-action-tooltip-${crypto.randomUUID()}`;
  protected readonly expanded = signal(false);
  protected readonly classes = ROW_ACTION_CLASSES;
  protected readonly isButton = this.element.nativeElement.tagName === 'BUTTON';
  readonly label = input.required<string>();
  protected readonly safeLabel = computed(() => escapeFormatChars(this.label()));
  readonly icon = input<HlmIconName>('lucideEye');
  readonly tone = input<'neutral' | 'danger'>('neutral');

  constructor() {
    // Consumer aria-label bindings win host bindings; normalize after that DOM write too.
    afterRenderEffect(() => {
      this.label();
      this.renderer.setAttribute(this.nativeElement, 'aria-label', this.safeLabel());
    });
    this.destroyRef.onDestroy(() => this.hideTooltip());
  }

  protected showTooltip(): void {
    if (this.closePanel || this.nativeElement.matches(':disabled')) return;
    this.closePanel = openFullValuePopover(this.nativeElement, this.tooltip().nativeElement, () =>
      this.hideTooltip(),
    );
    const description = this.nativeElement.getAttribute('aria-describedby');
    this.renderer.setAttribute(
      this.nativeElement,
      'aria-describedby',
      [description, this.tooltipId].filter(Boolean).join(' '),
    );
    this.expanded.set(true);
  }

  protected hideTooltip(): void {
    this.closePanel?.();
    this.closePanel = undefined;
    const remaining = (this.nativeElement.getAttribute('aria-describedby') ?? '')
      .split(/\s+/)
      .filter((id) => id && id !== this.tooltipId)
      .join(' ');
    if (remaining) this.renderer.setAttribute(this.nativeElement, 'aria-describedby', remaining);
    else this.renderer.removeAttribute(this.nativeElement, 'aria-describedby');
    this.expanded.set(false);
  }

  protected hover(event: Event): void {
    if ('pointerType' in event && (event.pointerType === 'mouse' || event.pointerType === 'pen')) {
      this.showTooltip();
    }
  }

  protected leave(): void {
    if (this.nativeElement.ownerDocument.activeElement !== this.nativeElement) this.hideTooltip();
  }

  protected dismiss(event: Event): void {
    if (!this.closePanel) return;
    event.stopPropagation();
    this.hideTooltip();
  }
}
