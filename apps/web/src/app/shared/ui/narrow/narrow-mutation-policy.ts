import { BreakpointObserver } from '@angular/cdk/layout';
import { computed, inject, Injectable } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { map } from 'rxjs';

/** Matches the existing md cutoff; viewport policy never grants authorization. */
export const DESKTOP_MUTATION_QUERY = '(min-width: 768px)';

@Injectable({ providedIn: 'root' })
export class NarrowMutationPolicy {
  private readonly breakpoints = inject(BreakpointObserver);
  readonly canMutate = toSignal(
    this.breakpoints.observe(DESKTOP_MUTATION_QUERY).pipe(map((state) => state.matches)),
    { initialValue: this.breakpoints.isMatched(DESKTOP_MUTATION_QUERY) },
  );
  readonly narrow = computed(() => !this.canMutate());

  /** Guard each mutation handler too: responsive DOM can lag the current viewport during resize. */
  allowsMutation(): boolean {
    return this.breakpoints.isMatched(DESKTOP_MUTATION_QUERY);
  }
}
