/**
 * Keeps Tab and Shift+Tab cycling inside an open native modal `<dialog>`.
 *
 * A modal dialog leaves one Tab stop outside itself: from its last control Chromium moves focus to
 * the browser chrome, and `document.activeElement` reads as `<body>`, before the next press re-enters
 * at the dialog element. `CdkTrapFocus` cannot close that gap here — it wraps through guard anchors
 * inserted as the dialog's *siblings*, which a modal dialog keeps inert. Wrapping at the edges in
 * script removes the stop. Only the two edges are handled, so every other press stays the browser's
 * own order: no extra stop, no reordering, and nothing to keep in sync when the controls change.
 *
 * This is the same edge handling `shared/ui/conflict/conflict-dialog.component.ts` (`wrapFocus`)
 * applies, so every native modal dialog in the app answers these keypresses the same way; the
 * members e2e suite pins it for the invite, remove and edit-roles dialogs.
 */
export function wrapDialogFocus(event: Event, dialog: HTMLDialogElement | undefined): void {
  if (dialog === undefined || !dialog.open || !(event instanceof KeyboardEvent)) return;
  const focusable = Array.from(
    dialog.querySelectorAll<HTMLElement>(
      'button, [href], input, select, textarea, summary, [contenteditable], [tabindex]:not([tabindex="-1"])',
    ),
  ).filter(
    (element) =>
      !element.matches(':disabled') &&
      // `visibilityProperty` also drops `visibility: hidden`, which keeps its client rects. jsdom
      // has no layout and reports no `checkVisibility`, so there its elements count as visible.
      (typeof element.checkVisibility !== 'function' ||
        element.checkVisibility({ visibilityProperty: true })) &&
      element.closest('[inert]') === null,
  );
  const first = focusable[0];
  const last = focusable[focusable.length - 1];
  if (first === undefined || last === undefined) return;
  const active = dialog.ownerDocument.activeElement;
  if (event.shiftKey && (active === first || active === dialog)) {
    event.preventDefault();
    last.focus();
  } else if (!event.shiftKey && active === last) {
    event.preventDefault();
    first.focus();
  }
}
