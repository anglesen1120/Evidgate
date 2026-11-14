import type { IngestTokenCreatedResponse } from '@evidgate/sdk';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { ActivatedRoute, convertToParamMap, Router, RouterLink } from '@angular/router';
import { BehaviorSubject } from 'rxjs';

import { OnboardingDataService } from '../../../core/data/onboarding-data.service';
import { SessionStore } from '../../../core/session/session.store';
import { RevealSlot } from '../../../shared/ui/reveal/one-time-reveal.slot';
import { OnboardingStore } from '../onboarding.store';
import { TOKEN } from '../onboarding.store.test-support';
import { ConnectCiPage } from '../steps/connect-ci.page';
import { CreateProjectPage } from '../steps/create-project.page';
import { EnableCollectionPage } from '../steps/enable-collection.page';
import { requiredElement } from '../steps/step-controls.testing';
import { OnboardingShellComponent } from './onboarding-shell.component';
import {
  CreateProjectStub,
  EnableCollectionStub,
  ShellRouterLinkStub,
  finishedRouter,
  shellStore,
} from './onboarding-shell.spec-support';

const DRAFTS: Readonly<Record<string, string>> = {
  'connect-oidc-repo-id': '123',
  'connect-oidc-repo-name': 'review-draft/web',
  'connect-oidc-refs': 'refs/heads/draft',
  'connect-oidc-workflows': 'review-draft/web/.github/workflows/ci.yml@refs/heads/draft',
  'connect-token-name': 'review-draft token',
};

