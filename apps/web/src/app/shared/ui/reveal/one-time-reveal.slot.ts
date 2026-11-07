// One-time credentials belong to the mounted step, never a route-singleton store.
import { Injectable, signal, type OnDestroy, type Signal } from '@angular/core';

export type RevealSource =
  'binding-challenge' | 'ingest-token' | 'ingest-token-rotation' | 'invite-token' | 'api-token';

export interface RevealEntry {
  readonly source: RevealSource;
  readonly value: string;
}

@Injectable()
export class RevealSlot implements OnDestroy {
  private readonly entry = signal<RevealEntry | null>(null);
  readonly current: Signal<RevealEntry | null> = this.entry.asReadonly();

  show(entry: RevealEntry): void {
    this.entry.set(entry);
  }

  clear(): void {
    this.entry.set(null);
  }

  ngOnDestroy(): void {
    this.clear();
  }
}
