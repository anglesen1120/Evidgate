import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { BrowserPlatformLocation, PlatformLocation } from '@angular/common';
import {
  HttpTestingController,
  provideHttpClientTesting,
  type TestRequest,
} from '@angular/common/http/testing';
import { computed, provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import type { RetentionPolicy } from '@evidgate/sdk';
import { expect, vi } from 'vitest';

import { provideApi } from '../../../core/http/api-client';
import { SessionStore } from '../../../core/session/session.store';
import { provideLayoutTestStubs, stubLayoutApis } from '../../../shared/testing/layout.testing';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow';
import { RetentionFormComponent } from './retention-form.component';
import { RetentionStore } from './retention.store';

provideLayoutTestStubs();
export const STORED: RetentionPolicy = { artifact_days: 14, metadata_days: 90 };
export const RETENTION_URL = `${location.origin}/v1/projects/p-1/retention`;
export const PROJECTS = [
  { id: 'p-1', name: 'First project', org_id: 'o-1' },
  { id: 'p-2', name: 'Second project', org_id: 'o-1' },
];

export class RetentionHarness extends ComponentHarness {
  static hostSelector = 'app-retention-form';
  readonly artifact = this.locatorFor('#retention-artifact');
  readonly metadata = this.locatorFor('#retention-metadata');
  readonly submit = this.locatorFor('[data-retention-save]');
  readonly project = this.locatorFor('#retention-project');
}

export function refusal(status: number, message = 'Server refused retention') {
  return {
    error: {
      code:
        status === 422
          ? 'retention_out_of_range'
          : status === 403
            ? 'forbidden'
            : 'project_deleting',
      message,
      request_id: 'request-retention',
      retryable: false,
      details:
        status === 422
          ? [
              { path: 'artifact_days', reason: 'above_max_artifact_days' },
              { path: 'retention_limits.max_artifact_days', reason: '120' },
            ]
          : [],
    },
  };
}

export function flush(request: TestRequest, body: object = STORED, status = 200): void {
  request.flush(body, {
    status,
    statusText: status === 200 ? 'OK' : 'Refused',
    headers: { 'Content-Type': 'application/json' },
  });
}

export function mountRetention(codes = ['project:read', 'project:write']) {
  stubLayoutApis();
  const permissions = signal(new Set(codes));
  const projects = signal(PROJECTS);
  const org = signal<{ id: string } | null>({ id: 'o-1' });
  const desktop = signal(true);
  TestBed.configureTestingModule({
    imports: [RetentionFormComponent],
    providers: [
      provideZonelessChangeDetection(),
      provideRouter([]),
      provideApi(`${location.origin}/v1`),
      provideHttpClientTesting(),
      { provide: PlatformLocation, useClass: BrowserPlatformLocation },
      {
        provide: NarrowMutationPolicy,
        useValue: {
          canMutate: desktop,
          narrow: computed(() => !desktop()),
          allowsMutation: () => desktop(),
        },
      },
      {
        provide: SessionStore,
        useValue: {
          has: (code: string) => permissions().has(code),
          projects,
          currentOrg: org,
          currentProject: signal(null),
        },
      },
    ],
  });
  const fixture = TestBed.createComponent(RetentionFormComponent);
  const store = fixture.debugElement.injector.get(RetentionStore);
  const http = TestBed.inject(HttpTestingController);
  fixture.detectChanges();
  const take = async (method: string, project = 'p-1'): Promise<TestRequest> => {
    let found: TestRequest | undefined;
    await vi.waitFor(() => {
      TestBed.tick();
      const matches = http.match(
        (request) =>
          request.method === method && request.url.endsWith(`/projects/${project}/retention`),
      );
      found = matches[0];
      expect(found).toBeDefined();
    });
    if (!found) throw new Error('No retention request');
    return found;
  };
  // A flushed answer arrives on a macrotask that a zoneless `whenStable()` never waits for, so the
  // fixture settles on the observable condition instead of a fixed delay: no read and no write of
  // this store is still in flight. `vi.waitFor` fails with a deadline rather than hanging.
  const settle = async (): Promise<void> => {
    await vi.waitFor(() => {
      TestBed.tick();
      expect(store.saving() || store.loading()).toBe(false);
    });
    TestBed.tick();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  };
  const load = async (body = STORED): Promise<void> => {
    flush(await take('GET'), body);
    await settle();
  };
  const harness = async () =>
    TestbedHarnessEnvironment.harnessForFixture(fixture, RetentionHarness);
  // CDK's testbed `setInputValue` only assigns `value` and stabilizes; it fires no event, so a
  // reactive control would keep the loaded window. Type the way a person does: assign, then send
  // the event the number value accessor listens for.
  const edit = async (artifact: string, metadata: string): Promise<void> => {
    const edits: readonly (readonly [string, string])[] = [
      ['#retention-artifact', artifact],
      ['#retention-metadata', metadata],
    ];
    for (const [selector, value] of edits) {
      const input = (fixture.nativeElement as HTMLElement).querySelector<HTMLInputElement>(
        selector,
      );
      if (!input) throw new Error(`Missing retention input ${selector}`);
      input.value = value;
      input.dispatchEvent(new Event('input', { bubbles: true }));
    }
    await settle();
  };
  return {
    fixture,
    store,
    http,
    take,
    settle,
    load,
    harness,
    edit,
    permissions,
    projects,
    org,
    desktop,
    root: fixture.nativeElement as HTMLElement,
  };
}

export function dialogButton(selector: string): HTMLButtonElement {
  const button = document.querySelector<HTMLButtonElement>(selector);
  if (!button) throw new Error(`Missing dialog button ${selector}`);
  return button;
}
