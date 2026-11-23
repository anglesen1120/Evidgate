import type { OutputEmitterRef } from '@angular/core';

export function retryWithDashboardFocus(
  heading: HTMLElement,
  retry: OutputEmitterRef<void> | (() => void),
): void {
  heading.focus();
  if (typeof retry === 'function') {
    retry();
  } else {
    retry.emit();
  }
}
