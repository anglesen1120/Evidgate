import { afterRenderEffect, type ElementRef, type Signal } from '@angular/core';

/** A wrapping verdict header must leave room for the tab controls even at high zoom. */
export function observeVerdictHeight(
  host: HTMLElement,
  verdict: Signal<ElementRef<HTMLElement> | undefined>,
): void {
  afterRenderEffect((cleanup) => {
    const header = verdict()?.nativeElement;
    if (!header || typeof ResizeObserver !== 'function') return;
    const observer = new ResizeObserver(() => {
      host.style.setProperty('--run-verdict-height', `${header.offsetHeight}px`);
    });
    observer.observe(header);
    cleanup(() => observer.disconnect());
  });
}

/** Scroll only the tab strip. Selecting a tab never scrolls the page horizontally. */
export function revealSelectedTab(selected: string, strip?: HTMLElement): void {
  if (!strip || strip.scrollWidth <= strip.clientWidth) return;
  const tab = strip.querySelector<HTMLElement>(`[data-run-tab="${selected}"]`);
  if (!tab) return;
  const box = strip.getBoundingClientRect();
  const inset = strip.clientLeft + Number.parseFloat(getComputedStyle(strip).paddingInlineStart);
  const { left, right } = tab.getBoundingClientRect();
  const delta = Math.min(0, left - box.left - inset) || Math.max(0, right - box.right + inset);
  if (delta) strip.scrollLeft += delta;
}
