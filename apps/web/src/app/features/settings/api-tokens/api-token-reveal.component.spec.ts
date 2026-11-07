import { TestBed } from '@angular/core/testing';
import { control, fillCreate, openCreate, submitCreate } from './api-token-dialog.test-support';
import { CREATED, mount, deferred } from './api-tokens.test-support';

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

  it('clears on project change and on component destruction (navigation)', async () => {
    const h = mount();
    await h.settle();
    await openCreate(h);
    await fillCreate(h);
    submitCreate();
    await h.settle();
    const value = control<HTMLInputElement>('[data-testid="reveal-value"]');
    h.projects.set([{ id: 'project-2', name: 'Other', org_id: 'org-1' }]);
    await h.settle();
    expect(value.value).toBe('');
    expect(h.store.createOpen()).toBe(false);
    await openCreate(h);
    await fillCreate(h);
    submitCreate();
    await h.settle();
    const second = control<HTMLInputElement>('[data-testid="reveal-value"]');
    h.fixture.destroy();
    expect(second.value).toBe('');
    expect(document.body.innerHTML).not.toContain(CREATED.token);
  });

  it('does not show a late credential after the creating member loses write access', async () => {
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
    expect(document.querySelector('[data-testid="reveal-value"]')).toBeNull();
    expect(h.store.createOpen()).toBe(false);
  });
});
