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
import { classes } from '@evidgate/ui/utils';
import { type VariantProps, cva } from 'class-variance-authority';

const badgeVariants = cva(
  'spartan-badge group/badge focus-visible:border-ring focus-visible:ring-ring aria-invalid:border-destructive aria-invalid:ring-destructive/20 dark:aria-invalid:ring-destructive/40 inline-flex w-fit min-w-0 shrink items-center justify-start focus-visible:ring-2 [&>hlm-icon]:pointer-events-none',
  {
    variants: {
      variant: {
        default: 'spartan-badge-variant-default',
        secondary: 'spartan-badge-variant-secondary',
        destructive: 'spartan-badge-variant-destructive',
        outline: 'spartan-badge-variant-outline',
        ghost: 'spartan-badge-variant-ghost',
        link: 'spartan-badge-variant-link',
      },
    },
    defaultVariants: {
      variant: 'default',
    },
  },
);

export type BadgeVariants = VariantProps<typeof badgeVariants>;

@Component({
  selector: '[hlmBadge],hlm-badge',
  exportAs: 'hlmBadge',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    'data-slot': 'badge',
    '[attr.data-variant]': 'variant()',
    '[attr.tabindex]': 'clipped() && !nativeAction ? 0 : null',
    '[class.spartan-badge--clipped]': 'clipped()',
    '(focusin)': 'show()',
    '(focusout)': 'hide()',
    '(mouseenter)': 'show()',
    '(mouseleave)': 'scheduleHide()',
    '(click)': 'show()',
    '(keydown)': 'onKeydown($event)',
  },
  template: `
    <ng-content select="hlm-icon" />
    <span #label class="spartan-badge__label" [attr.aria-hidden]="abbreviated() ? 'true' : null"
      ><ng-content
    /></span>
    @if (abbreviated()) {
      <span class="sr-only">{{ completeValue() }}</span>
    }
    @if (clipped()) {
      <span
        #popover
        class="spartan-badge__full-value"
        popover="manual"
        aria-hidden="true"
        (mouseenter)="cancelHide()"
        (mouseleave)="scheduleHide()"
        >{{ completeValue() }}</span
      >
    }
  `,
})
export class HlmBadge {
  private readonly element = inject<ElementRef<HTMLElement>>(ElementRef);

  readonly variant = input<BadgeVariants['variant']>('default');
  /** Original non-secret identity when the projected preview is deliberately shortened. */
  readonly fullValue = input<string | null>(null);
  protected readonly completeValue = signal('');
  protected readonly abbreviated = signal(false);
  private readonly measuredClipped = signal(false);
  readonly clipped = computed(() => this.abbreviated() || this.measuredClipped());
  protected readonly nativeAction = ['A', 'BUTTON'].includes(this.element.nativeElement.tagName);
  private readonly label = viewChild.required<ElementRef<HTMLElement>>('label');
  private readonly popover = viewChild<ElementRef<HTMLElement>>('popover');
  private hideTimer: number | undefined;
  private opened = false;
  private readonly dismissOnEscape = (event: KeyboardEvent) => {
    if (event.key === 'Escape') this.hide();
  };

  constructor() {
    classes(() => badgeVariants({ variant: this.variant() }));
    afterRenderEffect((onCleanup) => {
      const original = this.fullValue();
      const element = this.label().nativeElement;
      untracked(() => this.hide());
      const measure = () => {
        const text = element.textContent?.trim() ?? '';
        const complete = original ?? text;
        if (complete !== this.completeValue()) this.hide();
        this.completeValue.set(complete);
        this.abbreviated.set(original !== null && text !== original);
        this.measuredClipped.set(
          element.clientWidth > 0 && element.scrollWidth > element.clientWidth,
        );
        if (!this.clipped()) this.hide();
      };
      untracked(measure);
      const resize = typeof ResizeObserver === 'undefined' ? null : new ResizeObserver(measure);
      resize?.observe(element);
      const mutation = new MutationObserver(measure);
      mutation.observe(element, { childList: true, characterData: true, subtree: true });
      onCleanup(() => {
        resize?.disconnect();
        mutation.disconnect();
      });
    });
    inject(DestroyRef).onDestroy(() => this.hide());
  }

  show(): void {
    this.cancelHide();
    const element = this.popover()?.nativeElement;
    if (!this.clipped() || !element || this.opened) return;
    element.showPopover();
    this.opened = true;
    document.addEventListener('keydown', this.dismissOnEscape);
  }

  hide(): void {
    this.cancelHide();
    document.removeEventListener('keydown', this.dismissOnEscape);
    const element = this.popover()?.nativeElement;
    if (this.opened && element?.isConnected) element.hidePopover();
    this.opened = false;
  }

  protected onKeydown(event: KeyboardEvent): void {
    if (event.key === 'Escape' && this.opened) {
      event.stopPropagation();
      this.hide();
    } else if (!this.nativeAction && this.clipped() && ['Enter', ' '].includes(event.key)) {
      event.preventDefault();
      this.show();
    }
  }

  scheduleHide(): void {
    this.cancelHide();
    // Keep the panel open while the pointer crosses into the top layer or the badge has focus.
    this.hideTimer = window.setTimeout(() => {
      if (!this.element.nativeElement.contains(document.activeElement)) this.hide();
    }, 200);
  }

  cancelHide(): void {
    clearTimeout(this.hideTimer);
    this.hideTimer = undefined;
  }
}
