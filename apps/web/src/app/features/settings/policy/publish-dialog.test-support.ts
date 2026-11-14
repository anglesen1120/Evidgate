import { signal } from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import type { PolicyRevisionDto } from '@evidgate/sdk';
import { expect, vi } from 'vitest';
import type { ApiError } from '../../../core/http/api-error';
import { SessionStore } from '../../../core/session/session.store';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow';
import { rootElement } from './policy-reads.test-support';
import { PolicyStore, type PublishResult } from './policy.store';
import { PublishDialogComponent } from './publish-dialog.component';

export const revision: PolicyRevisionDto = {
  id: 'head',
  project_id: 'project',
  revision_no: 2,
  hash: 'hash',
  state: 'active',
  body: { flaky_budget: 1 },
  created_by: 'actor',
  created_at: '2026-10-26T00:00:00Z',
  version: 1,
};
export const refused: ApiError = {
  kind: 'validation',
  status: 422,
  code: 'critical_case_not_quarantinable',
  message: 'Exact server message <img src=x onerror=1>',
  details: [
    { path: '/critical_cases/0', reason: 'First reason <b>verbatim</b>' },
    { path: '/quarantine_ids/1', reason: 'Second reason. Do not paraphrase.' },
  ],
  requestId: null,
  retryable: false,
  retryAfterSeconds: null,
};
export const conflict: ApiError = {
  ...refused,
  kind: 'precondition_failed',
  status: 412,
  code: 'stale_revision',
  details: [{ path: 'revision_id', reason: 'current-head' }],
};
export class FakePolicyStore {
  readonly phase = signal<'ready' | 'error'>('ready');
  readonly loadError = signal<ApiError | null>(null);
  readonly publishing = signal(false);
  readonly loadBody = vi.fn(() => Promise.resolve(revision));
  readonly publish = vi.fn<(text: string) => Promise<PublishResult>>(() =>
    Promise.resolve({ kind: 'created', revision }),
  );
  readonly reload = vi.fn(() => Promise.resolve());
}

export function configurePublishDialog(store: FakePolicyStore, canWrite: () => boolean): void {
  TestBed.configureTestingModule({
    providers: [
      { provide: PolicyStore, useValue: store },
      {
        provide: NarrowMutationPolicy,
        useValue: { canMutate: () => true, narrow: () => false, allowsMutation: () => true },
      },
      {
        provide: SessionStore,
        useValue: { has: (code: string) => code === 'policy:write' && canWrite() },
      },
    ],
  });
}

export async function render() {
  const fixture = TestBed.createComponent(PublishDialogComponent);
  fixture.detectChanges();
  const root = document.body;
  const opener = document.createElement('button');
  rootElement(fixture).append(opener);
  await fixture.componentInstance.open(revision.id, opener);
  fixture.detectChanges();
  await fixture.whenStable();
  const textarea = root.querySelector<HTMLTextAreaElement>('#policy-publish-body');
  const submit = root.querySelector<HTMLButtonElement>('[data-policy-publish-submit]');
  const dialog = root.querySelector<HTMLElement>('[data-policy-publish-dialog]');
  if (!textarea || !submit || !dialog) throw new Error('Expected publish controls');
  return { fixture, root, opener, textarea, submit, dialog };
}
export function edit(textarea: HTMLTextAreaElement, value: string): void {
  textarea.value = value;
  textarea.dispatchEvent(new Event('input'));
}
export async function waitForConflict(
  fixture: ComponentFixture<unknown>,
  root: HTMLElement,
): Promise<HTMLElement> {
  expect(root.querySelector('app-conflict-dialog')).not.toBeNull();
  await fixture.whenStable();
  const dialog = document.querySelector<HTMLElement>('[role="dialog"]:has(.reload)');
  if (dialog === null) throw new Error('the conflict dialog did not open');
  return dialog;
}
