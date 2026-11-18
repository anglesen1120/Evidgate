import { ComponentHarness, TestKey } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { provideZonelessChangeDetection, signal, type WritableSignal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { vi, type Mock, type MockInstance } from 'vitest';
import { ApiRequestError, type ApiError } from '../../../../core/http/api-error';
import { SessionStore } from '../../../../core/session/session.store';
export { provideLayoutTestStubs } from '../../../../shared/testing/layout.testing';
import { NarrowMutationPolicy } from '../../../../shared/ui/narrow';
import { RunDataService } from '../../data/run-data.service';
import type { CreatedRun, CreateRunInput } from '../../data/run-actions.types';
import { CreateRunDialogComponent } from './create-run-dialog.component';

export const PROJECT = '018f0000-0000-7000-8000-000000000001';
export const MANIFEST = '018f0000-0000-7000-8000-000000000011';
export const CREATED: CreatedRun = {
  run_id: '018f0000-0000-7000-8000-000000000021',
  state: 'queued',
  unverified_ref: false,
};
export function refusal(patch: Partial<ApiError> = {}): ApiRequestError {
  return new ApiRequestError({
    kind: 'idempotency_conflict',
    status: 409,
    code: 'idempotency_conflict',
    message: 'The key was used for another body.',
    requestId: 'req-create-1',
    retryable: false,
    retryAfterSeconds: null,
    details: [],
    ...patch,
  });
}

export class CreateRunDialogHarness extends ComponentHarness {
  static hostSelector = '[data-run-create-dialog]';
  async set(field: string, value: string): Promise<void> {
    if (['refName', 'prNumber', 'headSha', 'baseSha'].includes(field)) {
      const details = await this.locatorFor('[data-run-create-ref]')();
      if (!(await details.getProperty<boolean>('open')))
        await (await this.locatorFor('[data-run-create-ref] summary')()).click();
    }
    const input = await this.locatorFor(`#run-create-${field}`)();
    await input.setInputValue(value);
    await input.dispatchEvent('input');
  }
  async value(field: string): Promise<string> {
    return (await this.locatorFor(`#run-create-${field}`)()).getProperty<string>('value');
  }
  async invalid(field: string): Promise<string | null> {
    return (await this.locatorFor(`#run-create-${field}`)()).getAttribute('aria-invalid');
  }
  async refExpanded(): Promise<boolean> {
    return (await this.locatorFor('[data-run-create-ref]')()).getProperty<boolean>('open');
  }
  async submit(): Promise<void> {
    await (await this.locatorFor('[data-run-create-submit]')()).click();
  }
  async dismiss(): Promise<void> {
    await (await this.locatorFor('[data-run-create-dismiss]')()).click();
  }
  async escape(): Promise<void> {
    await (await this.host()).sendKeys(TestKey.ESCAPE);
  }
  async choose(field: 'purpose' | 'kind', label: string): Promise<void> {
    await (await this.locatorFor(`#run-create-${field}`)()).click();
    const options = await this.documentRootLocatorFactory().locatorForAll('[role="option"]')();
    for (const option of options) {
      if ((await option.text()).trim() === label) {
        await option.click();
        return;
      }
    }
    throw new Error(`Missing ${field} option: ${label}`);
  }
  async requirement(field: string): Promise<string> {
    return (await this.locatorFor(`label[for="run-create-${field}"]`)()).text();
  }
  async required(field: string): Promise<boolean> {
    return (await this.locatorFor(`#run-create-${field}`)()).getProperty<boolean>('required');
  }
  async replayDisabled(): Promise<string | null> {
    await (await this.locatorFor('#run-create-purpose')()).click();
    const options = await this.documentRootLocatorFactory().locatorForAll('[role="option"]')();
    for (const option of options) {
      if ((await option.text()).trim() !== 'Replay') continue;
      const disabled = await option.getAttribute('aria-disabled');
      await option.click();
      await option.sendKeys(TestKey.ESCAPE);
      return disabled;
    }
    throw new Error('Missing replay option');
  }
  async purpose(): Promise<string> {
    return (await this.locatorFor('#run-create-purpose')()).text();
  }
  async text(): Promise<string> {
    return (await this.host()).text();
  }
  async failure(): Promise<string> {
    return (await this.locatorFor('[data-run-create-error]')()).text();
  }
  async disabled(): Promise<boolean> {
    return (await this.locatorFor('[data-run-create-submit]')()).getProperty<boolean>('disabled');
  }
  async busy(): Promise<string | null> {
    return (await this.locatorFor('form')()).getAttribute('aria-busy');
  }
}

type CreateRunCall = (
  projectId: string,
  input: CreateRunInput,
  key: string,
  signal?: AbortSignal,
) => Promise<CreatedRun>;
export interface CreateRunTestState {
  allowed: WritableSignal<boolean>;
  canMutate: WritableSignal<boolean>;
  currentProject: WritableSignal<{ id: string }>;
  currentOrg: WritableSignal<{ id: string }>;
  createRun: Mock<CreateRunCall>;
  newIdempotencyKey: Mock<() => string>;
  ended: Set<() => void>;
  navigate: MockInstance<Router['navigate']>;
}
export function setupCreateRun(): CreateRunTestState {
  const allowed = signal(true);
  const canMutate = signal(true);
  const currentProject = signal({ id: PROJECT });
  const currentOrg = signal({ id: 'org-1' });
  const createRun = vi.fn<CreateRunCall>().mockResolvedValue(CREATED);
  const newIdempotencyKey = vi.fn<() => string>(() => crypto.randomUUID());
  const ended = new Set<() => void>();
  TestBed.configureTestingModule({
    imports: [CreateRunDialogComponent],
    providers: [
      provideZonelessChangeDetection(),
      provideRouter([]),
      { provide: RunDataService, useValue: { createRun, newIdempotencyKey } },
      {
        provide: SessionStore,
        useValue: {
          has: () => allowed(),
          currentOrg,
          currentProject,
          onSessionEnd: (handler: () => void) => {
            ended.add(handler);
            return () => ended.delete(handler);
          },
        },
      },
      { provide: NarrowMutationPolicy, useValue: { canMutate, allowsMutation: () => canMutate() } },
    ],
  });
  const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
  return {
    allowed,
    canMutate,
    currentOrg,
    currentProject,
    createRun,
    newIdempotencyKey,
    ended,
    navigate,
  };
}

export async function renderCreateRun() {
  const opener = document.createElement('button');
  opener.textContent = 'Open create run';
  document.body.appendChild(opener);
  opener.focus();
  const fixture = TestBed.createComponent(CreateRunDialogComponent);
  fixture.componentRef.setInput('projectId', PROJECT);
  await fixture.whenStable();
  const loader = TestbedHarnessEnvironment.documentRootLoader(fixture);
  const dialog = await loader.getHarness(CreateRunDialogHarness);
  return { fixture, loader, dialog, opener };
}
