// 09.2.1-T2.2; state sheet: docs/design/web/state-sheet.md#shared-accessibility-and-themes.
import { provideZonelessChangeDetection } from '@angular/core';
import { type ComponentFixture, TestBed } from '@angular/core/testing';
import type { Mock } from 'vitest';

import { ConnectStepRevealComponent } from './connect-step-reveal.component';

const secret = 'eg_once_<private>&credential';
const warning = 'Shown once. If copying fails, select and copy the value manually.';

// Default: read-only reveal; hover/focus/active: native controls + action recipe A.
// Loading: only Copy disabled; error: manual selection + warning; success: polite confirmation.
// Empty: caller omits the reveal; overflow: shrinkable input + wrapping copy; no animation/timer.
describe('ConnectStepRevealComponent', () => {
  let fixture: ComponentFixture<ConnectStepRevealComponent>;
  let root: HTMLElement;
  let writeText: Mock;
  let originalClipboard: PropertyDescriptor | undefined;

  const control = () => root.querySelector<HTMLInputElement>('[data-testid="reveal-value"]')!;
  const copy = () => root.querySelector<HTMLButtonElement>('[data-testid="reveal-copy"]')!;
  const status = () => root.querySelector<HTMLElement>('[data-testid="reveal-copy-status"]')!;

  beforeEach(async () => {
    originalClipboard = Object.getOwnPropertyDescriptor(navigator, 'clipboard');
    writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', {
      configurable: true,
      value: { writeText },
    });
    await TestBed.configureTestingModule({
      imports: [ConnectStepRevealComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    fixture = TestBed.createComponent(ConnectStepRevealComponent);
    fixture.componentRef.setInput('heading', 'Save this token');
    fixture.componentRef.setInput('value', secret);
    fixture.componentRef.setInput('warning', warning);
    fixture.componentRef.setInput('copyLabel', 'Copy token');
    fixture.componentRef.setInput('copiedLabel', 'Token copied');
    fixture.componentRef.setInput('dismissLabel', 'Dismiss token');
    fixture.detectChanges();
    root = fixture.nativeElement as HTMLElement;
  });

  afterEach(() => {
    fixture.destroy();
    if (originalClipboard) {
      Object.defineProperty(navigator, 'clipboard', originalClipboard);
    } else {
      Reflect.deleteProperty(navigator, 'clipboard');
    }
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  it('copies the exact value once and announces only the localized success label', async () => {
    copy().click();
    await Promise.resolve();
    fixture.detectChanges();
    expect(writeText).toHaveBeenCalledExactlyOnceWith(secret);
    expect(status().textContent?.trim()).toBe('Token copied');
    expect(status().getAttribute('aria-live')).toBe('polite');
    expect(status().getAttribute('aria-atomic')).toBe('true');
    expect(status().textContent).not.toContain(secret);
    expect(control().value).toBe(secret);
  });

  it('announces manual-copy guidance and selects the still-visible value after rejection', async () => {
    writeText.mockRejectedValue(new Error('clipboard denied'));
    copy().click();
    await Promise.resolve();
    fixture.detectChanges();
    expect(status().textContent?.trim()).toBe(warning);
    expect(status().textContent).not.toContain(secret);
    expect(document.activeElement).toBe(control());
    expect(control().value).toBe(secret);
    expect(control().selectionStart).toBe(0);
    expect(control().selectionEnd).toBe(secret.length);
    expect(copy().disabled).toBe(false);
  });

  it('uses the same manual-selection fallback when the Clipboard API is unavailable', async () => {
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: undefined });
    copy().click();
    await Promise.resolve();
    fixture.detectChanges();
    expect(status().textContent?.trim()).toBe(warning);
    expect(control().selectionEnd).toBe(secret.length);
    expect(writeText).not.toHaveBeenCalled();
  });

  it('emits dismiss exactly once without copying', () => {
    const dismissed = vi.fn();
    fixture.componentInstance.dismissed.subscribe(dismissed);
    root.querySelector<HTMLButtonElement>('[data-testid="reveal-dismiss"]')!.click();
    expect(dismissed).toHaveBeenCalledExactlyOnceWith(undefined);
    expect(writeText).not.toHaveBeenCalled();
  });

  it('keeps a named read-only value before Copy and Dismiss in native keyboard order', () => {
    const buttons = [...root.querySelectorAll('button')];
    expect(buttons.map((button) => button.dataset['testid'])).toEqual([
      'reveal-copy',
      'reveal-dismiss',
    ]);
    expect(buttons.every((button) => button.type === 'button' && button.tabIndex === 0)).toBe(true);
    expect(control().readOnly).toBe(true);
    expect(control().disabled).toBe(false);
    expect(control().tabIndex).toBe(0);
    expect(control().autocomplete).toBe('off');
    const heading = document.getElementById(control().getAttribute('aria-labelledby')!);
    const description = document.getElementById(control().getAttribute('aria-describedby')!);
    expect(heading?.textContent).toBe('Save this token');
    expect(description?.textContent).toBe(warning);
    control().focus();
    expect(document.activeElement).toBe(control());
    for (const button of buttons) {
      button.focus();
      expect(document.activeElement).toBe(button);
    }
  });

  it('does not echo the value into host attributes, copy status or any other element', () => {
    expect(control().value).toBe(secret);
    const withoutControl = root.cloneNode(true) as HTMLElement;
    withoutControl.querySelector('[data-testid="reveal-value"]')!.remove();
    expect(withoutControl.outerHTML).not.toContain(secret);
    for (const attribute of control().attributes) {
      if (attribute.name !== 'value') expect(attribute.value).not.toContain(secret);
    }
    expect(root.textContent).not.toContain(secret);
  });

  it('guards one in-flight copy while Dismiss remains available', async () => {
    // ES2022 project lib does not expose Promise.withResolvers.
    let resolve!: () => void;
    const promise = new Promise<void>((finish) => {
      resolve = finish;
    });
    writeText.mockReturnValue(promise);
    copy().click();
    fixture.detectChanges();
    expect(copy().disabled).toBe(true);
    expect(copy().getAttribute('aria-busy')).toBe('true');
    expect(root.querySelector<HTMLButtonElement>('[data-testid="reveal-dismiss"]')!.disabled).toBe(
      false,
    );
    copy().click();
    expect(writeText).toHaveBeenCalledTimes(1);
    resolve();
    await Promise.resolve();
    fixture.detectChanges();
    expect(copy().disabled).toBe(false);
  });

  it('clears stale success on replacement and copies the replacement value', async () => {
    copy().click();
    await Promise.resolve();
    fixture.detectChanges();
    fixture.componentRef.setInput('value', 'replacement-credential');
    fixture.detectChanges();
    expect(status().textContent?.trim()).toBe('');
    expect(control().value).toBe('replacement-credential');
    copy().click();
    await Promise.resolve();
    expect(writeText).toHaveBeenLastCalledWith('replacement-credential');
  });

  it('ignores a superseded clipboard answer after the value changes', async () => {
    let resolve!: () => void;
    const promise = new Promise<void>((finish) => {
      resolve = finish;
    });
    writeText.mockReturnValue(promise);
    copy().click();
    fixture.componentRef.setInput('value', 'replacement-credential');
    fixture.detectChanges();
    resolve();
    await Promise.resolve();
    fixture.detectChanges();
    expect(status().textContent?.trim()).toBe('');
    expect(copy().disabled).toBe(false);
    expect(control().value).toBe('replacement-credential');
  });

  it('leaves no copied-status timer to fire after destruction and clears the rendered value', async () => {
    vi.useFakeTimers();
    copy().click();
    await Promise.resolve();
    fixture.detectChanges();
    const valueControl = control();
    fixture.destroy();
    expect(valueControl.value).toBe('');
    expect(vi.getTimerCount()).toBe(0);
    expect(() => vi.runAllTimers()).not.toThrow();
  });

  it.each(['resolve', 'reject'] as const)(
    'ignores a clipboard %s arriving after destruction',
    async (outcome) => {
      let resolve!: () => void;
      let reject!: (error: Error) => void;
      const promise = new Promise<void>((finish, fail) => {
        resolve = finish;
        reject = fail;
      });
      writeText.mockReturnValue(promise);
      copy().click();
      const valueControl = control();
      const liveRegion = status();
      fixture.destroy();
      if (outcome === 'resolve') resolve();
      else reject(new Error('clipboard denied'));
      await Promise.resolve();
      expect(valueControl.value).toBe('');
      expect(liveRegion.textContent?.trim()).toBe('');
    },
  );

  it('retains a long literal value without creating markup', () => {
    const longValue = '<script>private</script>' + 'x'.repeat(4096);
    fixture.componentRef.setInput('value', longValue);
    fixture.detectChanges();
    expect(control().value).toBe(longValue);
    expect(root.querySelector('script')).toBeNull();
    expect(root.textContent).not.toContain(longValue);
  });
});
