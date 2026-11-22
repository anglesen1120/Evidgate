import { afterRenderEffect } from '@angular/core';

/** Reveal the viewed step without handing it focus or scrolling any ancestor of the strip. */
export function revealViewedStep(viewedButton: () => HTMLElement | undefined): void {
  afterRenderEffect((onCleanup) => {
    const button = viewedButton();
    const strip = button?.closest('ol');
    if (!button || !strip) return;

    const reveal = (): void => {
      const bounds = strip.getBoundingClientRect();
      if (!bounds.width) return; // Collapsed compact disclosure.
      const step = button.getBoundingClientRect();
      if (!step.width) return;
      const left = step.left - bounds.left;
      const right = step.right - bounds.right;
      // For an oversized translated label, don't pan if it already spans the viewport.
      if (left < 0 && right > 0) return;
      if (left < 0) strip.scrollLeft += step.width > bounds.width ? right : left;
      else if (right > 0) strip.scrollLeft += step.width > bounds.width ? left : right;
    };

    reveal();
    if (typeof ResizeObserver === 'undefined') return;
    const observer = new ResizeObserver(reveal);
    observer.observe(strip);
    // A font swap can move the viewed step even when only an earlier translated label grows.
    for (const item of strip.children) observer.observe(item);
    onCleanup(() => observer.disconnect());
  });
}
