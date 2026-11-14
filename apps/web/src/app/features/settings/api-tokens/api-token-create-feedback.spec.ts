import { TestBed } from '@angular/core/testing';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow/narrow-mutation-policy';
import {
  control,
  fillCreate,
  openCreate,
  submitCreate,
  writeFailure,
} from './api-token-dialog.test-support';
import { mount } from './api-tokens.test-support';

describe('API token creation field feedback', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
    vi.restoreAllMocks();
  });

  it('associates the UTC hint, validates on blur and focuses a linked summary only after failed submit', async () => {
    const h = mount();
    await h.settle();
    await openCreate(h);
    const expiry = control<HTMLInputElement>('#api-create-expiry');
    expect(expiry.getAttribute('aria-describedby')).toContain('api-create-expiry-hint');
    expect(expiry.getAttribute('aria-invalid')).toBeNull();
    expect(control('[data-api-create-dialog]').textContent).toContain(
      'All fields required unless marked optional.',
    );
    expiry.value = '2020-01-01T00:00';
    expiry.dispatchEvent(new Event('input', { bubbles: true }));
    expiry.focus();
    expiry.dispatchEvent(new Event('blur'));
    await h.settle();
    expect(expiry.getAttribute('aria-invalid')).toBe('true');
    expect(expiry.getAttribute('aria-describedby')).toContain('api-create-expiry-error');
    expect(document.activeElement).toBe(expiry);
    submitCreate();
    await h.settle();
    expect(h.create).not.toHaveBeenCalled();
    const summary = control('#api-create-summary');
    expect(summary.contains(document.activeElement) || document.activeElement === summary).toBe(
      true,
    );
    const links = Array.from(summary.querySelectorAll('a'), (link) => link.getAttribute('href'));
    expect(links).toEqual(['#api-create-name', '#api-create-expiry', '#api-permission-run:read']);
    expect(expiry.value).toBe('2020-01-01T00:00');
  });

  it('keeps a permission refusal field-specific instead of marking a valid expiry invalid', async () => {
    const h = mount();
    await h.settle();
    await openCreate(h);
    await fillCreate(h);
    h.create.mockRejectedValueOnce(
      writeFailure({
        message: 'Cannot grant this permission.',
        details: [{ path: 'permissions[0]', reason: 'not_granted' }],
      }),
    );
    submitCreate();
    await h.settle();
    expect(control('#api-create-expiry').getAttribute('aria-invalid')).toBeNull();
    expect(control('[data-api-create-dialog]').textContent).toContain(
      'Cannot grant this permission.',
    );
    const summary = control('#api-create-summary');
    expect(Array.from(summary.querySelectorAll('a'), (link) => link.getAttribute('href'))).toEqual([
      '#api-permission-run:read',
    ]);
    expect(control<HTMLInputElement>('#api-create-name').value).toBe('Reader');
  });

  it('refuses an open-dialog submit after the viewport becomes narrow', async () => {
    const h = mount();
    await h.settle();
    await openCreate(h);
    await fillCreate(h);
    vi.spyOn(TestBed.inject(NarrowMutationPolicy), 'allowsMutation').mockReturnValue(false);
    submitCreate();
    await h.settle();
    expect(h.create).not.toHaveBeenCalled();
    expect(document.querySelector('[data-testid="reveal-value"]')).toBeNull();
  });
});
