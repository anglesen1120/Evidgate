import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { TOPBAR_HEIGHT_PROPERTY, trackTopbarHeight } from './topbar-height';

/** A bar element whose measured height the spec can change between frames. */
function topbarOf(height: number): { element: HTMLElement; measure: (next: number) => void } {
  const element = document.createElement('header');
  let measured = height;
  vi.spyOn(element, 'getBoundingClientRect').mockImplementation(
    () => ({ height: measured }) as DOMRect,
  );
  return {
    element,
    measure: (next: number): void => {
      measured = next;
    },
  };
}

/**
 * The observer the helper constructs, with the callback it published, for one test. A real one
 * delivers nothing after `disconnect()`, so this one stops too: a fake that keeps delivering would
 * let a teardown case pass on its own delivery rather than on the helper's behaviour.
 */
class FakeResizeObserver {
  static instances: FakeResizeObserver[] = [];
  readonly observed: Element[] = [];
  disconnected = false;
  constructor(readonly callback: ResizeObserverCallback) {
    FakeResizeObserver.instances.push(this);
  }
  observe(target: Element): void {
    this.observed.push(target);
  }
  unobserve(): void {}
  disconnect(): void {
    this.disconnected = true;
  }
  /** One notification, dropped exactly as the platform drops it once disconnected. */
  deliver(): void {
    if (this.disconnected) return;
    this.callback([], this);
  }
}

function publishedHeight(): string {
  return document.documentElement.style.getPropertyValue(TOPBAR_HEIGHT_PROPERTY);
}

describe('trackTopbarHeight', () => {
  beforeEach(() => {
    FakeResizeObserver.instances = [];
    vi.stubGlobal('ResizeObserver', FakeResizeObserver);
  });

  afterEach(() => {
    document.documentElement.style.removeProperty(TOPBAR_HEIGHT_PROPERTY);
    vi.unstubAllGlobals();
  });

  it('publishes the bar height rounded up, and follows it when the bar grows', () => {
    const bar = topbarOf(64.4);
    trackTopbarHeight(document, bar.element);

    expect(publishedHeight()).toBe('65px');
    expect(FakeResizeObserver.instances).toHaveLength(1);
    expect(FakeResizeObserver.instances[0]?.observed).toEqual([bar.element]);

    // A second row (the wrapped topbar) is what the observer exists for.
    bar.measure(128);
    FakeResizeObserver.instances[0]?.deliver();
    expect(publishedHeight()).toBe('128px');
  });

  it('leaves the stylesheet fallback when the bar has no layout yet', () => {
    const bar = topbarOf(0);
    trackTopbarHeight(document, bar.element);
    expect(publishedHeight()).toBe('');

    // The first real measurement still lands, so a shell rendered while hidden recovers.
    bar.measure(65);
    FakeResizeObserver.instances[0]?.deliver();
    expect(publishedHeight()).toBe('65px');
  });

  it('publishes once without a ResizeObserver and still removes the property on teardown', () => {
    vi.stubGlobal('ResizeObserver', undefined);
    const bar = topbarOf(65);
    const stop = trackTopbarHeight(document, bar.element);
    expect(publishedHeight()).toBe('65px');

    stop();
    expect(publishedHeight()).toBe('');
  });

  it('disconnects its observer and removes the property on teardown', () => {
    const bar = topbarOf(65);
    const stop = trackTopbarHeight(document, bar.element);
    const observer = FakeResizeObserver.instances[0]!;

    stop();
    expect(observer.disconnected).toBe(true);
    expect(publishedHeight()).toBe('');

    // A resize after teardown never reaches the helper again, so the property stays removed.
    bar.measure(128);
    observer.deliver();
    expect(publishedHeight()).toBe('');
  });
});
