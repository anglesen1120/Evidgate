import { afterRenderEffect, effect, type Signal, type WritableSignal } from '@angular/core';

/** What the two breakpoint handoffs need to find and move the navigator's focus. */
export interface CompactNavigatorFocus {
  readonly document: Document;
  readonly destroyRef: { onDestroy(callback: () => void): void };
  /** True below 768px, where the disclosure owns the list. */
  readonly narrow: Signal<boolean>;
  readonly stepsExpanded: Signal<boolean>;
  /** Whether the disclosure is where the visitor's focus last went. */
  readonly disclosureFocused: WritableSignal<boolean>;
  readonly toggle: () => HTMLElement | undefined;
  readonly stepButtons: () => readonly HTMLElement[];
  /** The button of the step on screen: the control that replaces the disclosure from 768px up. */
  readonly viewedButton: () => HTMLElement | undefined;
}

/**
 * Keeps the compact navigator's focus on something visible across the 768px breakpoint. Below it
 * the disclosure owns the list and the list is hidden while collapsed; above it the five-column
 * navigator is always shown and the disclosure is hidden. Each direction therefore hands the focus
 * over while its own control still exists, and leaves focus alone when the visitor is elsewhere.
 */
export function handOffCompactNavigatorFocus(options: CompactNavigatorFocus): void {
  const {
    document,
    destroyRef,
    narrow,
    stepsExpanded,
    disclosureFocused,
    toggle,
    stepButtons,
    viewedButton,
  } = options;

  // A hide at the breakpoint blurs to `<body>` and fires neither event, so the disclosure keeps
  // its claim only until the visitor focuses or presses anywhere else.
  const forgetUnlessDisclosure = (event: Event): void => {
    const button = toggle();
    if (button !== undefined && event.target instanceof Node && button.contains(event.target)) {
      return;
    }
    disclosureFocused.set(false);
  };
  document.addEventListener('focusin', forgetUnlessDisclosure);
  document.addEventListener('pointerdown', forgetUnlessDisclosure);
  destroyRef.onDestroy(() => {
    document.removeEventListener('focusin', forgetUnlessDisclosure);
    document.removeEventListener('pointerdown', forgetUnlessDisclosure);
  });

  // A view effect runs before the render that hides the list, so its button still holds focus.
  effect(() => {
    if (!narrow() || stepsExpanded()) return;
    if (stepButtons().some((button) => button === button.ownerDocument.activeElement)) {
      toggle()?.focus();
    }
  });

  // A render pass runs after the wide list is in the document, so its button can take the focus
  // the hidden disclosure was holding.
  afterRenderEffect(() => {
    if (narrow() || !disclosureFocused()) return;
    const button = viewedButton();
    if (button === undefined) return;
    disclosureFocused.set(false);
    button.focus();
  });
}
