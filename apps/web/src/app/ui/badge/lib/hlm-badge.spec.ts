import {
  ChangeDetectionStrategy,
  Component,
  provideZonelessChangeDetection,
  signal,
} from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { HlmIcon } from '@evidgate/ui/icon';
import type { Mock } from 'vitest';

import { HlmBadge } from './hlm-badge';

const VI_LABEL = 'Chưa đánh giá đầy đủ bằng chứng phiên bản thử nghiệm quốc tế';

@Component({
  selector: 'app-badge-test',
  imports: [HlmBadge, HlmIcon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <span hlmBadge [variant]="'secondary'" [fullValue]="original()">
      <hlm-icon [name]="'lucideClock'" />
      <span>{{ text() }}</span>
    </span>
    <a hlmBadge href="#destination">{{ text() }}</a>
  `,
})
class BadgeTestComponent {
  readonly text = signal(VI_LABEL);
  readonly original = signal<string | null>(null);
}

describe('HlmBadge clipping disclosure', () => {
  let fixture: ComponentFixture<BadgeTestComponent>;
  let root: HTMLElement;
  let badge: HTMLElement;
  let label: HTMLElement;
  let measurements: (() => void)[];
  let show: Mock;
  let hide: Mock;
  const originalShow = Object.getOwnPropertyDescriptor(HTMLElement.prototype, 'showPopover');
  const originalHide = Object.getOwnPropertyDescriptor(HTMLElement.prototype, 'hidePopover');

  beforeEach(async () => {
    measurements = [];
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
    const observer: ResizeObserver = { observe: vi.fn(), unobserve: vi.fn(), disconnect: vi.fn() };
    vi.stubGlobal(
      'ResizeObserver',
      vi.fn(function (callback: ResizeObserverCallback) {
        measurements.push(() => callback([], observer));
        return observer;
      }),
    );
    await TestBed.configureTestingModule({
      imports: [BadgeTestComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    fixture = TestBed.createComponent(BadgeTestComponent);
    await fixture.whenStable();
    root = fixture.nativeElement as HTMLElement;
    badge = root.querySelector('span[hlmBadge]')!;
    label = badge.querySelector('.spartan-badge__label')!;
    const badgeElement = fixture.debugElement.children[0];
    if (!badgeElement) throw new Error('Expected the primary badge fixture element');
    const badgeComponent = badgeElement.injector.get(HlmBadge);
    vi.spyOn(label, 'clientWidth', 'get').mockReturnValue(90);
    vi.spyOn(label, 'scrollWidth', 'get').mockReturnValue(90);
    expect(badgeComponent.clipped()).toBe(false);
  });

  afterEach(() => {
    fixture.destroy();
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
    vi.useRealTimers();
    if (originalShow) Object.defineProperty(HTMLElement.prototype, 'showPopover', originalShow);
    else Reflect.deleteProperty(HTMLElement.prototype, 'showPopover');
    if (originalHide) Object.defineProperty(HTMLElement.prototype, 'hidePopover', originalHide);
    else Reflect.deleteProperty(HTMLElement.prototype, 'hidePopover');
  });

  async function clip() {
    vi.spyOn(label, 'scrollWidth', 'get').mockReturnValue(420);
    // A content mutation also remeasures the correct projected label (not the sibling link).
    fixture.componentInstance.text.set(VI_LABEL + '!');
    for (const measure of measurements) measure();
    await fixture.whenStable();
  }

  it('keeps the full 60-grapheme Vietnamese source and no unnecessary disclosure', () => {
    expect([
      ...new Intl.Segmenter('vi', { granularity: 'grapheme' }).segment(VI_LABEL),
    ]).toHaveLength(60);
    expect(label.textContent?.trim()).toBe(VI_LABEL);
    expect(badge.getAttribute('tabindex')).toBeNull();
    expect(badge.querySelector('[popover], [title]')).toBeNull();
    expect(badge.querySelector('hlm-icon')?.getAttribute('aria-hidden')).toBe('true');
  });

  it('discloses actual clipping on focus, pointer, tap and keyboard, with Escape', async () => {
    await clip();
    expect(badge.getAttribute('tabindex')).toBe('0');
    expect(badge.querySelector('[popover]')?.textContent).toBe(VI_LABEL + '!');
    expect(badge.querySelector('[popover]')?.getAttribute('aria-hidden')).toBe('true');
    badge.dispatchEvent(new FocusEvent('focusin'));
    expect(show).toHaveBeenCalledTimes(1);
    badge.dispatchEvent(new MouseEvent('click'));
    expect(show).toHaveBeenCalledTimes(1);
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    expect(hide).toHaveBeenCalledTimes(1);
    badge.dispatchEvent(new MouseEvent('mouseenter'));
    expect(show).toHaveBeenCalledTimes(2);
    badge.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    badge.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    expect(show).toHaveBeenCalledTimes(3);
  });

  it('keeps hoverable full text persistent and dismisses it after focus leaves', async () => {
    await clip();
    badge.dispatchEvent(new MouseEvent('mouseenter'));
    vi.useFakeTimers();
    badge.dispatchEvent(new MouseEvent('mouseleave'));
    badge.querySelector('[popover]')?.dispatchEvent(new MouseEvent('mouseenter'));
    vi.advanceTimersByTime(250);
    expect(hide).not.toHaveBeenCalled();
    badge.dispatchEvent(new FocusEvent('focusout'));
    expect(hide).toHaveBeenCalledTimes(1);
  });

  it('removes recovery when a recycled label fits and never renders untrusted markup', async () => {
    await clip();
    badge.dispatchEvent(new MouseEvent('click'));
    vi.spyOn(label, 'scrollWidth', 'get').mockReturnValue(90);
    fixture.componentInstance.text.set('<img src=x onerror=alert(1)>');
    await fixture.whenStable();
    expect(badge.querySelector('[popover], img')).toBeNull();
    expect(badge.getAttribute('tabindex')).toBeNull();
    expect(label.textContent).toContain('<img');
    expect(hide).toHaveBeenCalled();
  });

  it('recovers authored abbreviation and does not intercept navigation clicks', async () => {
    fixture.componentInstance.text.set('sha256:012…cdef');
    fixture.componentInstance.original.set('sha256:0123456789abcdef');
    await fixture.whenStable();
    expect(badge.querySelector('.sr-only')?.textContent).toBe('sha256:0123456789abcdef');
    expect(label.getAttribute('aria-hidden')).toBe('true');
    expect(badge.querySelector('[popover]')?.textContent).toBe('sha256:0123456789abcdef');
    const link = root.querySelector('a')!;
    const click = new MouseEvent('click', { bubbles: true, cancelable: true });
    link.dispatchEvent(click);
    expect(click.defaultPrevented).toBe(false);
    expect(link.querySelector('button, a')).toBeNull();
  });
});
