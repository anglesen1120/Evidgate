import { provideZonelessChangeDetection, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import type {
  PolicyActivationDto,
  PolicyActivationStateDto,
  PolicyRevisionDto,
} from '@evidgate/sdk';
import { beforeEach, vi } from 'vitest';

import { OnboardingDataService } from '../../../core/data/onboarding-data.service';
import { ApiRequestError, type ApiError } from '../../../core/http/api-error';
import { SessionStore } from '../../../core/session/session.store';
import { DEFAULT_ADVISORY_POLICY } from './default-policy';
import { EnableCollectionPage } from './enable-collection.page';

const REVISION_HASH = 'sha256:9f2c1d7b4a6e8f0a1b2c3d4e5f60718293a4b5c6d7e8f9012345678a9b';

export const REVISION: PolicyRevisionDto = {
  id: '018f0000-0000-7000-8000-00000000a004',
  project_id: 'project-1',
  revision_no: 1,
  hash: REVISION_HASH,
  state: 'draft',
  body: DEFAULT_ADVISORY_POLICY,
  created_by: 'user-1',
  created_at: '2026-10-27T00:00:00Z',
  version: 1,
};

export const ACTIVATION: PolicyActivationDto = {
  project_id: 'project-1',
  policy_revision_id: REVISION.id,
  hash: REVISION_HASH,
  mode: 'advisory',
  activated_by: 'user-1',
  activated_at: '2026-10-27T01:00:00Z',
  previous_policy_revision_id: null,
  changed: true,
};

export const ACTIVATION_UNCHANGED: PolicyActivationDto = {
  ...ACTIVATION,
  previous_policy_revision_id: null,
  changed: false,
};

export const ACTIVATION_STATE: PolicyActivationStateDto = {
  project_id: 'project-1',
  policy_revision_id: REVISION.id,
  hash: REVISION_HASH,
  mode: 'advisory',
  activated_by: 'user-1',
  activated_at: '2026-10-27T01:00:00Z',
};

export const ACTIVATION_STATE_ENFORCING: PolicyActivationStateDto = {
  ...ACTIVATION_STATE,
  mode: 'enforcing',
};

export type ReadActivation = (projectId: string) => Promise<PolicyActivationStateDto | null>;

// The web project's ES2022 lib does not expose Promise.withResolvers.
export function deferredResult<T>(): {
  readonly promise: Promise<T>;
  readonly resolve: (value: T) => void;
} {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((done) => {
    resolve = done;
  });
  return { promise, resolve };
}

const RETRYABLE_STATUSES: readonly number[] = [429, 502, 503, 504];

export function failure(
  kind: ApiError['kind'],
  status: number,
  options: {
    retryable?: boolean;
    retryAfterSeconds?: number | null;
    details?: ApiError['details'];
  } = {},
): ApiRequestError {
  return new ApiRequestError({
    kind,
    status,
    code: kind,
    message: `Exact server refusal for ${kind}.`,
    requestId: 'request-1',
    retryable: options.retryable ?? RETRYABLE_STATUSES.includes(status),
    details: options.details ?? [],
    retryAfterSeconds: options.retryAfterSeconds ?? null,
  });
}

// Drain the mocked command's macrotask before waiting for the framework's render work.
export async function drain(target: ComponentFixture<unknown>): Promise<void> {
  await new Promise<void>((resolve) => {
    setTimeout(resolve, 0);
  });
  await target.whenStable();
}

export function confirmButton(root: HTMLElement): HTMLButtonElement | null {
  return root.querySelector<HTMLButtonElement>('[data-testid="enable-collection-confirm"]');
}

export function retryButton(root: HTMLElement): HTMLButtonElement | null {
  return root.querySelector<HTMLButtonElement>('app-error-pattern button');
}

export function readRetryAction(root: HTMLElement): HTMLButtonElement | null {
  return root.querySelector<HTMLButtonElement>('[data-testid="enable-collection-retry-read"]');
}

export function readAgainButton(root: HTMLElement): HTMLButtonElement | null {
  return root.querySelector<HTMLButtonElement>('[data-testid="enable-collection-read-again"]');
}

export function text(root: HTMLElement, selector: string): string {
  return root.querySelector(selector)?.textContent?.trim() ?? '';
}

export function submit(root: HTMLElement): void {
  root
    .querySelector('form')
    ?.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
}

export function createActivationReadContext() {
  const permitted = signal(true);
  const has = vi.fn((code: string) => permitted() && code === 'policy:write');
  const createPolicyRevision = vi.fn<OnboardingDataService['createPolicyRevision']>();
  const activatePolicy = vi.fn<OnboardingDataService['activatePolicy']>();
  const readPolicyActivation = vi.fn<ReadActivation>();

  beforeEach(() => {
    permitted.set(true);
    createPolicyRevision.mockReset().mockResolvedValue(REVISION);
    activatePolicy.mockReset().mockResolvedValue(ACTIVATION);
    readPolicyActivation.mockReset();
  });

  async function createPage(): Promise<ComponentFixture<EnableCollectionPage>> {
    await TestBed.configureTestingModule({
      imports: [EnableCollectionPage],
      providers: [
        provideZonelessChangeDetection(),
        {
          provide: OnboardingDataService,
          useValue: { createPolicyRevision, activatePolicy, readPolicyActivation },
        },
        { provide: SessionStore, useValue: { has } },
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(EnableCollectionPage);
    fixture.componentRef.setInput('orgId', 'org-1');
    fixture.componentRef.setInput('projectId', 'project-1');
    await drain(fixture);
    return fixture;
  }

  return { permitted, createPolicyRevision, activatePolicy, readPolicyActivation, createPage };
}
