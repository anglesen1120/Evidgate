import { provideZonelessChangeDetection, signal } from '@angular/core';
import { type ComponentFixture, TestBed } from '@angular/core/testing';
import type { PolicyActivationStateDto } from '@evidgate/sdk';

import { OnboardingDataService } from '../../../core/data/onboarding-data.service';
import { ApiRequestError } from '../../../core/http/api-error';
import { SessionStore } from '../../../core/session/session.store';
import { EnableCollectionPage } from './enable-collection.page';
import { requiredElement } from './step-controls.testing';

const ACTIVATION: PolicyActivationStateDto = {
  project_id: 'project-1',
  policy_revision_id: 'revision-1',
  hash: 'sha256:' + 'a'.repeat(64),
  mode: 'advisory',
  activated_by: 'user-1',
  activated_at: '2026-10-27T01:00:00Z',
};

function unreadable(): ApiRequestError {
  return new ApiRequestError({
    kind: 'forbidden',
    status: 403,
    code: 'forbidden',
    message: 'Activation is not readable.',
    requestId: null,
    retryable: false,
    details: [],
    retryAfterSeconds: null,
  });
}

describe('EnableCollectionPage Helm controls', () => {
  let fixture: ComponentFixture<EnableCollectionPage>;
  let root: HTMLElement;
  const permitted = signal(true);
  const readPolicyActivation = vi.fn<OnboardingDataService['readPolicyActivation']>();
  const createPolicyRevision = vi.fn<OnboardingDataService['createPolicyRevision']>();

  beforeEach(async () => {
    permitted.set(true);
    readPolicyActivation.mockReset().mockResolvedValue(null);
    createPolicyRevision.mockReset();
    await TestBed.configureTestingModule({
      imports: [EnableCollectionPage],
      providers: [
        provideZonelessChangeDetection(),
        {
          provide: OnboardingDataService,
          useValue: { readPolicyActivation, createPolicyRevision },
        },
        { provide: SessionStore, useValue: { has: () => permitted() } },
      ],
    }).compileComponents();
  });

  async function mount(waitForRead = true): Promise<void> {
    fixture = TestBed.createComponent(EnableCollectionPage);
    fixture.componentRef.setInput('orgId', 'org-1');
    fixture.componentRef.setInput('projectId', 'project-1');
    root = fixture.nativeElement as HTMLElement;
    await fixture.whenStable();
    if (waitForRead) {
      await vi.waitFor(() => {
        fixture.detectChanges();
        expect(requiredElement(root, 'p[role="status"]').textContent?.trim()).toBe('');
      });
    }
  }

  it('names the preview card and keeps replacement context on its primary native action', async () => {
    await mount();
    const preview = requiredElement(root, '[data-testid="enable-collection-policy-preview"]');
    expect(preview.getAttribute('data-slot')).toBe('card');
    expect(preview.getAttribute('aria-labelledby')).toBe('enable-preview-heading');
    const confirm = requiredElement<HTMLButtonElement>(
      root,
      '[data-testid="enable-collection-confirm"]',
    );
    expect(confirm.getAttribute('data-slot')).toBe('button');
    expect(confirm.type).toBe('submit');
    expect(confirm.disabled).toBe(false);
    expect(confirm.getAttribute('aria-describedby')).toContain('enable-replaces-warning');
    confirm.focus();
    expect(document.activeElement).toBe(confirm);
  });

  it('reserves a status line while the initial read keeps the confirm disabled', async () => {
    readPolicyActivation.mockImplementation(() => new Promise(() => undefined));
    await mount(false);
    const status = requiredElement(root, 'p[role="status"]');
    expect(status.textContent).toContain('Reading');
    expect(status.getAttribute('tabindex')).toBe('-1');
    expect(status.classList).toContain('min-h-(--eg-leading-sm)');
    expect(
      requiredElement<HTMLButtonElement>(root, '[data-testid="enable-collection-confirm"]')
        .disabled,
    ).toBe(true);
  });

  it('offers a keyboard-focusable read-only recovery instead of permitting confirmation', async () => {
    readPolicyActivation.mockRejectedValue(unreadable());
    await mount();
    const recovery = requiredElement<HTMLButtonElement>(
      root,
      '[data-testid="enable-collection-read-again"]',
    );
    expect(recovery.getAttribute('data-slot')).toBe('button');
    expect(recovery.type).toBe('button');
    recovery.focus();
    expect(document.activeElement).toBe(recovery);
    expect(
      requiredElement<HTMLButtonElement>(root, '[data-testid="enable-collection-confirm"]')
        .disabled,
    ).toBe(true);
    expect(createPolicyRevision).not.toHaveBeenCalled();
  });

  it('uses a named success card without inventing a release verdict or leaving a confirm', async () => {
    readPolicyActivation.mockResolvedValue(ACTIVATION);
    await mount();
    const enabled = requiredElement(root, '[data-testid="enable-collection-enabled"]');
    expect(enabled.getAttribute('data-slot')).toBe('card');
    expect(enabled.getAttribute('role')).toBe('status');
    expect(enabled.getAttribute('aria-labelledby')).toBe('enable-enabled-heading');
    expect(enabled.classList).toContain('bg-(--eg-success-bg)');
    expect(enabled.classList).toContain('text-(--eg-success-fg)');
    expect(requiredElement(root, '#enable-enabled-heading').getAttribute('tabindex')).toBe('-1');
    expect(root.querySelector('[data-testid="enable-collection-confirm"]')).toBeNull();
    expect(root.textContent).not.toMatch(/\bPASS\b|\bFAIL\b/);
  });

  it('keeps missing-permission feedback connected to the disabled Helm confirm', async () => {
    permitted.set(false);
    await mount();
    const confirm = requiredElement<HTMLButtonElement>(
      root,
      '[data-testid="enable-collection-confirm"]',
    );
    expect(confirm.disabled).toBe(true);
    expect(confirm.getAttribute('aria-describedby')).toContain('enable-permission');
    expect(requiredElement(root, '#enable-permission').textContent?.trim()).not.toBe('');
  });
});
