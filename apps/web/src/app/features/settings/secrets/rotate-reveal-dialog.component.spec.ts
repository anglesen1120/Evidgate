import { TestBed } from '@angular/core/testing';
import { RevealSlot } from '../../../shared/ui/reveal/one-time-reveal.slot';
import { RotateRevealDialogComponent } from './rotate-reveal-dialog.component';

const showModalDescriptor = Object.getOwnPropertyDescriptor(
  HTMLDialogElement.prototype,
  'showModal',
);
const closeDescriptor = Object.getOwnPropertyDescriptor(HTMLDialogElement.prototype, 'close');
describe('RotateRevealDialogComponent', () => {
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
  it.each(['dismiss', 'escape', 'destroy'])('clears slot and live input on %s', async (action) => {
    const fixture = TestBed.createComponent(RotateRevealDialogComponent);
    const slot = TestBed.inject(RevealSlot);
    const opener = document.createElement('button');
    document.body.append(opener);
    fixture.detectChanges();
    slot.show({ source: 'ingest-token-rotation', value: 'one-time-rotation-value' });
    fixture.componentInstance.open(opener, '2026-10-26T00:00:00Z', vi.fn());
    fixture.detectChanges();
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    const input = root.querySelector<HTMLInputElement>('[data-testid="reveal-value"]');
    expect(input?.value).toBe('one-time-rotation-value');
    expect(root.querySelector('[data-testid="reveal-copy"]')).not.toBeNull();
    expect(root.textContent).toContain('The previous token keeps working until');
    if (action === 'destroy') fixture.destroy();
    else if (action === 'escape')
      root.querySelector('dialog')?.dispatchEvent(new Event('cancel', { cancelable: true }));
    else root.querySelector<HTMLButtonElement>('[data-testid="reveal-dismiss"]')?.click();
    if (action !== 'destroy') {
      fixture.detectChanges();
      await fixture.whenStable();
      expect(document.activeElement).toBe(opener);
    }
    expect(slot.current()).toBeNull();
    expect(input?.value).toBe('');
    expect(root.outerHTML).not.toContain('one-time-rotation-value');
    opener.remove();
  });
  it('wraps Tab and Shift+Tab inside the native reveal dialog', async () => {
    const fixture = TestBed.createComponent(RotateRevealDialogComponent);
    fixture.detectChanges();
    TestBed.inject(RevealSlot).show({
      source: 'ingest-token-rotation',
      value: 'keyboard-copy-once',
    });
    fixture.componentInstance.open(document.createElement('button'), null, vi.fn());
    fixture.detectChanges();
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    const first = root.querySelector<HTMLInputElement>('[data-testid="reveal-value"]');
    const last = root.querySelector<HTMLButtonElement>('[data-testid="reveal-dismiss"]');
    if (!first || !last) throw new Error('Expected reveal controls');
    last.focus();
    last.dispatchEvent(
      new KeyboardEvent('keydown', { key: 'Tab', bubbles: true, cancelable: true }),
    );
    expect(document.activeElement).toBe(first);
    first.dispatchEvent(
      new KeyboardEvent('keydown', { key: 'Tab', shiftKey: true, bubbles: true, cancelable: true }),
    );
    expect(document.activeElement).toBe(last);
  });
  it('announces copy failure and leaves the value selectable for manual copying', async () => {
    const fixture = TestBed.createComponent(RotateRevealDialogComponent);
    vi.stubGlobal('navigator', {
      clipboard: { writeText: vi.fn().mockRejectedValue(new Error('denied')) },
    });
    fixture.detectChanges();
    TestBed.inject(RevealSlot).show({ source: 'ingest-token-rotation', value: 'copy-once' });
    fixture.componentInstance.open(document.createElement('button'), null, vi.fn());
    fixture.detectChanges();
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    root.querySelector<HTMLButtonElement>('[data-testid="reveal-copy"]')?.click();
    await fixture.whenStable();
    expect(root.querySelector('[data-testid="reveal-copy-status"]')?.textContent).toContain(
      'select and copy',
    );
    vi.unstubAllGlobals();
  });
});
