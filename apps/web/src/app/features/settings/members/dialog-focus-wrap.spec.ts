import { afterEach, describe, expect, it } from 'vitest';

import { wrapDialogFocus } from './dialog-focus-wrap';

/**
 * The shared Tab-wrap the invite, remove and edit-roles dialogs bind to their `<dialog>`. jsdom has
 * no tab navigation, so the browser's own order cannot be observed here — what is testable is the
 * helper's contract: it acts only at the two edges, and it never steals a press from the middle of
 * the order. `settings-members-edit-role.spec.ts` proves the same edges in real Chromium.
 */
describe('wrapDialogFocus', () => {
  let dialog: HTMLDialogElement | null = null;

  afterEach(() => {
    dialog?.remove();
    dialog = null;
  });

  function build(options: { open?: boolean; disabledSubmit?: boolean } = {}): {
    dialog: HTMLDialogElement;
    first: HTMLInputElement;
    middle: HTMLInputElement;
    last: HTMLButtonElement;
  } {
    dialog = document.createElement('dialog');
    if (options.open !== false) dialog.open = true;
    const first = document.createElement('input');
    first.type = 'checkbox';
    const middle = document.createElement('input');
    middle.type = 'checkbox';
    const last = document.createElement('button');
    last.textContent = 'Cancel';
    const submit = document.createElement('button');
    submit.textContent = 'Save';
    submit.disabled = options.disabledSubmit !== false;
    dialog.append(first, middle, last, submit);
    document.body.append(dialog);
    first.focus();
    return { dialog, first, middle, last };
  }

  function key(options: { shiftKey?: boolean } = {}): KeyboardEvent {
    return new KeyboardEvent('keydown', {
      key: 'Tab',
      shiftKey: options.shiftKey === true,
      cancelable: true,
      bubbles: true,
    });
  }

  it('wraps Tab on the last enabled control to the first, and consumes the press', () => {
    const built = build();
    built.last.focus();
    const event = key();
    wrapDialogFocus(event, built.dialog);
    expect(event.defaultPrevented).toBe(true);
    expect(document.activeElement).toBe(built.first);
  });

  it('wraps Shift+Tab on the first control to the last enabled one', () => {
    const built = build();
    built.first.focus();
    const event = key({ shiftKey: true });
    wrapDialogFocus(event, built.dialog);
    expect(event.defaultPrevented).toBe(true);
    // The disabled Save is not a stop, so the wrap lands on Cancel.
    expect(document.activeElement).toBe(built.last);
  });

  it('wraps Shift+Tab from the dialog element itself, where a modal can put focus', () => {
    const built = build();
    built.dialog.focus();
    const event = key({ shiftKey: true });
    wrapDialogFocus(event, built.dialog);
    expect(event.defaultPrevented).toBe(true);
    expect(document.activeElement).toBe(built.last);
  });

  it('leaves a press in the middle of the order to the browser', () => {
    const built = build();
    built.middle.focus();
    const forward = key();
    wrapDialogFocus(forward, built.dialog);
    expect(forward.defaultPrevented).toBe(false);
    expect(document.activeElement).toBe(built.middle);

    const backward = key({ shiftKey: true });
    wrapDialogFocus(backward, built.dialog);
    expect(backward.defaultPrevented).toBe(false);
    expect(document.activeElement).toBe(built.middle);
  });

  it('does nothing while the dialog is closed', () => {
    const built = build({ open: false });
    // A closed dialog is not focusable, so nothing inside it can hold focus: what matters is that
    // the helper consumes nothing and moves nothing.
    built.last.focus();
    const before = document.activeElement;
    const event = key();
    wrapDialogFocus(event, built.dialog);
    expect(event.defaultPrevented).toBe(false);
    expect(document.activeElement).toBe(before);
  });

  it('ignores an event that is not a keyboard press, and a dialog it was not given', () => {
    const built = build();
    built.last.focus();
    const click = new MouseEvent('click', { bubbles: true, cancelable: true });
    wrapDialogFocus(click, built.dialog);
    expect(click.defaultPrevented).toBe(false);
    expect(document.activeElement).toBe(built.last);

    const event = key();
    wrapDialogFocus(event, undefined);
    expect(event.defaultPrevented).toBe(false);
  });
});
