import { BreakpointObserver, type BreakpointState } from '@angular/cdk/layout';
import type { Provider } from '@angular/core';
import { BehaviorSubject } from 'rxjs';

const DESKTOP_QUERY = '(min-width: 768px)';

/** A local observer, so responsive onboarding tests never change another suite's viewport. */
export class OnboardingViewport {
  private width = 1440;
  private readonly states = new BehaviorSubject<BreakpointState>({
    matches: true,
    breakpoints: { [DESKTOP_QUERY]: true },
  });

  readonly provider: Provider = {
    provide: BreakpointObserver,
    useValue: {
      observe: () => this.states.asObservable(),
      isMatched: (query: string) => query === DESKTOP_QUERY && this.width >= 768,
    },
  };

  setWidth(width: number): void {
    this.width = width;
    const matches = width >= 768;
    this.states.next({ matches, breakpoints: { [DESKTOP_QUERY]: matches } });
  }
}

export function provideDesktopOnboardingViewport(): Provider {
  return new OnboardingViewport().provider;
}
