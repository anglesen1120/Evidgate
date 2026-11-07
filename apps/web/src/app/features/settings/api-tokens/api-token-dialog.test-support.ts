import { expect } from 'vitest';
import { ApiRequestError, type ApiError } from '../../../core/http/api-error';

interface TokenHarness {
  readonly store: { openCreate: () => void };
  readonly settle: () => Promise<void>;
}

export function control<T extends HTMLElement>(selector: string): T {
  const element = document.querySelector<T>(selector);
  if (!element) throw new Error(`Missing ${selector}`);
  return element;
}

export async function openCreate(h: TokenHarness): Promise<void> {
  h.store.openCreate();
  await h.settle();
  expect(control('[data-api-create-dialog]')).toBeTruthy();
}

export async function fillCreate(h: TokenHarness): Promise<void> {
  for (const [selector, value] of [
    ['#api-create-name', 'Reader'],
    ['#api-create-expiry', '2027-01-01T00:00'],
  ]) {
    const input = control<HTMLInputElement>(selector!);
    input.value = value!;
    input.dispatchEvent(new Event('input', { bubbles: true }));
  }
  control<HTMLButtonElement>('button[id="api-permission-run:read"]').click();
  await h.settle();
}

export function submitCreate(): void {
  control<HTMLFormElement>('[data-api-create-dialog] form').dispatchEvent(
    new Event('submit', { bubbles: true, cancelable: true }),
  );
}

export function writeFailure(patch: Partial<ApiError> = {}): ApiRequestError {
  return new ApiRequestError({
    kind: 'validation',
    status: 422,
    code: 'validation_failed',
    message: 'Server validation refused this request.',
    requestId: 'write-request',
    retryable: false,
    retryAfterSeconds: null,
    details: [],
    ...patch,
  });
}
