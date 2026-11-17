import { afterRenderEffect, effect, type Signal } from '@angular/core';

/** What the two breakpoint handoffs need to find and move the navigator's focus. */
export interface CompactNavigatorFocus {
  readonly document: Document;
  /** True below 768px, where the disclosure owns the list. */
  readonly narrow: Signal<boolean>;
  readonly stepsExpanded: Signal<boolean>;
  readonly toggle: () => HTMLElement | undefined;
  readonly stepButtons: () => readonly HTMLElement[];
  /** The button of the step on screen: the control that replaces the disclosure from 768px up. */
  readonly viewedButton: () => HTMLElement | undefined;
}

/** Which control the focus is due to move to once the render for the new layout has happened. */
type Handoff = 'viewed-step' | 'disclosure' | undefined;

/**
 * Keeps the compact navigator's focus on something visible across the 768px breakpoint. Below it
 * the disclosure owns the list and the list is hidden while collapsed; above it the five-column
 * navigator is always shown and the disclosure is hidden. Both the list and the disclosure take
 * their visibility from `narrow()`, so the change of layout and the focus handoff happen in the
 * same render instead of whenever a media query happens to hide the focused button. Focus moves
 * only when the disclosure or a step button held it, so a visitor reading the page keeps their
 * place.
 */
export function handOffCompactNavigatorFocus(options: CompactNavigatorFocus): void {
  const { document, narrow, stepsExpanded, toggle, stepButtons, viewedButton } = options;

  let handoff: Handoff;
  let previous: boolean | undefined;

  // Runs before the render that swaps the layouts, while the control that is about to leave the
  // layout is still the active element: nothing in CSS hides it ahead of this pass any more.
  effect(() => {
    const isNarrow = narrow();
    stepsExpanded();
    if (previous === undefined) {
      previous = isNarrow;
      return;
    }
    if (previous === isNarrow) return;
    previous = isNarrow;
    const active = document.activeElement;
    handoff = isNarrow
      ? active !== null && stepButtons().includes(active as HTMLElement)
        ? 'disclosure'
        : undefined
      : active === toggle()
        ? 'viewed-step'
        : undefined;
  });

  // Runs after that render, so the control being handed the focus is already the one on screen.
  afterRenderEffect(() => {
    narrow();
    const due = handoff;
    if (due === undefined) return;
    handoff = undefined;
    (due === 'disclosure' ? toggle() : viewedButton())?.focus();
  });
}
