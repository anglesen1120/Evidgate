import { provideZonelessChangeDetection, signal } from '@angular/core';
import { type ComponentFixture, TestBed } from '@angular/core/testing';
import type { CiBindingView, IngestTokenView } from '@evidgate/sdk';

import { OnboardingDataService } from '../../../core/data/onboarding-data.service';
import { SessionStore } from '../../../core/session/session.store';
import { OnboardingStore } from '../onboarding.store';
import { OnboardingViewport } from '../onboarding-viewport.testing';
import { RevealSlot } from '../../../shared/ui/reveal/one-time-reveal.slot';
import { TOKEN } from '../onboarding.store.test-support';
import { ConnectCiPage } from './connect-ci.page';
import { requiredElement } from './step-controls.testing';

describe('ConnectCiPage Helm controls', () => {
  let fixture: ComponentFixture<ConnectCiPage>;
  let root: HTMLElement;
  let viewport: OnboardingViewport;
  const tokens = signal<readonly IngestTokenView[]>([]);
  const createIngestToken = vi.fn<OnboardingDataService['createIngestToken']>();
  const rotateIngestToken = vi.fn<OnboardingDataService['rotateIngestToken']>();
  const permitted = signal(true);
  const refreshFailure = signal<'unavailable' | null>(null);
  const refresh = vi.fn<() => Promise<void>>();
  const createCiBinding = vi.fn<OnboardingDataService['createCiBinding']>();

  beforeEach(async () => {
    permitted.set(true);
    viewport = new OnboardingViewport();
    tokens.set([]);
    createIngestToken.mockReset();
    rotateIngestToken.mockReset();
    refreshFailure.set(null);
    refresh.mockReset().mockResolvedValue(undefined);
    createCiBinding.mockReset();
    await TestBed.configureTestingModule({
      imports: [ConnectCiPage],
      providers: [
        provideZonelessChangeDetection(),
        viewport.provider,
        {
          provide: OnboardingDataService,
          useValue: { createCiBinding, createIngestToken, rotateIngestToken },
        },
        {
          provide: OnboardingStore,
          useValue: {
            bindings: signal<readonly CiBindingView[]>([]),
            tokens,
            bindingsTruncated: signal(false),
            tokensTruncated: signal(false),
            project: signal({ slug: 'storefront-web' }),
            refreshFailure,
            refresh,
          },
        },
        {
          provide: SessionStore,
          useValue: { has: () => permitted(), onSessionEnd: () => () => undefined },
        },
      ],
    }).compileComponents();
    fixture = TestBed.createComponent(ConnectCiPage);
    fixture.componentRef.setInput('orgId', 'org-1');
    fixture.componentRef.setInput('projectId', 'project-1');
    root = fixture.nativeElement as HTMLElement;
    await fixture.whenStable();
  });

  it('keeps both empty credential panels named and distinguishes their action priority', () => {
    for (const kind of ['oidc', 'token']) {
      const panel = requiredElement(root, `[data-testid="connect-${kind}-panel"]`);
      expect(panel.getAttribute('data-slot')).toBe('card');
      expect(panel.getAttribute('aria-labelledby')).toBe(`connect-${kind}-heading`);
      expect(panel.querySelectorAll('ul > li')).toHaveLength(1);
    }
    expect(requiredElement(root, '[data-testid="connect-oidc-submit"]').classList).toContain(
      'spartan-button-variant-default',
    );
    expect(requiredElement(root, '[data-testid="connect-token-submit"]').classList).toContain(
      'spartan-button-variant-outline',
    );
  });

  it('preserves native keyboard stops and labels on every Helm field', () => {
    const fields = root.querySelectorAll<HTMLInputElement | HTMLTextAreaElement>('[hlmInput]');
    expect(fields).toHaveLength(5);
    for (const field of fields) {
      expect(field.getAttribute('data-slot')).toBe('input');
      expect(field.labels?.item(0)?.htmlFor).toBe(field.id);
      expect(field.required).toBe(true);
      expect(field.labels?.item(0)?.textContent).not.toContain('*');
      const hintId = field.getAttribute('aria-describedby');
      expect(hintId).not.toBeNull();
      expect(root.querySelector(`#${hintId}`)?.textContent?.trim()).not.toBe('');
      field.focus();
      expect(document.activeElement).toBe(field);
      expect(field.tabIndex).toBe(0);
    }
  });

  it('explains required fields once across the two alternative methods', () => {
    expect(root.textContent?.match(/All fields required unless marked optional\./g)).toHaveLength(
      1,
    );
    expect(root.querySelectorAll('label [aria-hidden="true"]')).toHaveLength(0);
  });

  it('keeps field validation visible to Brain and names the error after form submission', async () => {
    requiredElement<HTMLFormElement>(
      root,
      '[data-testid="connect-oidc-panel"] form',
    ).requestSubmit();
    await fixture.whenStable();
    const field = requiredElement(root, '#connect-oidc-repo-id');
    expect(field.getAttribute('aria-invalid')).toBe('true');
    expect(field.getAttribute('data-matches-spartan-invalid')).toBe('true');
    expect(field.getAttribute('aria-describedby')).toBe(
      'connect-oidc-repo-id-hint connect-oidc-repo-id-error',
    );
    expect(createCiBinding).not.toHaveBeenCalled();
  });

  it('retains focus on the aria-disabled refresh control while refusing duplicate requests', async () => {
    refreshFailure.set('unavailable');
    refresh.mockImplementation(() => new Promise(() => undefined));
    await fixture.whenStable();
    const button = requiredElement<HTMLButtonElement>(
      root,
      '[data-testid="connect-ci-refresh-failed"] button',
    );
    button.focus();
    button.click();
    await fixture.whenStable();
    expect(button.getAttribute('data-slot')).toBe('button');
    expect(button.disabled).toBe(false);
    expect(button.getAttribute('aria-disabled')).toBe('true');
    expect(document.activeElement).toBe(button);
    button.click();
    expect(refresh).toHaveBeenCalledTimes(1);
  });

  it('keeps all mutation controls natively disabled when permission is absent', async () => {
    permitted.set(false);
    await fixture.whenStable();
    for (const field of root.querySelectorAll<HTMLInputElement | HTMLTextAreaElement>(
      '[hlmInput]',
    )) {
      expect(field.disabled).toBe(true);
    }
    for (const kind of ['oidc', 'token']) {
      expect(
        requiredElement<HTMLButtonElement>(root, `[data-testid="connect-${kind}-submit"]`).disabled,
      ).toBe(true);
    }
  });
  it('shows a touched correction on blur without submitting or moving focus', async () => {
    const field = requiredElement<HTMLInputElement>(root, '#connect-oidc-repo-id');
    field.focus();
    field.dispatchEvent(new Event('blur'));
    await fixture.whenStable();
    expect(field.getAttribute('aria-invalid')).toBe('true');
    expect(root.querySelector('#connect-oidc-errors')).toBeNull();
    expect(document.activeElement).toBe(field);
    expect(createCiBinding).not.toHaveBeenCalled();
  });

  it.each([320, 390, 767])(
    'guards all CI mutations at %ipx while retaining drafts and existing reveal',
    async (width) => {
      tokens.set([TOKEN]);
      await fixture.whenStable();
      const values: Record<string, string> = {
        'connect-oidc-repo-id': '123',
        'connect-oidc-repo-name': 'acme/web',
        'connect-oidc-refs': 'refs/heads/main',
        'connect-oidc-workflows': 'acme/web/.github/workflows/ci.yml@refs/heads/main',
        'connect-token-name': 'Retained token draft',
      };
      for (const [id, value] of Object.entries(values)) {
        const field = requiredElement<HTMLInputElement | HTMLTextAreaElement>(root, `#${id}`);
        field.value = value;
        field.dispatchEvent(new Event('input', { bubbles: true }));
      }
      await fixture.whenStable();
      const forms = Array.from(root.querySelectorAll<HTMLFormElement>('form'));
      const rotate = requiredElement<HTMLButtonElement>(
        root,
        '[data-testid="connect-token-rotate"]',
      );
      rotate.click();
      await fixture.whenStable();
      const confirm = requiredElement<HTMLButtonElement>(
        document.body,
        '[data-testid="connect-rotation-confirm"]',
      );
      fixture.debugElement.injector
        .get(RevealSlot)
        .show({ source: 'ingest-token', value: 'test-only-reveal' });
      viewport.setWidth(width);
      for (const form of forms) form.requestSubmit();
      confirm.click();
      await fixture.whenStable();
      expect(root.querySelector('form')).toBeNull();
      expect(root.querySelector('[data-testid="connect-token-rotate"]')).toBeNull();
      expect(root.querySelector('[data-testid="connect-token-draft"]')?.textContent).toContain(
        'Retained token draft',
      );
      expect(root.querySelector('[data-testid="connect-oidc-draft"]')?.textContent).toContain(
        'acme/web',
      );
      expect(requiredElement<HTMLInputElement>(root, '[data-testid="reveal-value"]').value).toBe(
        'test-only-reveal',
      );
      expect(requiredElement<HTMLButtonElement>(root, '[data-testid="reveal-copy"]').disabled).toBe(
        false,
      );
      expect(root.querySelector('[data-testid="connection-health"]')).not.toBeNull();
      expect(createCiBinding).not.toHaveBeenCalled();
      expect(createIngestToken).not.toHaveBeenCalled();
      expect(rotateIngestToken).not.toHaveBeenCalled();
      viewport.setWidth(768);
      await fixture.whenStable();
      expect(requiredElement<HTMLInputElement>(root, '#connect-token-name').value).toBe(
        'Retained token draft',
      );
      expect(requiredElement<HTMLInputElement>(root, '#connect-oidc-repo-name').value).toBe(
        'acme/web',
      );
    },
  );
});
