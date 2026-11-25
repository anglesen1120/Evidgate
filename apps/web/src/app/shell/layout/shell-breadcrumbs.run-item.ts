import { Injectable, signal } from '@angular/core';

/** The run page publishes only an admitted run. A denied or unknown address never labels the shell. */
@Injectable({ providedIn: 'root' })
export class RunBreadcrumbItem {
  readonly id = signal<string | null>(null);
}
