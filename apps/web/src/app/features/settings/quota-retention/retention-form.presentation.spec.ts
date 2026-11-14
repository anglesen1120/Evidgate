import { TestBed } from '@angular/core/testing';
import { dialogButton, flush, mountRetention } from './retention-form.test-support';

function submit(root: HTMLElement): void {
  root
    .querySelector('form')
    ?.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
}

describe('RetentionFormComponent presentation safeguards', () => {
  afterEach(() => {
    vi.restoreAllMocks();
    TestBed.resetTestingModule();
  });

  it('uses the Helm project combobox and updates the selected project read, not a native select', async () => {
    const h = mountRetention();
    await h.load();
    expect(h.root.querySelector('select')).toBeNull();
    const trigger = h.root.querySelector<HTMLButtonElement>('#retention-project');
    expect(trigger?.getAttribute('role')).toBe('combobox');
    expect(h.root.querySelector('label[for="retention-project"]')).not.toBeNull();
    trigger?.click();
    await h.settle();
    const option = Array.from(document.querySelectorAll<HTMLElement>('[role="option"]')).find(
      (item) => item.textContent?.includes('Second project'),
    );
    expect(option).toBeDefined();
    option?.click();
    flush(await h.take('GET', 'p-2'), { artifact_days: 7, metadata_days: 60 });
    await h.settle();
    expect(h.root.querySelector('[data-retention-current]')?.textContent).toContain('60');
    h.http.expectNone((request) => request.method === 'PUT');
  });

  it('marks required fields and shows inline blur feedback without moving focus or showing a submit summary', async () => {
    const h = mountRetention();
    await h.load();
    expect(h.root.querySelector('app-form-required-note')?.textContent).toMatch(/required/i);
    const input = h.root.querySelector<HTMLInputElement>('#retention-artifact');
    expect(input?.required).toBe(true);
    input?.focus();
    await h.edit('', '90');
    expect(h.root.querySelector('#retention-validation-error')).toBeNull();
    input?.dispatchEvent(new Event('blur'));
    await h.settle();
    expect(input?.getAttribute('aria-invalid')).toBe('true');
    const descriptions = input?.getAttribute('aria-describedby')?.split(' ') ?? [];
    expect(descriptions).toContain('retention-hint');
    expect(
      descriptions.some((id) => h.root.querySelector(`#${id}`)?.textContent?.includes('whole')),
    ).toBe(true);
    expect(h.root.querySelector('#retention-validation-error')).toBeNull();
    expect(document.activeElement).toBe(input);
    h.http.expectNone((request) => request.method === 'PUT');
  });

  it('removes mutation fields on narrow screens but retains the draft, facts and project inspection', async () => {
    const h = mountRetention();
    await h.load();
    await h.edit('30', '90');
    const form = h.root.querySelector('form');
    h.desktop.set(false);
    form?.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    await h.settle();
    expect(h.root.querySelector('form, input, [data-retention-save]')).toBeNull();
    expect(h.root.textContent).toContain('Desktop required');
    expect(h.root.querySelector('#retention-project')).not.toBeNull();
    expect(h.root.querySelector('[data-retention-current]')?.textContent).toContain('14');
    h.http.expectNone((request) => request.method === 'PUT');
    h.desktop.set(true);
    await h.settle();
    expect(h.root.querySelector<HTMLInputElement>('#retention-artifact')?.value).toBe('30');
    expect(h.root.querySelector<HTMLInputElement>('#retention-metadata')?.value).toBe('90');
  });

  it('guards a confirmation captured before resize and retains the lowering draft', async () => {
    const h = mountRetention();
    await h.load();
    await h.edit('1', '90');
    submit(h.root);
    await h.settle();
    const confirm = dialogButton('[data-retention-confirm]');
    h.desktop.set(false);
    confirm.click();
    await h.settle();
    expect(document.querySelector('[role="dialog"]')).toBeNull();
    h.http.expectNone((request) => request.method === 'PUT');
    h.desktop.set(true);
    await h.settle();
    expect(h.root.querySelector<HTMLInputElement>('#retention-artifact')?.value).toBe('1');
  });

  it('reserves a form skeleton while loading and never exposes stale editable inputs', async () => {
    const h = mountRetention();
    TestBed.tick();
    expect(h.root.querySelector('app-loading-pattern')).not.toBeNull();
    expect(h.root.querySelector('input')).toBeNull();
    await h.load();
    expect(h.root.querySelector('app-loading-pattern')).toBeNull();
  });
});
