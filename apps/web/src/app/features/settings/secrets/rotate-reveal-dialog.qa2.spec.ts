// QA2 independent gate (09.7.1-T3 AC3 "cleared on dismiss" in the hardest shape): the reveal
// dialog's own guard against a queued native `close` event, and a replacement reveal. The suite's
// spec covers dismiss/Escape/destroy and focus wrapping; these cases are the branch in
// `RotateRevealDialogComponent.closed()` and the "one value on screen at a time" property.
import { TestBed } from '@angular/core/testing';
import { RevealSlot } from '../../../shared/ui/reveal/one-time-reveal.slot';
import { RotateRevealDialogComponent } from './rotate-reveal-dialog.component';

const showModalDescriptor = Object.getOwnPropertyDescriptor(
  HTMLDialogElement.prototype,
  'showModal',
);
const closeDescriptor = Object.getOwnPropertyDescriptor(HTMLDialogElement.prototype, 'close');

describe('RotateRevealDialog queued close and replacement (09.7.1-T3 QA2)', () => {
  beforeEach(() => {
    Object.defineProperty(HTMLDialogElement.prototype, 'showModal', {
      configurable: true,
      value: function (this: HTMLDialogElement) {
        this.open = true;
      },
    });
    Object.defineProperty(HTMLDialogElement.prototype, 'close', {
      configurable: true,
      value: function (this: HTMLDialogElement) {
        this.open = false;
      },
    });
    TestBed.configureTestingModule({ providers: [RevealSlot] });
  });

  afterEach(() => {
    TestBed.resetTestingModule();
    if (showModalDescriptor)
      Object.defineProperty(HTMLDialogElement.prototype, 'showModal', showModalDescriptor);
    else Reflect.deleteProperty(HTMLDialogElement.prototype, 'showModal');
    if (closeDescriptor)
      Object.defineProperty(HTMLDialogElement.prototype, 'close', closeDescriptor);
    else Reflect.deleteProperty(HTMLDialogElement.prototype, 'close');
    vi.restoreAllMocks();
  });

  function mount() {
    const fixture = TestBed.createComponent(RotateRevealDialogComponent);
    fixture.detectChanges();
    const opener = document.createElement('button');
    document.body.append(opener);
    return {
      fixture,
      opener,
      slot: TestBed.inject(RevealSlot),
      root: fixture.nativeElement as HTMLElement,
    };
  }

  it('a queued native close never clears a newly opened reveal', async () => {
    const { fixture, opener, slot, root } = mount();
    slot.show({ source: 'ingest-token-rotation', value: 'first-one-time-value' });
    fixture.componentInstance.open(opener, null, vi.fn());
    fixture.detectChanges();
    await fixture.whenStable();
    const dialog = root.querySelector('dialog');
    if (!dialog) throw new Error('Expected the dialog');

    // An older close event arriving while this dialog is open must not wipe its value.
    dialog.dispatchEvent(new Event('close'));
    fixture.detectChanges();
    expect(dialog.open).toBe(true);
    expect(slot.current()?.value).toBe('first-one-time-value');
    expect(root.querySelector<HTMLInputElement>('[data-testid="reveal-value"]')?.value).toBe(
      'first-one-time-value',
    );

    // And the same event once the dialog is really closed does clear it, so the guard is a guard
    // and not an unbound listener: a closed dialog must never keep the value.
    dialog.open = false;
    dialog.dispatchEvent(new Event('close'));
    fixture.detectChanges();
    expect(slot.current()).toBeNull();
    expect(root.querySelector<HTMLInputElement>('[data-testid="reveal-value"]')).toBeNull();
    expect(root.outerHTML).not.toContain('first-one-time-value');
    opener.remove();
  });

  it('replaces the previous value without leaving any of it behind', async () => {
    const { fixture, opener, slot, root } = mount();
    slot.show({ source: 'ingest-token-rotation', value: 'value-to-replace' });
    fixture.componentInstance.open(opener, null, vi.fn());
    fixture.detectChanges();
    await fixture.whenStable();
    expect(root.querySelector<HTMLInputElement>('[data-testid="reveal-value"]')?.value).toBe(
      'value-to-replace',
    );

    slot.show({ source: 'ingest-token-rotation', value: 'replacement-value' });
    fixture.detectChanges();
    await fixture.whenStable();

    expect(root.querySelector<HTMLInputElement>('[data-testid="reveal-value"]')?.value).toBe(
      'replacement-value',
    );
    expect(root.querySelectorAll('[data-testid="reveal-value"]')).toHaveLength(1);
    expect(root.outerHTML).not.toContain('value-to-replace');
    fixture.componentInstance.close(false);
    opener.remove();
  });
});
