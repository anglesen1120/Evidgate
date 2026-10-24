// State sheet: docs/design/web/state-sheet.md#429-retry-after-indicator
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { RetryAfterComponent } from './retry-after.component';

describe('RetryAfterComponent', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [RetryAfterComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
  });

  afterEach(() => {
    TestBed.resetTestingModule();
    vi.useRealTimers();
  });

  function render(seconds: number | null = 30, eligible = false) {
    const fixture = TestBed.createComponent(RetryAfterComponent);
    fixture.componentRef.setInput('retryAfterSeconds', seconds);
    fixture.componentRef.setInput('retryEligible', eligible);
    fixture.componentRef.setInput('code', 'rate_limited');
    fixture.componentRef.setInput('requestId', 'request-429');
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    const button = root.querySelector('button');
    if (!button) throw new Error('Expected native retry button');
    return { fixture, root, button };
  }

  it('shows the supplied interval and original error context without automatic retry', () => {
    const { fixture, root, button } = render();
    const retry = vi.fn();
    fixture.componentInstance.retry.subscribe(retry);
    expect(root.querySelector('h2')?.textContent).toBe('429 · Rate limited');
    expect(root.textContent).toContain('Retry after: 30 s');
    expect(root.textContent).toContain('rate_limited');
    expect(root.textContent).toContain('request-429');
    expect(button.type).toBe('button');
    expect(button.disabled).toBe(true);
    expect(button.textContent).toContain('Retry available after the wait');
    button.click();
    expect(retry).not.toHaveBeenCalled();
  });

  it('renders the supplied unavailable kind without mislabeling a 503 as rate limited', () => {
    const { fixture, root, button } = render(30, true);
    const retry = vi.fn();
    fixture.componentInstance.retry.subscribe(retry);
    fixture.componentRef.setInput('kind', 'unavailable');
    fixture.componentRef.setInput('code', 'unavailable');
    fixture.detectChanges();
    expect(root.querySelector('h2')?.textContent).toBe('503 · Service unavailable');
    expect(root.textContent).not.toContain('429 · Rate limited');
    expect(root.textContent).toContain('Retry after: 30 s');
    expect(button.disabled).toBe(false);
    expect(retry).not.toHaveBeenCalled();
    fixture.componentRef.setInput('kind', 'rate_limited');
    fixture.detectChanges();
    expect(root.querySelector('h2')?.textContent).toBe('429 · Rate limited');
  });

  it('stays disabled until supplied eligibility changes, then emits exactly once per activation', () => {
    const { fixture, root, button } = render();
    const retry = vi.fn();
    fixture.componentInstance.retry.subscribe(retry);
    button.click();
    expect(retry).not.toHaveBeenCalled();
    fixture.componentRef.setInput('retryEligible', true);
    fixture.detectChanges();
    expect(button.disabled).toBe(false);
    expect(button.textContent?.trim()).toBe('Retry');
    expect(root.querySelector('[role="status"]')?.textContent).toContain('You can retry now.');
    button.click();
    expect(retry).toHaveBeenCalledTimes(1);
    fixture.detectChanges();
    expect(retry).toHaveBeenCalledTimes(1);
  });

  it('shows busy text and natively disables retry without changing the interval announcement', () => {
    const { fixture, root, button } = render(30, true);
    const retry = vi.fn();
    fixture.componentInstance.retry.subscribe(retry);
    const status = root.querySelector('[role="status"]');
    if (!status) throw new Error('Expected interval status');
    const observer = new MutationObserver(() => undefined);
    observer.observe(status, {
      subtree: true,
      childList: true,
      characterData: true,
      attributes: true,
    });
    const before = status.textContent;
    fixture.componentRef.setInput('busy', true);
    fixture.componentRef.setInput('code', 'quota_exceeded');
    fixture.detectChanges();
    expect(button.disabled).toBe(true);
    expect(button.textContent).toContain('Retrying…');
    button.click();
    expect(retry).not.toHaveBeenCalled();
    expect(status.textContent).toBe(before);
    expect(observer.takeRecords()).toEqual([]);
    observer.disconnect();
    fixture.componentRef.setInput('busy', false);
    fixture.detectChanges();
    expect(button.disabled).toBe(false);
  });

  it.each([null, -1, Number.NaN, Number.POSITIVE_INFINITY])(
    'uses missing-interval copy without inventing a duration for %s',
    (seconds) => {
      const { root, button } = render(seconds, true);
      expect(root.textContent).toContain('The server did not say how long to wait');
      expect(root.textContent).not.toContain('0 seconds');
      expect(root.textContent).not.toContain('Retry after:');
      expect(button.disabled).toBe(false);
    },
  );

  it('uses one polite atomic status updated only for supplied interval or eligibility changes', () => {
    const { fixture, root } = render();
    const status = root.querySelector('[role="status"]');
    expect(root.querySelectorAll('[aria-live]')).toHaveLength(1);
    expect(status?.getAttribute('aria-live')).toBe('polite');
    expect(status?.getAttribute('aria-atomic')).toBe('true');
    fixture.componentRef.setInput('retryAfterSeconds', 90);
    fixture.detectChanges();
    expect(status?.textContent).toContain('Retry after: 90 s');
    fixture.componentRef.setInput('retryEligible', true);
    fixture.detectChanges();
    expect(status?.textContent).toContain('You can retry now.');
    expect(status?.textContent).not.toContain('Retry available after the wait');
  });

  it('does not create timers, count down the supplied interval or retry automatically', () => {
    vi.useFakeTimers();
    const { fixture, root } = render(30, true);
    const retry = vi.fn();
    fixture.componentInstance.retry.subscribe(retry);
    expect(vi.getTimerCount()).toBe(0);
    vi.advanceTimersByTime(60_000);
    fixture.detectChanges();
    expect(vi.getTimerCount()).toBe(0);
    expect(root.textContent).toContain('Retry after: 30 s');
    expect(retry).not.toHaveBeenCalled();
  });

  it('renders error codes and request ids as text without creating markup', () => {
    const { fixture, root } = render(null, true);
    fixture.componentRef.setInput('code', '<img src=x>');
    fixture.componentRef.setInput('requestId', '<script>request</script>');
    fixture.detectChanges();
    expect(root.textContent).toContain('<img src=x>');
    expect(root.textContent).toContain('<script>request</script>');
    expect(root.querySelector('img, script')).toBeNull();
    fixture.componentRef.setInput('code', null);
    fixture.componentRef.setInput('requestId', null);
    fixture.detectChanges();
    expect(root.querySelectorAll('dt')).toHaveLength(0);
  });
});
