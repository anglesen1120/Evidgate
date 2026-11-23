function overlaps(a: DOMRect, b: DOMRect): boolean {
  return a.bottom > b.top && a.top < b.bottom && a.right > b.left && a.left < b.right;
}

/** CSS positions the native top-layer panel; older engines can ignore the implicit anchor. */
function positionPanel(trigger: HTMLElement, panel: HTMLElement, scrollport?: DOMRect): boolean {
  const anchor = trigger.getBoundingClientRect();
  const viewport = trigger.ownerDocument.documentElement;
  const width = viewport.clientWidth;
  const height = viewport.clientHeight;
  if (
    anchor.bottom <= 0 ||
    anchor.top >= height ||
    anchor.right <= 0 ||
    anchor.left >= width ||
    (scrollport && !overlaps(anchor, scrollport))
  ) {
    return false;
  }
  const box = panel.getBoundingClientRect();
  const top = anchor.bottom + box.height <= height ? anchor.bottom : anchor.top - box.height;
  const clampedTop = Math.max(0, Math.min(top, height - box.height));
  // An engine can support popovers without resolving their implicit CSS anchor. Check the
  // actual placement, including oversized panels for which neither CSS position fits.
  if (
    panel.style.top ||
    Math.abs(box.top - clampedTop) > 1 ||
    box.left < 0 ||
    box.right > width ||
    box.right <= anchor.left ||
    box.left >= anchor.right
  ) {
    panel.classList.add('eg-measured-position');
    panel.style.top = `${clampedTop}px`;
    panel.style.left = `${Math.max(0, Math.min(anchor.left, width - box.width))}px`;
  }
  return true;
}

export function openFullValuePopover(
  trigger: HTMLElement,
  panel: HTMLElement,
  dismiss: () => void,
): () => void {
  panel.showPopover({ source: trigger });
  positionPanel(trigger, panel);

  const events = new AbortController();
  const { signal } = events;
  window.addEventListener('resize', dismiss, { signal });
  window.addEventListener(
    'scroll',
    (event) => {
      const target = event.target;
      if (target instanceof Element && (panel.contains(target) || !target.contains(trigger)))
        return;
      const bounds = target instanceof Element ? target.getBoundingClientRect() : undefined;
      if (!positionPanel(trigger, panel, bounds)) dismiss();
    },
    { capture: true, signal },
  );
  document.addEventListener(
    'keydown',
    (event) => {
      if (event.key === 'Escape') dismiss();
    },
    { signal },
  );
  return () => {
    events.abort();
    if (panel.isConnected) panel.hidePopover();
    panel.classList.remove('eg-measured-position');
    panel.style.removeProperty('top');
    panel.style.removeProperty('left');
  };
}
