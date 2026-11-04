import { InteractivityChecker } from '@angular/cdk/a11y';
import { TestBed } from '@angular/core/testing';
import { RevealSlot } from '../../../shared/ui/reveal/one-time-reveal.slot';
import { RotateRevealDialogComponent } from './rotate-reveal-dialog.component';

function revealRoot(): HTMLElement {
  const root = document.querySelector<HTMLElement>('[role="dialog"]');
  if (!root) throw new Error('Expected reveal overlay');
  return root;
}

describe('RotateRevealDialogComponent', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [RevealSlot] });
    // jsdom has no layout; keep CDK's enabled/tabbable checks and real focus trap.
    vi.spyOn(TestBed.inject(InteractivityChecker), 'isVisible').mockReturnValue(true);
  });
  afterEach(() => {
    TestBed.resetTestingModule();
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
  });

  async function render(value = 'one-time-rotation-value') {
    const fixture = TestBed.createComponent(RotateRevealDialogComponent);
    const slot = TestBed.inject(RevealSlot);
    const opener = document.createElement('button');
    document.body.append(opener);
    opener.focus();
    fixture.detectChanges();
    slot.show({ source: 'ingest-token-rotation', value });
    fixture.componentInstance.open(opener, '2026-10-26T00:00:00Z', vi.fn());
    await fixture.whenStable();
    return { fixture, slot, opener, root: revealRoot() };
  }

  it.each(['dismiss', 'escape', 'backdrop', 'destroy'])(
    'clears slot and live input on %s',
    async (action) => {
      const { fixture, slot, opener, root } = await render();
      const input = root.querySelector<HTMLInputElement>('[data-testid="reveal-value"]');
      expect(input?.value).toBe('one-time-rotation-value');
      expect(root.querySelector('[data-testid="reveal-copy"]')).not.toBeNull();
      expect(root.textContent).toContain('The previous token keeps working until');
      expect(root.getAttribute('aria-labelledby')).toBeTruthy();
      if (action === 'destroy') fixture.destroy();
      else if (action === 'escape') {
        root.dispatchEvent(
          new KeyboardEvent('keydown', {
            key: 'Escape',
            keyCode: 27,
            bubbles: true,
            cancelable: true,
          }),
        );
      } else if (action === 'backdrop') {
        document.querySelector<HTMLElement>('.cdk-overlay-backdrop')?.click();
      } else root.querySelector<HTMLButtonElement>('[data-testid="reveal-dismiss"]')?.click();
      expect(slot.current()).toBeNull();
      expect(input?.value).toBe('');
      if (action !== 'destroy') {
        await fixture.whenStable();
        expect(document.activeElement).toBe(opener);
      }
      expect(document.querySelector('[data-testid="reveal-value"]')).toBeNull();
      expect(document.body.outerHTML).not.toContain('one-time-rotation-value');
      opener.remove();
    },
  );

  it('uses one Brain focus trap for both keyboard boundaries', async () => {
    const { root, opener } = await render();
    const first = root.querySelector<HTMLInputElement>('[data-testid="reveal-value"]');
    const last = root.querySelector<HTMLButtonElement>('[data-testid="reveal-dismiss"]');
    if (!first || !last) throw new Error('Expected reveal controls');
    const anchors = document.querySelectorAll<HTMLElement>('.cdk-focus-trap-anchor');
    expect(anchors).toHaveLength(2);
    last.focus();
    last.dispatchEvent(new KeyboardEvent('keydown', { key: 'Tab', bubbles: true }));
    anchors[1]?.focus();
    expect(document.activeElement).toBe(first);
    first.dispatchEvent(
      new KeyboardEvent('keydown', {
        key: 'Tab',
        shiftKey: true,
        bubbles: true,
      }),
    );
    anchors[0]?.focus();
    expect(document.activeElement).toBe(last);
    opener.remove();
  });

  it('announces copy failure and leaves the value selectable for manual copying', async () => {
    vi.stubGlobal('navigator', {
      clipboard: { writeText: vi.fn().mockRejectedValue(new Error('denied')) },
    });
    const { fixture, root, opener } = await render('copy-once');
    root.querySelector<HTMLButtonElement>('[data-testid="reveal-copy"]')?.click();
    await fixture.whenStable();
    expect(root.querySelector('[data-testid="reveal-copy-status"]')?.textContent).toContain(
      'select and copy',
    );
    expect(root.querySelector<HTMLInputElement>('[data-testid="reveal-value"]')?.value).toBe(
      'copy-once',
    );
    opener.remove();
  });

  it('keeps a replacement reveal when reopening before the old close settles', async () => {
    const { fixture, slot, opener } = await render('value-to-replace');
    fixture.componentInstance.close(false);
    fixture.detectChanges();
    slot.show({ source: 'ingest-token-rotation', value: 'replacement-value' });
    fixture.componentInstance.open(opener, null, vi.fn());
    await fixture.whenStable();
    expect(slot.current()?.value).toBe('replacement-value');
    expect(
      revealRoot().querySelector<HTMLInputElement>('[data-testid="reveal-value"]')?.value,
    ).toBe('replacement-value');
    expect(document.querySelectorAll('[data-testid="reveal-value"]')).toHaveLength(1);
    expect(document.body.outerHTML).not.toContain('value-to-replace');
    fixture.componentInstance.close();
    await fixture.whenStable();
    expect(slot.current()).toBeNull();
    opener.remove();
  });

  it('replaces a value in the open overlay without retaining the old value', async () => {
    const { fixture, slot, opener } = await render('value-to-replace');
    slot.show({ source: 'ingest-token-rotation', value: 'replacement-value' });
    await fixture.whenStable();
    expect(
      revealRoot().querySelector<HTMLInputElement>('[data-testid="reveal-value"]')?.value,
    ).toBe('replacement-value');
    expect(document.querySelectorAll('[data-testid="reveal-value"]')).toHaveLength(1);
    expect(document.body.outerHTML).not.toContain('value-to-replace');
    opener.remove();
  });

  it('uses the fallback when the opener was removed', async () => {
    const { fixture, opener } = await render();
    const fallback = vi.fn();
    fixture.componentInstance.open(opener, null, fallback);
    opener.remove();
    fixture.componentInstance.close();
    await fixture.whenStable();
    expect(fallback).toHaveBeenCalledExactlyOnceWith();
  });
});
