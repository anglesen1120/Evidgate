import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { CiBindingView, IngestTokenView } from '@evidgate/sdk';
import { vi } from 'vitest';
import type { ApiError } from '../../../core/http/api-error';
import fixtureData from '../../../../../../../fixtures/web-api/ingest-tokens-list-onboarding-token/input.json';
import bindingData from '../../../../../../../fixtures/web-api/ci-bindings-list-settings/input.json';
import type { CiBindingsPhase, RotationResult, RotationState } from './secrets.store';
import { RevealSlot } from '../../../shared/ui/reveal/one-time-reveal.slot';
import { SecretsPage } from './secrets.page';

export const base = fixtureData.body.items[0] as IngestTokenView;
export const failure: ApiError = {
  kind: 'conflict',
  status: 409,
  code: 'invalid_transition',
  message: 'Already rotated. <strong>Server text stays verbatim.</strong>',
  requestId: 'request-rotate',
  retryable: false,
  retryAfterSeconds: null,
  details: [],
};

export class FakeSecretsStore {
  readonly rows = signal<readonly IngestTokenView[]>([base]);
  readonly phase = signal<'loading' | 'ready' | 'error'>('ready');
  readonly loadError = signal<ApiError | null>(null);
  readonly hasMore = signal(false);
  readonly projectId = signal<string | null>(null);
  readonly rotateState = signal<RotationState>({ phase: 'idle', tokenId: null, error: null });
  readonly rotating = signal(false);
  readonly ciBindings = signal<readonly CiBindingView[]>(bindingData.body.items as CiBindingView[]);
  readonly ciBindingsPhase = signal<CiBindingsPhase>('ready');
  readonly ciBindingsError = signal<ApiError | null>(null);
  readonly hasMoreCiBindings = signal(false);
  readonly loadCiBindings = vi.fn<(id: string) => Promise<void>>(() => Promise.resolve());
  readonly loadMoreCiBindings = vi.fn(() => Promise.resolve());
  readonly resetCiBindings = vi.fn();
  readonly load = vi.fn((id: string) => {
    this.projectId.set(id);
    return Promise.resolve();
  });
  readonly loadMore = vi.fn(() => Promise.resolve());
  readonly rotate = vi.fn<(id: string) => Promise<RotationResult | null>>(() =>
    Promise.resolve({
      successorId: 'next',
      overlapEndsAt: '2026-10-26T00:00:00Z',
      value: 'new-full-credential-once',
    }),
  );
  resetRotation(): void {
    this.rotateState.set({ phase: 'idle', tokenId: null, error: null });
  }
}

export async function renderSecretsPage() {
  const fixture = TestBed.createComponent(SecretsPage);
  fixture.detectChanges();
  await fixture.whenStable();
  return {
    fixture,
    root: fixture.nativeElement as HTMLElement,
    slot: fixture.debugElement.injector.get(RevealSlot),
  };
}

export function rotateButton(root: HTMLElement): HTMLButtonElement {
  const button = root.querySelector<HTMLButtonElement>('[data-rotate]');
  if (!button) throw new Error('Expected Rotate button');
  return button;
}
