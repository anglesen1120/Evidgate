// The harness every fixture-backend spec shares: the production interceptor, backend and SDK
// adapter path, the catalog readers, and the toggles a case drives. Each spec builds its own
// TestBed, so the spies and the request log stay per test.
import type { HttpEvent, HttpRequest } from '@angular/common/http';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { isErrorEnvelope, type ErrorEnvelope } from '@evidgate/sdk';
import { tap } from 'rxjs';
import { vi } from 'vitest';

import { provideApi } from '../http/api-client';
import { ApiGateway } from '../http/api-gateway';
import { API_BASE_PATH } from '../http/api-origin';
import { FIXTURE_BACKEND, provideFixtureBackend } from './fixture-backend';
import { FIXTURE_CATALOG, type FixtureDocument } from './fixture-catalog';
import { FixtureToggles } from './fixture-toggles';

export const API_BASE_URL = `${location.origin}${API_BASE_PATH}`;

/** One catalog document by its corpus directory name; a missing one fails the spec loudly. */
export function documentOf(name: string): FixtureDocument {
  const entry = FIXTURE_CATALOG.find((candidate) => candidate.name === name);
  if (entry === undefined) {
    throw new Error(`fixtures/web-api has no fixture named "${name}"`);
  }
  return entry.document;
}

/** The 00 §9.2 envelope a refusal fixture carries, validated before its fields are asserted on. */
export function envelopeOf(document: FixtureDocument): ErrorEnvelope {
  if (!isErrorEnvelope(document.body)) {
    throw new Error(
      `fixture ${document.operation_id}/${document.variant} carries no 00 §9.2 envelope`,
    );
  }
  return document.body;
}

/** The rejection of `promise`, or a failure when it resolved — the shape every refusal case reads. */
export async function rejectionOf<T>(promise: Promise<T>): Promise<unknown> {
  try {
    await promise;
  } catch (error: unknown) {
    return error;
  }
  throw new Error('expected the call to reject');
}

/**
 * The gateway, toggles, request log and event log a spec drives, with the backend's own request
 * handler spied on: the fixture response header and the request's X-Request-Id are not visible
 * through the SDK's resolved value, and reaching for them any other way would duplicate the chain
 * under test.
 */
export function setupFixtureBackend(): {
  readonly gateway: ApiGateway;
  readonly toggles: FixtureToggles;
  readonly handled: HttpRequest<unknown>[];
  readonly events: HttpEvent<unknown>[];
} {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      provideApi(API_BASE_URL),
      provideFixtureBackend(),
    ],
  });
  const gateway = TestBed.inject(ApiGateway);
  const toggles = TestBed.inject(FixtureToggles);
  const handled: HttpRequest<unknown>[] = [];
  const events: HttpEvent<unknown>[] = [];

  const backend = TestBed.inject(FIXTURE_BACKEND);
  const handle = backend.handle.bind(backend);
  vi.spyOn(backend, 'handle').mockImplementation((request: HttpRequest<unknown>) => {
    handled.push(request);
    return handle(request).pipe(tap((event) => events.push(event)));
  });

  return { gateway, toggles, handled, events };
}
