import { afterAll, beforeAll, vi } from 'vitest';

/** JSDOM omits layout APIs used by Brain; interaction assertions still use the real select. */
export function provideSelectTestLayout(): void {
  const scrollIntoView = Object.getOwnPropertyDescriptor(HTMLElement.prototype, 'scrollIntoView');
  beforeAll(() => {
    vi.stubGlobal(
      'ResizeObserver',
      vi.fn(function () {
        return { observe: vi.fn(), unobserve: vi.fn(), disconnect: vi.fn() };
      }),
    );
    Object.defineProperty(HTMLElement.prototype, 'scrollIntoView', {
      configurable: true,
      writable: true,
      value: vi.fn(),
    });
  });
  afterAll(() => {
    vi.unstubAllGlobals();
    if (scrollIntoView) {
      Object.defineProperty(HTMLElement.prototype, 'scrollIntoView', scrollIntoView);
    } else {
      Reflect.deleteProperty(HTMLElement.prototype, 'scrollIntoView');
    }
  });
}
