import { vi } from 'vitest';

// jsdom lacks Popover methods but applies the closed-popover UA display rule.
// CDK detects these methods and uses native popover hosts, so the test mock must
// expose open hosts to focus checks, not just accept show/hide calls.
const closedDisplay = new WeakMap<HTMLElement, string>();
const methods = {
  showPopover(this: HTMLElement) {
    if (!closedDisplay.has(this)) closedDisplay.set(this, this.style.display);
    this.style.display = 'block';
  },
  hidePopover(this: HTMLElement) {
    const display = closedDisplay.get(this);
    if (display === undefined) return;
    this.style.display = display;
    closedDisplay.delete(this);
  },
};

for (const method of ['showPopover', 'hidePopover'] as const) {
  if (typeof HTMLElement.prototype[method] === 'function') continue;
  Object.defineProperty(HTMLElement.prototype, method, {
    configurable: true,
    writable: true,
    value: vi.fn(function (this: HTMLElement) {
      methods[method].call(this);
    }),
  });
}
