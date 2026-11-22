/* Fixture-only: an exact local dialog ID, never a selector, record fetch or write. */
(() => {
  const openers = new WeakMap();
  const open = (id, trigger) => {
    const next = document.getElementById(id);
    if (!(next instanceof HTMLDialogElement) || next.open || next.closest('[hidden]')) return;
    const previous = trigger?.closest('dialog[open]');
    const opener = previous ? openers.get(previous) : trigger;
    previous?.close();
    openers.set(next, opener);
    next.showModal(); // Native inert background, focus trap, autofocus and Escape.
    next.addEventListener('close', () => {
      if (!document.querySelector('dialog[open]')) (opener?.getClientRects().length ? opener : document.querySelector('main[tabindex]'))?.focus({preventScroll:true});
    }, {once:true});
  };
  document.addEventListener('click', event => {
    const button = event.target.closest('[data-dialog-open],[data-dialog-close]');
    if (button?.hasAttribute('data-dialog-open')) open(button.dataset.dialogOpen, button);
    else if (button) { const dialog = button.closest('dialog'); if (dialog?.dispatchEvent(new Event('cancel', {cancelable:true}))) dialog.close(); }
  });
  const id = new URLSearchParams(location.search).get('dialog');
  if (id) open(id, [...document.querySelectorAll('[data-dialog-open]')].find(button => button.dataset.dialogOpen === id && button.getClientRects().length));
})();
