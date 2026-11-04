// The store the onboarding shell reads, in the shape its template and its focus passes bind.
// Shared by the shell's specs, so a case drives one concern — focus, routing, the scope a create
// opened — without a progress read behind it.
import { computed, signal, type Signal, type WritableSignal } from '@angular/core';
import { vi, type Mock } from 'vitest';
import type { PolicyActivationStateDto } from '@evidgate/sdk';

import type { StepNumber } from '../data/step-derivation';
import type { OnboardingProgress } from '../data/onboarding-progress.service';

/** The scope a stub store reports as the one-shot create marker. */
export interface StubScope {
  readonly orgId: string;
  readonly projectId: string | null;
}

export interface ShellStoreOptions {
  /** The scope the store reports as just created; nothing by default. */
  readonly createdScope?: () => StubScope | null;
  readonly viewStep?: StepNumber;
  readonly currentStep?: StepNumber | 'enabled';
  readonly done?: Readonly<Record<StepNumber, boolean>>;
  /** The token the store already carries when the shell is built. */
  readonly focusToken?: number;
}

/** Everything the shell binds. Every member is a real signal or a spy. */
export interface ShellStore {
  readonly status: WritableSignal<'loading' | 'ready' | 'error'>;
  readonly failure: WritableSignal<'forbidden' | 'not-found' | 'unavailable' | null>;
  readonly progress: WritableSignal<OnboardingProgress | null>;
  readonly refreshFailure: WritableSignal<'forbidden' | 'not-found' | 'unavailable' | null>;
  readonly viewStep: WritableSignal<StepNumber>;
  readonly currentStep: WritableSignal<StepNumber | 'enabled'>;
  readonly done: WritableSignal<Readonly<Record<StepNumber, boolean>>>;
  readonly project: WritableSignal<{ id: string; name: string } | null>;
  readonly focusToken: WritableSignal<number>;
  readonly canGoNext: Signal<boolean>;
  readonly canGoBack: Signal<boolean>;
  readonly load: Mock<(orgId: string, projectId: string | null) => Promise<void>>;
  readonly recordActivation: Mock<(orgId: string, activation: PolicyActivationStateDto) => void>;
  readonly consumeCreated: Mock<(orgId: string, projectId: string | null) => boolean>;
  readonly selectStep: Mock<(step: StepNumber) => void>;
  readonly next: Mock<() => void>;
  readonly back: Mock<() => void>;
}

const NOTHING_DONE: Readonly<Record<StepNumber, boolean>> = {
  1: false,
  2: false,
  3: false,
  4: false,
  5: false,
};

/**
 * Builds the stub. Every member is a real signal or a spy, so a spec drives it the way the shell
 * does: a render pass answers a write, and a call is asserted where the shell makes one.
 */
export function shellStore(options: ShellStoreOptions = {}): ShellStore {
  const viewStep = signal<StepNumber>(options.viewStep ?? 1);
  const currentStep = signal<StepNumber | 'enabled'>(options.currentStep ?? 2);
  const focusToken = signal(options.focusToken ?? 0);
  const done = signal<Readonly<Record<StepNumber, boolean>>>(options.done ?? NOTHING_DONE);
  /** The real store bumps the token only when a user move actually changes the step. */
  const showStep = (step: StepNumber): void => {
    if (viewStep() === step) return;
    viewStep.set(step);
    focusToken.update((token) => token + 1);
  };
  return {
    status: signal<'loading' | 'ready' | 'error'>('ready'),
    failure: signal<'forbidden' | 'not-found' | 'unavailable' | null>(null),
    progress: signal<OnboardingProgress | null>(null),
    refreshFailure: signal<'forbidden' | 'not-found' | 'unavailable' | null>(null),
    recordActivation: vi.fn<(orgId: string, activation: PolicyActivationStateDto) => void>(),
    viewStep,
    currentStep,
    done,
    project: signal<{ id: string; name: string } | null>({
      id: 'project-1',
      name: 'Storefront web',
    }),
    focusToken,
    canGoNext: computed(() => done()[viewStep()] && viewStep() < 5),
    canGoBack: computed(() => viewStep() > 1),
    load: vi.fn<(orgId: string, projectId: string | null) => Promise<void>>(() =>
      Promise.resolve(),
    ),
    consumeCreated: vi.fn((orgId: string, projectId: string | null) => {
      const created = options.createdScope?.() ?? null;
      return created !== null && created.orgId === orgId && created.projectId === projectId;
    }),
    selectStep: vi.fn((step: StepNumber) => {
      const current = currentStep();
      if (current === 'enabled' || step <= current) showStep(step);
    }),
    next: vi.fn(() => {
      if (done()[viewStep()] && viewStep() < 5) showStep((viewStep() + 1) as StepNumber);
    }),
    back: vi.fn(() => {
      if (viewStep() > 1) showStep((viewStep() - 1) as StepNumber);
    }),
  };
}