describe('onboarding session connection drafts', () => {
  let fixture: ComponentFixture<OnboardingShellComponent>;
  let root: HTMLElement;
  const orgParams = new BehaviorSubject(convertToParamMap({ orgId: 'org-1' }));
  const createIngestToken = vi.fn<OnboardingDataService['createIngestToken']>();
  let endSession: () => void = () => undefined;

  beforeEach(async () => {
    orgParams.next(convertToParamMap({ orgId: 'org-1' }));
    createIngestToken.mockReset().mockResolvedValue({ token: TOKEN, secret: 'one-time-only' });
    const store = {
      ...shellStore({
        currentStep: 4,
        viewStep: 2,
        done: { 1: true, 2: true, 3: true, 4: true, 5: false },
      }),
      bindings: signal([]),
      tokens: signal([]),
      bindingsTruncated: signal(false),
      tokensTruncated: signal(false),
      refresh: vi.fn<() => Promise<void>>().mockResolvedValue(undefined),
    };
    await TestBed.configureTestingModule({
      imports: [OnboardingShellComponent],
      providers: [
        provideZonelessChangeDetection(),
        { provide: OnboardingStore, useValue: store },
        { provide: OnboardingDataService, useValue: { createIngestToken } },
        {
          provide: SessionStore,
          useValue: {
            has: () => true,
            onSessionEnd: (callback: () => void) => {
              endSession = callback;
              return () => undefined;
            },
          },
        },
        { provide: Router, useValue: finishedRouter() },
        { provide: ActivatedRoute, useValue: { pathFromRoot: [{ paramMap: orgParams }] } },
      ],
    })
      .overrideDirective(ShellRouterLinkStub, { set: { selector: '[routerLink]' } })
      .overrideComponent(OnboardingShellComponent, {
        remove: { imports: [CreateProjectPage, EnableCollectionPage, RouterLink] },
        add: { imports: [CreateProjectStub, EnableCollectionStub, ShellRouterLinkStub] },
      })
      .compileComponents();
    fixture = TestBed.createComponent(OnboardingShellComponent);
    root = fixture.nativeElement as HTMLElement;
    await fixture.whenStable();
  });

  async function fillDrafts(): Promise<void> {
    for (const [id, value] of Object.entries(DRAFTS)) {
      const field = requiredElement<HTMLInputElement | HTMLTextAreaElement>(root, `#${id}`);
      field.value = value;
      field.dispatchEvent(new Event('input', { bubbles: true }));
    }
    await fixture.whenStable();
  }

  async function press(selector: string): Promise<void> {
    requiredElement<HTMLButtonElement>(root, selector).click();
    await fixture.whenStable();
  }

  function expectDrafts(values: Readonly<Record<string, string>> = DRAFTS): void {
    for (const [id, value] of Object.entries(values)) {
      expect(requiredElement<HTMLInputElement | HTMLTextAreaElement>(root, `#${id}`).value).toBe(
        value,
      );
    }
  }

  it('keeps all non-secret drafts through Next/Back and step selection without recreating CI', async () => {
    await fillDrafts();
    const instance = fixture.debugElement
      .query(By.directive(ConnectCiPage))
      .injector.get(ConnectCiPage);
    await press('[data-testid="onboarding-next"]');
    expect(
      root.querySelector('[data-testid="connect-ci-page"]')?.closest('[hidden]'),
    ).not.toBeNull();
    await press('[data-testid="onboarding-back"]');
    expectDrafts();
    await press('[data-testid="onboarding-step-4"] button');
    await press('[data-testid="onboarding-step-2"] button');
    expectDrafts();
    expect(
      fixture.debugElement.query(By.directive(ConnectCiPage)).injector.get(ConnectCiPage),
    ).toBe(instance);
    expect(createIngestToken).not.toHaveBeenCalled();
  });

  it('clears a one-time reveal when leaving the CI step but retains its non-secret drafts', async () => {
    await fillDrafts();
    await press('[data-testid="connect-token-submit"]');
    await vi.waitFor(() => {
      fixture.detectChanges();
      expect(requiredElement<HTMLInputElement>(root, '[data-testid="reveal-value"]').value).toBe(
        'one-time-only',
      );
    });
    const slot = fixture.debugElement.query(By.directive(ConnectCiPage)).injector.get(RevealSlot);
    await press('[data-testid="onboarding-next"]');
    expect(slot.current()).toBeNull();
    expect(root.textContent).not.toContain('one-time-only');
    await press('[data-testid="onboarding-back"]');
    expectDrafts();
    expect(root.querySelector('[data-testid="connect-reveal"]')).toBeNull();
  });

  it('does not reveal a secret from a command that answers after the CI step was left', async () => {
    let resolveToken: (value: IngestTokenCreatedResponse) => void = () => undefined;
    // The web project targets ES2022, which does not expose Promise.withResolvers.
    createIngestToken.mockImplementationOnce(
      () =>
        new Promise<IngestTokenCreatedResponse>((resolve) => {
          resolveToken = resolve;
        }),
    );
    await fillDrafts();
    await press('[data-testid="connect-token-submit"]');
    await press('[data-testid="onboarding-next"]');
    resolveToken({ token: TOKEN, secret: 'late-one-time-only' });
    await fixture.whenStable();
    expect(createIngestToken).toHaveBeenCalledTimes(1);
    expect(root.querySelector('[data-testid="connect-reveal"]')).toBeNull();
    await press('[data-testid="onboarding-back"]');
    expect(root.querySelector('[data-testid="connect-reveal"]')).toBeNull();
    expect(root.querySelector('[data-testid="reveal-value"]')).toBeNull();
    expectDrafts();
  });

  it('clears a retained step’s one-time reveal when the session ends', async () => {
    await fillDrafts();
    await press('[data-testid="connect-token-submit"]');
    const slot = fixture.debugElement.query(By.directive(ConnectCiPage)).injector.get(RevealSlot);
    await vi.waitFor(() => {
      fixture.detectChanges();
      expect(slot.current()?.value).toBe('one-time-only');
    });
    endSession();
    await fixture.whenStable();
    expect(slot.current()).toBeNull();
    expect(root.querySelector('[data-testid="reveal-value"]')).toBeNull();
  });

  it('drops drafts when the project scope changes within a reused shell', async () => {
    await fillDrafts();
    orgParams.next(convertToParamMap({ orgId: 'org-2' }));
    await fixture.whenStable();
    expectDrafts(Object.fromEntries(Object.keys(DRAFTS).map((id) => [id, ''])));
  });

  it('drops drafts on leaving onboarding even when the route store survives', async () => {
    await fillDrafts();
    fixture.destroy();
    fixture = TestBed.createComponent(OnboardingShellComponent);
    root = fixture.nativeElement as HTMLElement;
    await fixture.whenStable();
    expectDrafts(Object.fromEntries(Object.keys(DRAFTS).map((id) => [id, ''])));
    expect(root.querySelector('[data-testid="connect-reveal"]')).toBeNull();
  });
});
