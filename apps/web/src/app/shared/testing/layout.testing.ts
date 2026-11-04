import { afterAll, beforeAll, vi } from 'vitest';

/**
 * JSDOM omits the layout APIs Brain and Helm touch: their menu/tab observers construct a
 * `ResizeObserver` and Helm scrolls the active option into view. Stubbing both keeps the
 * interaction assertions running against the real components.
 *
 * Stubs the globals only, so a spec that calls `vi.unstubAllGlobals()` between tests can
 * re-stub in its own `beforeEach`; `provideLayoutTestStubs()` is the file-scoped form.
 */
export function stubLayoutApis(): void {
  vi.stubGlobal(
    'ResizeObserver',
    vi.fn(function () {
      return { observe: vi.fn(), unobserve: vi.fn(), disconnect: vi.fn() };
    }),
  );
  Object.defineProperty(Element.prototype, 'scrollIntoView', {
    configurable: true,
    writable: true,
    value: vi.fn(),
  });
}

/** Registers the layout stubs for a whole spec file and restores `scrollIntoView` afterwards. */
export function provideLayoutTestStubs(): void {
  const scrollIntoView = Object.getOwnPropertyDescriptor(Element.prototype, 'scrollIntoView');
  beforeAll(() => stubLayoutApis());
  afterAll(() => {
    vi.unstubAllGlobals();
    if (scrollIntoView) {
      Object.defineProperty(Element.prototype, 'scrollIntoView', scrollIntoView);
    } else {
      Reflect.deleteProperty(Element.prototype, 'scrollIntoView');
    }
  });
}
