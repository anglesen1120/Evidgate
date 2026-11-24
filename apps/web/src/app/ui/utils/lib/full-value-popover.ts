function overlaps(a: DOMRect, b: DOMRect): boolean {
  return a.bottom > b.top && a.top < b.bottom && a.right > b.left && a.left < b.right;
}

/** Place above the anchor or beside it, clamped horizontally inside `within`. */
export interface AbovePlacement {
  readonly above: Element;
  readonly within: Element;
}

/** CSS positions the native top-layer panel; older engines can ignore the implicit anchor. */
function positionPanel(
  trigger: HTMLElement,
  panel: HTMLElement,
  placement?: AbovePlacement,
  scrollport?: DOMRect,
): boolean {
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
  if (placement) {
    return positionAbove(panel, placement, width, height);
  }
  const box = panel.getBoundingClientRect();
  const top = anchor.bottom + box.height <= height ? anchor.bottom : anchor.top - box.height;
  const clampedTop = Math.max(0, Math.min(top, height - box.height));
  // An engine can support popovers without resolving their implicit CSS anchor. Check the
  // actual placement, including oversized panels for which neither CSS position fits.
  if (
    panel.style.top ||
    Math.abs(box.top - clampedTop) > 1 ||
    box.left < -1 ||
    box.right > width + 1 ||
    box.right <= anchor.left ||
    box.left >= anchor.right
  ) {
    if (!panel.style.top) panel.classList.add('eg-measured-position');
    const topPx = `${clampedTop}px`;
    const leftPx = `${Math.max(0, Math.min(anchor.left, width - box.width))}px`;
    if (panel.style.top !== topPx) panel.style.top = topPx;
    if (panel.style.left !== leftPx) panel.style.left = leftPx;
  }
  return true;
}

/** If neither side fits, scroll the readout above rather than obscuring content below. */
function positionAbove(
  panel: HTMLElement,
  placement: AbovePlacement,
  width: number,
  height: number,
): boolean {
  const anchor = placement.above.getBoundingClientRect();
  const boundary = placement.within.getBoundingClientRect();
  const left = Math.max(0, boundary.left);
  const right = Math.min(width, boundary.right);
  const bottom = Math.min(height, anchor.bottom);
  if (
    right <= left ||
    anchor.right <= left ||
    anchor.left >= right ||
    bottom <= 0 ||
    anchor.top >= height
  )
    return false;

  panel.classList.add('eg-measured-position');
  panel.style.maxWidth = `${right - left}px`;
  panel.style.maxHeight = `${bottom}px`;
  let box = panel.getBoundingClientRect();
  let x = anchor.left + (anchor.width - box.width) / 2;
  let y = anchor.top - box.height;
  if (y < 0) {
    // A middle bar on a narrow card may need a narrower, wrapping side panel.
    const roomLeft = Math.max(0, anchor.left - left);
    const roomRight = Math.max(0, right - anchor.right);
    const onRight = roomRight >= roomLeft;
    const room = onRight ? roomRight : roomLeft;
    if (room === 0) {
      if (anchor.top <= 0) return false;
      panel.style.maxHeight = `${anchor.top}px`;
      box = panel.getBoundingClientRect();
      if (box.height > anchor.top) return false;
    } else {
      if (room < box.width) {
        panel.style.maxWidth = `${room}px`;
        box = panel.getBoundingClientRect();
        if (box.width > room) return false;
      }
      x = onRight ? anchor.right : anchor.left - box.width;
    }
    y =
      room === 0 ? anchor.top - box.height : Math.max(0, Math.min(anchor.top, bottom - box.height));
  }
  panel.style.left = `${Math.max(left, Math.min(x, right - box.width))}px`;
  panel.style.top = `${y}px`;
  return true;
}

export function openFullValuePopover(
  trigger: HTMLElement,
  panel: HTMLElement,
  dismiss: () => void,
  placement?: AbovePlacement,
): () => void {
  panel.showPopover({ source: trigger });
  if (!positionPanel(trigger, panel, placement) && placement) panel.style.visibility = 'hidden';

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
      if (!positionPanel(trigger, panel, placement, bounds)) dismiss();
      else if (placement && panel.style.visibility === 'hidden')
        panel.style.removeProperty('visibility');
    },
    { capture: true, signal },
  );
  document.addEventListener(
    'keydown',
    (event) => {
      if (event.key !== 'Escape') return;
      // The focused disclosure handles its own Escape without closing an enclosing dialog.
      // Dismiss other open panels before that handler stops propagation.
      const target = event.target;
      if (!(target instanceof Node && (trigger.contains(target) || panel.contains(target)))) {
        dismiss();
      }
    },
    { capture: true, signal },
  );
  return () => {
    events.abort();
    if (panel.isConnected) panel.hidePopover();
    panel.classList.remove('eg-measured-position');
    panel.style.removeProperty('top');
    panel.style.removeProperty('left');
    if (placement) {
      panel.style.removeProperty('max-width');
      panel.style.removeProperty('max-height');
      panel.style.removeProperty('visibility');
    }
  };
}
