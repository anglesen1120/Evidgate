import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { PolicyRevisionDto } from '@evidgate/sdk';
import { MAX_DIFF_VALUE_CHARS } from './policy-diff';
import type { PublishResult } from './policy.store';
import { PublishDialogComponent } from './publish-dialog.component';
import {
  conflict,
  configurePublishDialog,
  edit,
  FakePolicyStore,
  refused,
  render,
  revision,
  waitForConflict,
} from './publish-dialog.test-support';
describe('PublishDialogComponent', () => {
  let store: FakePolicyStore;
  const canWrite = signal(true);
  beforeEach(() => {
    store = new FakePolicyStore();
    canWrite.set(true);
    configurePublishDialog(store, canWrite);
  });
  afterEach(() => {
    TestBed.resetTestingModule();
    vi.restoreAllMocks();
  });
  it('describes the editor with the rendered byte count before validation', async () => {
    const { root, textarea } = await render();
    expect(textarea.getAttribute('aria-describedby')).toBe('policy-body-size');
    expect(root.querySelector('#policy-body-size')?.textContent).toContain('/ 65536');
    expect(root.querySelector('#policy-local-error')).toBeNull();
  });
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
      expect(textarea.getAttribute('aria-describedby')).toBe('policy-body-size policy-local-error');
      for (const id of ['policy-body-size', 'policy-local-error']) {
        expect(root.querySelector(`#${id}`)?.textContent).toMatch(/\S/);
      }
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
    expect(dialog.isConnected).toBe(false);
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
    expect(dialog.isConnected).toBe(true);
  });
  it('opens the shared 412 dialog and Reload reads the list without resubmitting', async () => {
    store.publish.mockResolvedValue({
      kind: 'conflict',
      error: conflict,
      currentRevisionId: 'current-head',
    });
    const { fixture, root, submit, dialog } = await render();
    submit.click();
    const shared = await waitForConflict(fixture, root);
    expect(shared.textContent).toContain('current-head');
    expect(dialog.isConnected).toBe(false);
    const reload = shared.querySelector<HTMLButtonElement>('button.reload');
    expect(reload).not.toBeNull();
    reload?.click();
    await fixture.whenStable();
    expect(store.reload).toHaveBeenCalledTimes(1);
    expect(store.publish).toHaveBeenCalledTimes(1);
    expect(shared.isConnected).toBe(false);
  });
  it('Keep editing returns to the same textarea draft without any reload or publish', async () => {
    store.publish.mockResolvedValue({
      kind: 'conflict',
      error: conflict,
      currentRevisionId: 'current-head',
    });
    const { fixture, root, textarea, submit } = await render();
    edit(textarea, '{"flaky_budget":9}');
    submit.click();
    const shared = await waitForConflict(fixture, root);
    shared.querySelector<HTMLButtonElement>('footer button:not(.reload)')?.click();
    await fixture.whenStable();
    const reopened = root.querySelector<HTMLTextAreaElement>('#policy-publish-body');
    expect(root.querySelector('[data-policy-publish-dialog]')).not.toBeNull();
    expect(reopened?.value).toBe('{"flaky_budget":9}');
    expect(document.activeElement).toBe(reopened);
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
    const shared = await waitForConflict(fixture, root);
    shared.querySelector<HTMLButtonElement>('button.reload')?.click();
    await fixture.whenStable();
    expect(shared.isConnected).toBe(true);
    expect(shared.textContent).toContain(refused.message);
    expect(store.publish).toHaveBeenCalledTimes(1);
  });
  it('uses one focus trap, wraps both boundaries and restores opener focus on Escape', async () => {
    const { fixture, root, textarea, submit, dialog, opener } = await render();
    // JSDOM has no layout; CDK checks rendered geometry before traversing the real focus trap.
    vi.spyOn(textarea, 'offsetWidth', 'get').mockReturnValue(100);
    vi.spyOn(submit, 'offsetWidth', 'get').mockReturnValue(100);
    const anchors = document.querySelectorAll<HTMLElement>('.cdk-focus-trap-anchor');
    expect(anchors).toHaveLength(2);
    const container = dialog.closest('[role="dialog"]');
    const titleId = container?.getAttribute('aria-labelledby') ?? '';
    const descriptionId = container?.getAttribute('aria-describedby') ?? '';
    expect(document.getElementById(titleId)?.textContent).toContain('Publish revision');
    expect(document.getElementById(descriptionId)?.textContent).toContain('activation mode');
    submit.focus();
    anchors[1]?.focus();
    expect(document.activeElement).toBe(textarea);
    anchors[0]?.focus();
    expect(document.activeElement).toBe(submit);
    dialog.dispatchEvent(
      new KeyboardEvent('keydown', { key: 'Escape', keyCode: 27, bubbles: true }),
    );
    await fixture.whenStable();
    expect(dialog.isConnected).toBe(false);
    expect(document.activeElement).toBe(opener);
    expect(store.publish).not.toHaveBeenCalled();
    expect(root.querySelector('#policy-publish-body')).toBeNull();
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
    dialog.dispatchEvent(
      new KeyboardEvent('keydown', { key: 'Escape', keyCode: 27, bubbles: true }),
    );
    expect(dialog.isConnected).toBe(true);
    expect(submit.disabled).toBe(true);
    expect(store.publish).toHaveBeenCalledTimes(1);
    resolve?.({ kind: 'unchanged', revision });
    await Promise.resolve();
    await fixture.whenStable();
    expect(dialog.isConnected).toBe(false);
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
    const { fixture, root, submit } = await render();
    submit.click();
    const shared = await waitForConflict(fixture, root);
    shared.querySelector<HTMLButtonElement>('button.reload')?.click();
    fixture.detectChanges();
    shared.querySelector<HTMLButtonElement>('footer button:not(.reload)')?.click();
    await fixture.whenStable();
    const reopened = root.querySelector<HTMLTextAreaElement>('#policy-publish-body');
    if (!reopened) throw new Error('Expected reopened editor');
    edit(reopened, '{"flaky_budget":3}');
    resolve?.();
    await Promise.resolve();
    await fixture.whenStable();
    expect(root.querySelector('[data-policy-publish-dialog]')).not.toBeNull();
    expect(reopened.value).toBe('{"flaky_budget":3}');
    expect(store.publish).toHaveBeenCalledTimes(1);
  });
  it('keeps publish disabled after a seed failure and allows closing without sending', async () => {
    store.loadBody.mockRejectedValue(new Error('body read failed'));
    const { fixture, root, submit, dialog } = await render();
    expect(submit.disabled).toBe(true);
    expect(root.querySelector('[role="alert"]')?.textContent).toMatch(/\S/);
    submit.click();
    expect(store.publish).not.toHaveBeenCalled();
    dialog.dispatchEvent(
      new KeyboardEvent('keydown', { key: 'Escape', keyCode: 27, bubbles: true }),
    );
    await fixture.whenStable();
    expect(dialog.isConnected).toBe(false);
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
    const root = document.body;
    const pending = fixture.componentInstance.open(revision.id, document.createElement('button'));
    fixture.detectChanges();
    await fixture.whenStable();
    const dialog = root.querySelector<HTMLElement>('[data-policy-publish-dialog]');
    expect(dialog).not.toBeNull();
    dialog?.dispatchEvent(
      new KeyboardEvent('keydown', { key: 'Escape', keyCode: 27, bubbles: true }),
    );
    resolve?.(revision);
    await pending;
    await fixture.whenStable();
    expect(dialog?.isConnected).toBe(false);
    expect(root.querySelector('#policy-publish-body')).toBeNull();
    expect(store.publish).not.toHaveBeenCalled();
  });
  it('closes and clears a draft when write permission is revoked', async () => {
    const { fixture, dialog } = await render();
    canWrite.set(false);
    await fixture.whenStable();
    expect(dialog.isConnected).toBe(false);
    expect(document.querySelector('#policy-publish-body')).toBeNull();
    expect(store.publish).not.toHaveBeenCalled();
  });
  it('does not fetch a seed when write permission is missing', async () => {
    canWrite.set(false);
    const fixture = TestBed.createComponent(PublishDialogComponent);
    fixture.detectChanges();
    await fixture.componentInstance.open(revision.id, document.createElement('button'));
    await fixture.whenStable();
    expect(store.loadBody).not.toHaveBeenCalled();
    expect(document.querySelector('[data-policy-publish-dialog]')).toBeNull();
  });
  it('clears a stale server refusal as soon as the draft is edited', async () => {
    store.publish.mockResolvedValue({ kind: 'refused', error: refused });
    const { fixture, root, textarea, submit } = await render();
    submit.click();
    await fixture.whenStable();
    expect(root.querySelector('[data-policy-error]')?.textContent).toContain(refused.message);
    edit(textarea, '{"flaky_budget":7}');
    await fixture.whenStable();
    expect(root.querySelector('[data-policy-error]')).toBeNull();
  });
  it('clips a conflict field label to the same budget as its values', async () => {
    const longKey = 'k'.repeat(MAX_DIFF_VALUE_CHARS * 2);
    const headBody: PolicyRevisionDto = { ...revision, id: 'current-head', body: {} };
    store.loadBody.mockResolvedValueOnce(revision).mockResolvedValueOnce(headBody);
    store.publish.mockResolvedValue({
      kind: 'conflict',
      error: conflict,
      currentRevisionId: 'current-head',
    });
    const { fixture, root, textarea, submit } = await render();
    edit(textarea, JSON.stringify({ [longKey]: 1 }, null, 2));
    submit.click();
    const shared = await waitForConflict(fixture, root);

    const heading = shared.querySelector('.field h3');
    const label = [...(heading?.childNodes ?? [])]
      .filter((node) => node.nodeType === Node.TEXT_NODE)
      .map((node) => node.textContent ?? '')
      .join('')
      .trim();
    expect(`/${longKey}`.length).toBeGreaterThan(MAX_DIFF_VALUE_CHARS);
    expect(label.length).toBeLessThanOrEqual(MAX_DIFF_VALUE_CHARS);
    expect(label.endsWith('…')).toBe(true);
    expect(label).not.toContain(longKey);
  });
  it('measures the compact body, so pretty-printing cannot push it over the cap', async () => {
    const { fixture, root, textarea, submit } = await render();
    // The seed is pretty-printed: the hint reports the compact body, not the editor's text.
    expect(new TextEncoder().encode(textarea.value).byteLength).toBeGreaterThan(18);
    expect(root.querySelector('#policy-body-size')?.textContent).toContain('18 / 65536');
    const text = JSON.stringify({ x: 'a'.repeat(65528) }, null, 2);
    expect(new TextEncoder().encode(text).byteLength).toBeGreaterThan(65536);
    edit(textarea, text);
    fixture.detectChanges();
    expect(root.querySelector('#policy-body-size')?.textContent).toContain('65536 / 65536');
    submit.click();
    await fixture.whenStable();
    expect(store.publish).toHaveBeenCalledExactlyOnceWith(text);
    expect(root.querySelector('[data-policy-publish-local-error]')).toBeNull();
  });
});
