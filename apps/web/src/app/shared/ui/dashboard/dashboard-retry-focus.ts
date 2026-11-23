import type { OutputEmitterRef } from '@angular/core';

export function retryWithDashboardFocus(heading: HTMLElement, retry: OutputEmitterRef<void>): void {
  heading.focus();
  retry.emit();
}
