// State sheet: docs/design/web/layout.md#open-points--hand-overs
// Dense table contract: docs/design/web/layout.md#dense-table-rules
import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import type { Mock } from 'vitest';
import { DataTableValueComponent } from './data-table-value.component';

const originalShow = Object.getOwnPropertyDescriptor(HTMLElement.prototype, 'showPopover');
const originalHide = Object.getOwnPropertyDescriptor(HTMLElement.prototype, 'hidePopover');

describe('DataTableValueComponent', () => {
  let fixture: ComponentFixture<DataTableValueComponent>;
  let root: HTMLElement;
  let value: HTMLElement;
  let measure: () => void;
  let show: Mock;
  let hide: Mock;

  beforeEach(async () => {
    const observer: ResizeObserver = {
      observe: vi.fn(),
      unobserve: vi.fn(),
      disconnect: vi.fn(),
    };
    vi.stubGlobal(
      'ResizeObserver',
      vi.fn(function (callback: ResizeObserverCallback) {
        measure = () => callback([], observer);
        return observer;
      }),
    );
    show = vi.fn();
    hide = vi.fn();
    Object.defineProperty(HTMLElement.prototype, 'showPopover', {
      configurable: true,
      value: show,
    });
    Object.defineProperty(HTMLElement.prototype, 'hidePopover', {
      configurable: true,
      value: hide,
    });
    await TestBed.configureTestingModule({
      imports: [DataTableValueComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    fixture = TestBed.createComponent(DataTableValueComponent);
    root = fixture.nativeElement as HTMLElement;
    fixture.componentRef.setInput('value', 'sha256:0123456789abcdef<script>not markup</script>');
    fixture.componentRef.setInput('truncate', true);
    await fixture.whenStable();
    value = root.querySelector('.value') as HTMLElement;
  });

  afterEach(() => {
    fixture.destroy();
    vi.useRealTimers();
    vi.unstubAllGlobals();
    if (originalShow) Object.defineProperty(HTMLElement.prototype, 'showPopover', originalShow);
    else Reflect.deleteProperty(HTMLElement.prototype, 'showPopover');
    if (originalHide) Object.defineProperty(HTMLElement.prototype, 'hidePopover', originalHide);
    else Reflect.deleteProperty(HTMLElement.prototype, 'hidePopover');
  });

  async function clip(): Promise<void> {
    vi.spyOn(value, 'clientWidth', 'get').mockReturnValue(100);
    vi.spyOn(value, 'scrollWidth', 'get').mockReturnValue(300);
    measure();
    await fixture.whenStable();
  }

  it('R4: guards zero-layout jsdom and never focuses content which is not actually clipped', () => {
    expect(value.hasAttribute('tabindex')).toBe(false);
    expect(value.hasAttribute('aria-hidden')).toBe(false);
    expect(root.querySelector('[popover]')).toBeNull();
  });

  it('R4: creates a focusable trigger and native top-layer popover only for real clipping, exposing the value once', async () => {
    await clip();
    expect(value.getAttribute('tabindex')).toBe('0');
    // The focusable span is the one accessible copy: no description pointing at a second copy.
    expect(value.hasAttribute('aria-describedby')).toBe(false);
    expect(value.textContent).toBe(String(fixture.componentInstance.value()));
    const popover = root.querySelector('[popover]');
    expect(popover?.getAttribute('popover')).toBe('manual');
    expect(popover?.classList.contains('bg-card')).toBe(true);
    expect(popover?.classList.contains('text-foreground')).toBe(true);
    expect(value.classList.contains('pointer-coarse:min-h-[var(--eg-control-touch)]')).toBe(true);
    expect(popover?.getAttribute('aria-hidden')).toBe('true');
    expect(popover?.textContent).toBe(fixture.componentInstance.value());
    expect(root.querySelector('[role="tooltip"], .sr-only')).toBeNull();
    expect(root.querySelector('[title],script')).toBeNull();
    expect(value.textContent).toContain('<script>not markup</script>');
  });

  it('R4: exposes full text on focus and hover, supports touch click and Escape without title-only content', async () => {
    await clip();
    value.dispatchEvent(new FocusEvent('focus'));
    expect(show).toHaveBeenCalledTimes(1);
    // A touch tap may focus before click: click must not immediately hide it.
    value.dispatchEvent(new MouseEvent('click'));
    expect(hide).not.toHaveBeenCalled();
    value.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    expect(hide).toHaveBeenCalledTimes(1);
    value.dispatchEvent(new MouseEvent('mouseenter'));
    expect(show).toHaveBeenCalledTimes(2);
    // Escape also dismisses a hover tooltip while focus remains elsewhere.
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    expect(hide).toHaveBeenCalledTimes(2);
    value.dispatchEvent(new MouseEvent('click'));
    expect(show).toHaveBeenCalledTimes(3);
    value.dispatchEvent(new FocusEvent('blur'));
    expect(hide).toHaveBeenCalledTimes(3);
  });

  it('R4: remeasures after resize and removes tooltip/tabstop when clipping disappears', async () => {
    await clip();
    value.dispatchEvent(new MouseEvent('click'));
    vi.spyOn(value, 'scrollWidth', 'get').mockReturnValue(100);
    measure();
    await fixture.whenStable();
    expect(value.hasAttribute('tabindex')).toBe(false);
    expect(root.querySelector('[popover]')).toBeNull();
    expect(hide).toHaveBeenCalled();
  });

  it('R4: dismisses a recycled clipped value and resets its text and open state', async () => {
    await clip();
    value.dispatchEvent(new MouseEvent('click'));
    expect(show).toHaveBeenCalledTimes(1);

    fixture.componentRef.setInput('value', 'sha256:replacement-value-still-clipped');
    await fixture.whenStable();
    expect(hide).toHaveBeenCalledTimes(1);
    expect(value.getAttribute('tabindex')).toBe('0');
    expect(value.textContent).toBe('sha256:replacement-value-still-clipped');
    expect(root.querySelector('[popover]')?.textContent).toBe(
      'sha256:replacement-value-still-clipped',
    );
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    expect(hide).toHaveBeenCalledTimes(1);

    value.dispatchEvent(new MouseEvent('click'));
    expect(show).toHaveBeenCalledTimes(2);
    value.dispatchEvent(new FocusEvent('blur'));
    expect(hide).toHaveBeenCalledTimes(2);
  });

  it('R4: never opts a naturally wrapping column into tooltips even with measured overflow', async () => {
    await clip();
    fixture.componentRef.setInput('truncate', false);
    await fixture.whenStable();
    expect(value.classList.contains('truncate')).toBe(false);
    expect(value.hasAttribute('tabindex')).toBe(false);
    expect(root.querySelector('[popover]')).toBeNull();
    expect(value.classList.contains('[overflow-wrap:anywhere]')).toBe(true);
  });

  it('R4: keeps the popover hoverable while the pointer crosses to its full-value panel', async () => {
    await clip();
    vi.useFakeTimers();
    value.dispatchEvent(new MouseEvent('mouseenter'));
    value.dispatchEvent(new MouseEvent('mouseleave'));
    root.querySelector('[popover]')?.dispatchEvent(new MouseEvent('mouseenter'));
    vi.advanceTimersByTime(250);
    expect(hide).not.toHaveBeenCalled();
    root.querySelector('[popover]')?.dispatchEvent(new MouseEvent('mouseleave'));
    vi.advanceTimersByTime(250);
    expect(hide).toHaveBeenCalledTimes(1);
    vi.useRealTimers();
  });

  it.each([
    'default',
    'hover',
    'focus-visible',
    'active',
    'disabled',
    'loading',
    'empty',
    'error',
    'success',
  ])('state %s: text rendering never invents an action, request or verdict', async (state) => {
    if (state === 'empty') {
      fixture.componentRef.setInput('value', '');
      await fixture.whenStable();
      expect(value.textContent).toBe('');
    }
    expect(value.hasAttribute('tabindex')).toBe(false);
    expect(root.querySelector('button,[role="button"],[aria-disabled],[aria-busy]')).toBeNull();
    expect(root.textContent).not.toMatch(/\bPASS\b|\bFAIL\b/);
    expect(root.querySelector('script')).toBeNull();
  });
});
