import { TelemetryService } from '../../../core/telemetry/telemetry.service';
import { TestBed } from '@angular/core/testing';
import { control, fillCreate, openCreate, submitCreate } from './api-token-dialog.test-support';
import { CREATED, mount, deferred, project } from './api-tokens.test-support';

const originalClipboard = Object.getOwnPropertyDescriptor(navigator, 'clipboard');

describe('API token one-time reveal lifetime', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
    vi.restoreAllMocks();
    if (originalClipboard) Object.defineProperty(navigator, 'clipboard', originalClipboard);
    else Reflect.deleteProperty(navigator, 'clipboard');
  });

  it('keeps the value through Escape and outside clicks; explicit dismissal wipes the input and restores focus', async () => {
    const h = mount();
    await h.settle();
    const opener = h.root.querySelector<HTMLButtonElement>('[data-api-create]')!;
    opener.focus();
    await openCreate(h);
    await fillCreate(h);
    submitCreate();
    await h.settle();
    const value = control<HTMLInputElement>('[data-testid="reveal-value"]');
    value.dispatchEvent(
      new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }),
    );
    document.body.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true }));
    document.body.click();
    await h.settle();
    expect(value.value).toBe(CREATED.token);
    expect(h.store.createOpen()).toBe(true);
    control<HTMLButtonElement>('[data-testid="reveal-dismiss"]').click();
    await h.settle();
    expect(value.value).toBe('');
    expect(h.store.createOpen()).toBe(false);
    expect(document.activeElement).toBe(opener);
    expect(document.body.innerHTML).not.toContain(CREATED.token);
  });

  it('copies through the existing shared reveal component and announces success', async () => {
    const writeText = vi.fn<(value: string) => Promise<void>>().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText } });
    const h = mount();
    await h.settle();
    await openCreate(h);
    await fillCreate(h);
    submitCreate();
    await h.settle();
    control<HTMLButtonElement>('[data-testid="reveal-copy"]').click();
    await h.settle();
    expect(writeText).toHaveBeenCalledWith(CREATED.token);
    expect(control('[data-testid="reveal-copy-status"]').textContent).toContain('Token copied');
  });

  it('clears the displayed value on session end', async () => {
    const h = mount();
    await h.settle();
    await openCreate(h);
    await fillCreate(h);
    submitCreate();
    await h.settle();
    const value = control<HTMLInputElement>('[data-testid="reveal-value"]');
    h.endSession();
    await h.settle();
    expect(value.value).toBe('');
    expect(document.body.innerHTML).not.toContain(CREATED.token);
  });

  it('keeps the reveal bound to the minted scope when the selection moves, and clears on destruction', async () => {
    const h = mount();
    await h.settle();
    await openCreate(h);
    await fillCreate(h);
    submitCreate();
    await h.settle();
    const value = control<HTMLInputElement>('[data-testid="reveal-value"]');
    h.projects.set([project('project-2', 'Other')]);
    await h.settle();
    // The dialog stays on the scope it opened for: the only copy of the secret is in this field.
    expect(value.value).toBe(CREATED.token);
    expect(h.store.createOpen()).toBe(true);
    h.fixture.destroy();
    expect(value.value).toBe('');
    expect(document.body.innerHTML).not.toContain(CREATED.token);
  });

  it('still reveals a credential minted just before the creating member lost write access', async () => {
    const h = mount();
    await h.settle();
    await openCreate(h);
    await fillCreate(h);
    const pending = deferred<typeof CREATED>();
    h.create.mockReturnValueOnce(pending.promise);
    submitCreate();
    expect(h.create).toHaveBeenCalledTimes(1);
    h.permissions.set(new Set(['api_token:read']));
    pending.resolve(CREATED);
    await h.settle();
    // Dropping the answer would leave a token nobody can copy and few can even find to revoke.
    expect(control<HTMLInputElement>('[data-testid="reveal-value"]').value).toBe(CREATED.token);
    expect(h.store.createOpen()).toBe(true);
  });

  it('never sends the secret to console or telemetry across create, reveal, copy and dismiss', async () => {
    const sinks = [
      ...(['log', 'info', 'warn', 'error', 'debug', 'trace'] as const).map((level) =>
        vi.spyOn(console, level).mockImplementation(() => undefined),
      ),
      vi.spyOn(TelemetryService.prototype, 'reportError'),
      vi.spyOn(TelemetryService.prototype, 'reportEvent'),
      vi.spyOn(TelemetryService.prototype, 'startApiCall'),
    ];
    const h = mount();
    await h.settle();
    await openCreate(h);
    await fillCreate(h);
    submitCreate();
    await h.settle();
    expect(control<HTMLInputElement>('[data-testid="reveal-value"]').value).toBe(CREATED.token);
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
    control<HTMLButtonElement>('[data-testid="reveal-copy"]').click();
    await h.settle();
    expect(writeText).toHaveBeenCalledWith(CREATED.token);
    control<HTMLButtonElement>('[data-testid="reveal-dismiss"]').click();
    await h.settle();
    // Error arguments hide their message and stack from JSON, so flatten those explicitly.
    const flatten = (arg: unknown): string =>
      arg instanceof Error ? `${arg.message}\n${arg.stack ?? ''}` : JSON.stringify(arg);
    for (const sink of sinks) {
      const seen = sink.mock.calls.map((call) => call.map(flatten).join(' ')).join('\n');
      expect(seen, String(sink.getMockName())).not.toContain(CREATED.token);
    }
  });
});
