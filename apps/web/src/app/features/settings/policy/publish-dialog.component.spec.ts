import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { PolicyRevisionDto } from '@evidgate/sdk';
import type { ApiError } from '../../../core/http/api-error';
import { SessionStore } from '../../../core/session/session.store';
import { PolicyStore, type PublishResult } from './policy.store';
import { PublishDialogComponent } from './publish-dialog.component';

const revision: PolicyRevisionDto = {
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
const refused: ApiError = {
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
const conflict: ApiError = {
  ...refused,
  kind: 'precondition_failed',
  status: 412,
  code: 'stale_revision',
  details: [{ path: 'revision_id', reason: 'current-head' }],
};
class FakePolicyStore {
  readonly phase = signal<'ready' | 'error'>('ready');
  readonly loadError = signal<ApiError | null>(null);
  readonly publishing = signal(false);
  readonly loadBody = vi.fn(() => Promise.resolve(revision));
  readonly publish = vi.fn<(text: string) => Promise<PublishResult>>(() =>
    Promise.resolve({ kind: 'created', revision }),
  );
  readonly reload = vi.fn(() => Promise.resolve());
}
const originalShow = Object.getOwnPropertyDescriptor(HTMLDialogElement.prototype, 'showModal');
const originalClose = Object.getOwnPropertyDescriptor(HTMLDialogElement.prototype, 'close');
describe('PublishDialogComponent', () => {
  let store: FakePolicyStore;
  const canWrite = signal(true);
  beforeEach(() => {
    store = new FakePolicyStore();
    canWrite.set(true);
    Object.defineProperty(HTMLDialogElement.prototype, 'showModal', {
      configurable: true,
      value: function (this: HTMLDialogElement) {
        this.open = true;
      },
    });
    Object.defineProperty(HTMLDialogElement.prototype, 'close', {
      configurable: true,
      value: function (this: HTMLDialogElement) {
        this.open = false;
      },
    });
    TestBed.configureTestingModule({
      providers: [
        { provide: PolicyStore, useValue: store },
        {
          provide: SessionStore,
          useValue: { has: (code: string) => code === 'policy:write' && canWrite() },
        },
      ],
    });
  });
  afterEach(() => {
    TestBed.resetTestingModule();
    if (originalShow) Object.defineProperty(HTMLDialogElement.prototype, 'showModal', originalShow);
    else Reflect.deleteProperty(HTMLDialogElement.prototype, 'showModal');
    if (originalClose) Object.defineProperty(HTMLDialogElement.prototype, 'close', originalClose);
    else Reflect.deleteProperty(HTMLDialogElement.prototype, 'close');
    vi.restoreAllMocks();
  });
  async function render() {
    const fixture = TestBed.createComponent(PublishDialogComponent);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    const opener = document.createElement('button');
    root.append(opener);
    await fixture.componentInstance.open(revision.id, opener);
    fixture.detectChanges();
    await fixture.whenStable();
    const textarea = root.querySelector<HTMLTextAreaElement>('#policy-publish-body');
    const submit = root.querySelector<HTMLButtonElement>('[data-policy-publish-submit]');
    const dialog = root.querySelector<HTMLDialogElement>('[data-policy-publish-dialog]');
    if (!textarea || !submit || !dialog) throw new Error('Expected publish controls');
    return { fixture, root, opener, textarea, submit, dialog };
  }
  function edit(textarea: HTMLTextAreaElement, value: string): void {
    textarea.value = value;
    textarea.dispatchEvent(new Event('input'));
  }
  it.each(['{broken', '[]', 'null', '1', '"string"'])(
    'rejects invalid JSON object %s locally without publishing',
    async (text) => {
      const { fixture, root, textarea, submit } = await render();
      edit(textarea, text);
      submit.click();
      await fixture.whenStable();
      expect(store.publish).not.toHaveBeenCalled();
      expect(root.querySelector('[data-policy-publish-local-error]')?.textContent).toMatch(/\S/);
      expect(textarea.value).toBe(text);
      expect(textarea.getAttribute('aria-invalid')).toBe('true');
    },
  );
  it('refuses 65537 UTF-8 bytes locally and shows the byte count versus the limit', async () => {
    const { fixture, root, textarea, submit } = await render();
    const text = JSON.stringify({ x: 'a'.repeat(65529) });
    expect(new TextEncoder().encode(text).byteLength).toBe(65537);
    edit(textarea, text);
    submit.click();
    await fixture.whenStable();
    expect(store.publish).not.toHaveBeenCalled();
    expect(root.querySelector('#policy-body-size')?.textContent).toContain('65537 / 65536');
    expect(root.querySelector('[data-policy-publish-local-error]')?.textContent).toMatch(/\S/);
  });
  it('allows exactly 65536 bytes and emits a server-confirmed success', async () => {
    const { fixture, textarea, submit, dialog } = await render();
    const done = vi.fn();
    fixture.componentInstance.completed.subscribe(done);
    const text = JSON.stringify({ x: 'a'.repeat(65528) });
    expect(new TextEncoder().encode(text).byteLength).toBe(65536);
    edit(textarea, text);
    submit.click();
    await fixture.whenStable();
    expect(store.publish).toHaveBeenCalledExactlyOnceWith(text);
    expect(done).toHaveBeenCalledWith({ revision, created: true });
    expect(dialog.open).toBe(false);
  });
  it('retains the draft and renders 422 code, message and every detail verbatim as text', async () => {
    store.publish.mockResolvedValue({ kind: 'refused', error: refused });
    const { fixture, root, textarea, submit, dialog } = await render();
    const text = textarea.value;
    submit.click();
    await fixture.whenStable();
    const alert = root.querySelector('[data-policy-error]');
    expect(alert?.getAttribute('data-error-code')).toBe(refused.code);
    expect(alert?.textContent).toContain(refused.code);
    expect(alert?.textContent).toContain(refused.message);
    for (const detail of refused.details) {
      expect(alert?.textContent).toContain(detail.path);
      expect(alert?.textContent).toContain(detail.reason);
    }
    expect(alert?.querySelector('img, b, [onerror]')).toBeNull();
    expect(textarea.value).toBe(text);
    expect(dialog.open).toBe(true);
  });
  it('opens the shared 412 dialog and Reload reads the list without resubmitting', async () => {
    store.publish.mockResolvedValue({
      kind: 'conflict',
      error: conflict,
      currentRevisionId: 'current-head',
    });
    const { fixture, root, submit, dialog } = await render();
    submit.click();
    await fixture.whenStable();
    const shared = root.querySelector<HTMLDialogElement>('app-conflict-dialog dialog');
    expect(shared?.open).toBe(true);
    expect(shared?.textContent).toContain('current-head');
    expect(dialog.open).toBe(false);
    const reload = shared?.querySelector<HTMLButtonElement>('button.reload');
    expect(reload).not.toBeNull();
    reload?.click();
    await fixture.whenStable();
    expect(store.reload).toHaveBeenCalledTimes(1);
    expect(store.publish).toHaveBeenCalledTimes(1);
    expect(shared?.open).toBe(false);
  });
  it('Keep editing returns to the same textarea draft without any reload or publish', async () => {
    store.publish.mockResolvedValue({
      kind: 'conflict',
      error: conflict,
      currentRevisionId: 'current-head',
    });
    const { fixture, root, textarea, submit, dialog } = await render();
    edit(textarea, '{"flaky_budget":9}');
    submit.click();
    await fixture.whenStable();
    root.querySelector<HTMLButtonElement>('app-conflict-dialog footer button')?.click();
    await fixture.whenStable();
    expect(dialog.open).toBe(true);
    expect(textarea.value).toBe('{"flaky_budget":9}');
    expect(document.activeElement).toBe(textarea);
    expect(store.reload).not.toHaveBeenCalled();
    expect(store.publish).toHaveBeenCalledTimes(1);
  });
  it('keeps a failed explicit reload in the conflict dialog with the server error', async () => {
    store.publish.mockResolvedValue({
      kind: 'conflict',
      error: conflict,
      currentRevisionId: 'current-head',
    });
    store.reload.mockImplementation(() => {
      store.phase.set('error');
      store.loadError.set(refused);
      return Promise.resolve();
    });
    const { fixture, root, submit } = await render();
    submit.click();
    await fixture.whenStable();
    root.querySelector<HTMLButtonElement>('app-conflict-dialog button.reload')?.click();
    await fixture.whenStable();
    expect(root.querySelector<HTMLDialogElement>('app-conflict-dialog dialog')?.open).toBe(true);
    expect(root.querySelector('app-conflict-dialog')?.textContent).toContain(refused.message);
    expect(store.publish).toHaveBeenCalledTimes(1);
  });
  it('wraps Tab and Shift+Tab and restores opener focus on cancel', async () => {
    const { fixture, root, textarea, submit, dialog, opener } = await render();
    const key = (shiftKey: boolean) =>
      new KeyboardEvent('keydown', { key: 'Tab', shiftKey, bubbles: true, cancelable: true });
    submit.focus();
    const forward = key(false);
    submit.dispatchEvent(forward);
    expect(forward.defaultPrevented).toBe(true);
    expect(document.activeElement).toBe(textarea);
    const backward = key(true);
    textarea.dispatchEvent(backward);
    expect(backward.defaultPrevented).toBe(true);
    expect(document.activeElement).toBe(submit);
    dialog.dispatchEvent(new Event('cancel', { cancelable: true }));
    await fixture.whenStable();
    expect(dialog.open).toBe(false);
    expect(document.activeElement).toBe(opener);
    expect(store.publish).not.toHaveBeenCalled();
    expect(root.querySelector<HTMLTextAreaElement>('#policy-publish-body')?.value).toBe('');
  });
  it('ignores Escape and double submits while publishing', async () => {
    let resolve: ((result: PublishResult) => void) | undefined;
    store.publish.mockImplementation(
      () =>
        new Promise((done) => {
          resolve = done;
        }),
    );
    const { fixture, submit, dialog } = await render();
    submit.click();
    submit.click();
    fixture.detectChanges();
    const cancel = new Event('cancel', { cancelable: true });
    dialog.dispatchEvent(cancel);
    expect(cancel.defaultPrevented).toBe(true);
    expect(dialog.open).toBe(true);
    expect(submit.disabled).toBe(true);
    expect(store.publish).toHaveBeenCalledTimes(1);
    resolve?.({ kind: 'unchanged', revision });
    await Promise.resolve();
    await fixture.whenStable();
    expect(dialog.open).toBe(false);
  });
  it('does not let a late reload close an editor reopened with Keep editing', async () => {
    let resolve: (() => void) | undefined;
    store.reload.mockImplementation(
      () =>
        new Promise<void>((done) => {
          resolve = done;
        }),
    );
    store.publish.mockResolvedValue({
      kind: 'conflict',
      error: conflict,
      currentRevisionId: 'current-head',
    });
    const { fixture, root, textarea, submit, dialog } = await render();
    submit.click();
    await fixture.whenStable();
    root.querySelector<HTMLButtonElement>('app-conflict-dialog button.reload')?.click();
    fixture.detectChanges();
    root.querySelector<HTMLButtonElement>('app-conflict-dialog footer button')?.click();
    await fixture.whenStable();
    edit(textarea, '{"flaky_budget":3}');
    resolve?.();
    await Promise.resolve();
    await fixture.whenStable();
    expect(dialog.open).toBe(true);
    expect(textarea.value).toBe('{"flaky_budget":3}');
    expect(store.publish).toHaveBeenCalledTimes(1);
  });
  it('keeps publish disabled after a seed failure and allows closing without sending', async () => {
    store.loadBody.mockRejectedValue(new Error('body read failed'));
    const { fixture, root, submit, dialog } = await render();
    expect(submit.disabled).toBe(true);
    expect(root.querySelector('[role="alert"]')?.textContent).toMatch(/\S/);
    submit.click();
    expect(store.publish).not.toHaveBeenCalled();
    dialog.dispatchEvent(new Event('cancel', { cancelable: true }));
    await fixture.whenStable();
    expect(dialog.open).toBe(false);
  });
  it('allows cancelling a pending seed and ignores the late body', async () => {
    let resolve: ((value: PolicyRevisionDto) => void) | undefined;
    store.loadBody.mockImplementation(
      () =>
        new Promise((done) => {
          resolve = done;
        }),
    );
    const fixture = TestBed.createComponent(PublishDialogComponent);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    const pending = fixture.componentInstance.open(revision.id, document.createElement('button'));
    fixture.detectChanges();
    const dialog = root.querySelector<HTMLDialogElement>('[data-policy-publish-dialog]');
    dialog?.dispatchEvent(new Event('cancel', { cancelable: true }));
    resolve?.(revision);
    await pending;
    await fixture.whenStable();
    expect(dialog?.open).toBe(false);
    expect(root.querySelector<HTMLTextAreaElement>('#policy-publish-body')?.value).toBe('');
    expect(store.publish).not.toHaveBeenCalled();
  });
  it('closes and clears a draft when write permission is revoked', async () => {
    const { fixture, textarea, dialog } = await render();
    canWrite.set(false);
    await fixture.whenStable();
    expect(dialog.open).toBe(false);
    expect(textarea.value).toBe('');
    expect(store.publish).not.toHaveBeenCalled();
  });
  it('does not fetch a seed when write permission is missing', async () => {
    canWrite.set(false);
    const { dialog } = await render();
    expect(store.loadBody).not.toHaveBeenCalled();
    expect(dialog.open).toBe(false);
  });
});
