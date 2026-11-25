import { BreakpointObserver } from '@angular/cdk/layout';
import { computed, inject, Injectable } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { map } from 'rxjs';

/** Matches the existing md cutoff; viewport policy never grants authorization. */
export const DESKTOP_MUTATION_QUERY = '(min-width: 768px)';

/**
 * The only mutations that stay available below the desktop cutoff: lightweight on-call containment.
 * Adding an entry is a reviewed policy change; nothing else may bypass the desktop-only rule.
 */
export const EMERGENCY_ACTIONS = ['healing-kill-switch', 'revoke-api-token', 'cancel-run'] as const;
export type EmergencyAction = (typeof EMERGENCY_ACTIONS)[number];

const EMERGENCY_SET: ReadonlySet<string> = new Set(EMERGENCY_ACTIONS);

@Injectable({ providedIn: 'root' })
export class NarrowMutationPolicy {
  private readonly breakpoints = inject(BreakpointObserver);
  readonly canMutate = toSignal(
    this.breakpoints.observe(DESKTOP_MUTATION_QUERY).pipe(map((state) => state.matches)),
    { initialValue: this.breakpoints.isMatched(DESKTOP_MUTATION_QUERY) },
  );
  readonly narrow = computed(() => !this.canMutate());

  /**
   * Guard each mutation handler too: responsive DOM can lag the current viewport during resize.
   * Pass an allowlisted action id only from that action's own handler; the policy never grants
   * authorization, so permission and server checks stay in the caller.
   */
  allowsMutation(action?: EmergencyAction): boolean {
    return (
      (action !== undefined && EMERGENCY_SET.has(action)) ||
      this.breakpoints.isMatched(DESKTOP_MUTATION_QUERY)
    );
  }

  /** Reactive twin of allowsMutation(action) for templates and computed state. */
  canMutateFor(action?: EmergencyAction): boolean {
    return (action !== undefined && EMERGENCY_SET.has(action)) || this.canMutate();
  }
}
