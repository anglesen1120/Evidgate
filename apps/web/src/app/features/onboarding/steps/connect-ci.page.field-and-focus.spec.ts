// A refusal's field reasons clear as the visitor edits them, and a re-read keeps the place they
// were on. Both belong to the same answer: what the screen still says, and where the stop is.
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import type { IngestTokenView } from '@evidgate/sdk';

import { OnboardingDataService } from '../../../core/data/onboarding-data.service';
import { ApiRequestError, type ApiError } from '../../../core/http/api-error';
import { SessionStore } from '../../../core/session/session.store';
import { OnboardingStore } from '../onboarding.store';
import { provideDesktopOnboardingViewport } from '../onboarding-viewport.testing';
import { ConnectCiPage } from './connect-ci.page';
import { provideTokenInventory } from './connect-ci.page.test-support';

function refusal(details: NonNullable<ApiError['details']>): ApiRequestError {
  return new ApiRequestError({
    kind: 'validation',
    status: 422,
    code: 'server_refusal',
    message: 'Exact server refusal, unchanged.',
    requestId: 'request-1',
    retryable: false,
    details,
    retryAfterSeconds: null,
  });
}

describe('ConnectCiPage field reasons and re-read focus', () => {
  let fixture: ComponentFixture<ConnectCiPage>;
  let root: HTMLElement;
  const permitted = signal(true);
  const tokens = signal<readonly IngestTokenView[]>([]);
  const refreshFailure = signal<'unavailable' | null>(null);
  const createIngestToken = vi.fn<OnboardingDataService['createIngestToken']>();
  const refresh = vi.fn<(orgId: string, projectId: string) => Promise<void>>();

  beforeEach(async () => {
    permitted.set(true);
    tokens.set([]);
    refreshFailure.set(null);
    createIngestToken.mockReset();
    // The store clears its refresh failure on success, which is what takes the retry notice away.
    refresh.mockReset().mockImplementation(() => {
      refreshFailure.set(null);
      return Promise.resolve();
    });
    provideTokenInventory({ tokens, tokensRefreshFailure: refreshFailure, refreshTokens: refresh });
    await TestBed.configureTestingModule({
      imports: [ConnectCiPage],
      providers: [
        provideZonelessChangeDetection(),
        provideDesktopOnboardingViewport(),
        {
          provide: OnboardingDataService,
          useValue: { createIngestToken, rotateIngestToken: vi.fn() },
        },
        {
          provide: OnboardingStore,
          useValue: {
            project: signal<{ readonly slug: string } | null>({ slug: 'acme-web' }),
          },
        },
        {
          provide: SessionStore,
          useValue: {
            has: () => permitted(),
            onSessionEnd: () => () => undefined,
          },
        },
      ],
    }).compileComponents();
    fixture = TestBed.createComponent(ConnectCiPage);
    fixture.componentRef.setInput('orgId', 'org-1');
    fixture.componentRef.setInput('projectId', 'project-1');
    root = fixture.nativeElement as HTMLElement;
    await fixture.whenStable();
  });

  async function fill(values: Record<string, string>): Promise<void> {
    for (const [id, value] of Object.entries(values)) {
      const field = root.querySelector<HTMLInputElement | HTMLTextAreaElement>(
        `[data-testid="${id}"]`,
      )!;
      field.value = value;
      field.dispatchEvent(new Event('input', { bubbles: true }));
    }
    await fixture.whenStable();
  }

  async function submit(): Promise<void> {
    root
      .querySelector('[data-testid="connect-token-panel"] form')!
      .dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    await fixture.whenStable();
  }

  function field(id: string): HTMLInputElement | HTMLTextAreaElement {
    return root.querySelector<HTMLInputElement | HTMLTextAreaElement>(`#${id}`)!;
  }

  function retryButton(): HTMLButtonElement {
    return root.querySelector<HTMLButtonElement>(
      '[data-testid="connect-ci-refresh-failed"] button',
    )!;
  }

  /** The refresh handoff is spent after the render; one macrotask drains every queued microtask. */
  async function settle(): Promise<void> {
    // `Promise.withResolvers` is ES2024 and the project compiles against ES2022.
    await new Promise<void>((resolve) => {
      setTimeout(resolve, 0);
    });
    await fixture.whenStable();
  }

  it('clears the edited token-name refusal without clearing the alert', async () => {
    createIngestToken.mockRejectedValueOnce(
      refusal([{ path: '/name', reason: 'That token name is reserved.' }]),
    );
    await fill({ 'connect-token-name': 'Build upload' });
    await submit();
    expect(field('connect-token-name').getAttribute('aria-invalid')).toBe('true');

    await fill({ 'connect-token-name': 'Replacement token name' });
    // The edited field answers for itself: its reason and its invalidity go.
    expect(field('connect-token-name').getAttribute('aria-invalid')).toBeNull();
    expect(field('connect-token-name').getAttribute('aria-describedby')).toBe(
      'connect-token-name-hint',
    );
    expect(root.querySelector('#connect-token-name-error')).toBeNull();
    const alert = root.querySelector<HTMLElement>('[data-testid="connect-ci-details"]')!;
    expect(alert.textContent).toContain('That token name is reserved.');
    expect(alert.textContent).toContain('Token name:');
    expect(alert.textContent).not.toContain('/name');

    // An edited field that is still invalid stays invalid, now for its own reason.
    await fill({ 'connect-token-name': '' });
    expect(field('connect-token-name').getAttribute('aria-invalid')).toBe('true');
    expect(root.querySelector('#connect-token-name-error')?.textContent).toContain(
      'Enter a token name',
    );
    for (const described of root.querySelectorAll<HTMLElement>('[aria-describedby]')) {
      for (const id of (described.getAttribute('aria-describedby') ?? '').split(/\s+/)) {
        if (id) expect(root.querySelector(`#${id}`), `${id} must be rendered`).not.toBeNull();
      }
    }
  });

  it('associates initial hints without an error id before validation or refusal', () => {
    expect(field('connect-token-name').getAttribute('aria-describedby')).toBe(
      'connect-token-name-hint',
    );
  });

  it.each(['', '   ', 'a'.repeat(129)])(
    'focuses a linked token-name summary after submit',
    async (value) => {
      await fill({ 'connect-token-name': value });
      const submitButton = root.querySelector<HTMLButtonElement>(
        '[data-testid="connect-token-submit"]',
      );
      submitButton?.focus();
      const form = root.querySelector('[data-testid="connect-token-panel"] form');
      form?.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
      await fixture.whenStable();
      const summary = root.querySelector('#connect-token-errors')!;
      expect(document.activeElement).toBe(summary);
      summary.querySelector<HTMLAnchorElement>('a[href="#connect-token-name"]')!.click();
      expect(document.activeElement).toBe(field('connect-token-name'));
      expect(field('connect-token-name').getAttribute('aria-invalid')).toBe('true');
      expect(root.querySelector('#connect-token-name-error')?.textContent).toContain(
        'Enter a token name',
      );
      expect(createIngestToken).not.toHaveBeenCalled();
    },
  );

  it('refresh keeps the focus the visitor moved to while the re-read ran', async () => {
    refreshFailure.set('unavailable');
    await fixture.whenStable();
    const elsewhere = document.createElement('button');
    document.body.append(elsewhere);
    refresh.mockImplementationOnce(() => {
      // The visitor moved on while the read was running: that focus is theirs to keep.
      elsewhere.focus();
      refreshFailure.set(null);
      return Promise.resolve();
    });
    const pressed = retryButton();
    pressed.focus();
    pressed.click();
    await settle();
    expect(root.querySelector('[data-testid="connect-ci-refresh-failed"]')).toBeNull();
    expect(document.activeElement).toBe(elsewhere);
    elsewhere.remove();
  });

  it('releases the re-read focus listener when the page is destroyed mid-read', async () => {
    refreshFailure.set('unavailable');
    await fixture.whenStable();
    retryButton().focus();
    const removed = vi.spyOn(document, 'removeEventListener');
    const focusRemovals = (): number =>
      removed.mock.calls.filter((call) => call[0] === 'focusin').length;
    let finishRead: () => void = () => undefined;
    refresh.mockImplementationOnce(
      () =>
        new Promise<void>((resolve) => {
          finishRead = resolve;
        }),
    );
    try {
      retryButton().click();
      // Still watching while the read is in flight: the release belongs to the settle paths.
      expect(focusRemovals()).toBe(0);
      fixture.destroy();
      // The pass that would spend the place never runs, so the destroy path owes the release.
      expect(focusRemovals()).toBe(1);
      finishRead();
      await new Promise<void>((resolve) => {
        setTimeout(resolve, 0);
      });
      // Released exactly once, and the read that settled on a destroyed page puts nothing back.
      expect(focusRemovals()).toBe(1);
    } finally {
      removed.mockRestore();
    }
  });

  it('starts no re-read when the page is already gone', async () => {
    refreshFailure.set('unavailable');
    await fixture.whenStable();
    // `refresh` is protected: read it reflectively instead of widening the page's API for a test.
    const refreshPage = Reflect.get(fixture.componentInstance, 'refresh') as () => Promise<void>;
    const page = fixture.componentInstance;
    fixture.destroy();
    await refreshPage.call(page);
    // Nothing was captured, so nothing has to be released: no listener outlives the page.
    expect(refresh).not.toHaveBeenCalled();
  });
});
