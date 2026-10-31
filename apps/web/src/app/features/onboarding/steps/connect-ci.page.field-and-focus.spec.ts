// A refusal's field reasons clear as the visitor edits them, and a re-read keeps the place they
// were on. Both belong to the same answer: what the screen still says, and where the stop is.
import { computed, provideZonelessChangeDetection, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import type { CiBindingView, IngestTokenView } from '@evidgate/sdk';

import { OnboardingDataService } from '../../../core/data/onboarding-data.service';
import { ApiRequestError, type ApiError } from '../../../core/http/api-error';
import { SessionStore } from '../../../core/session/session.store';
import { OnboardingStore } from '../onboarding.store';
import { ConnectCiPage } from './connect-ci.page';

const BINDING: CiBindingView = {
  id: 'binding-1',
  project_id: 'project-1',
  provider: 'github_actions',
  repository_id: 123,
  repository_full_name: 'acme/web',
  allowed_ref_patterns: ['refs/heads/main'],
  allowed_workflow_refs: ['acme/web/.github/workflows/ci.yml@refs/heads/main'],
  status: 'pending_verification',
  created_at: '2026-10-25T00:00:00Z',
  version: 1,
};
const TOKEN: IngestTokenView = {
  id: 'token-1',
  project_id: 'project-1',
  name: 'Build upload',
  prefix: 'egt_prefix',
  status: 'active',
  scopes: ['event:write'],
  expires_at: '2026-11-25T00:00:00Z',
  last_used_at: null,
  created_at: '2026-10-25T00:00:00Z',
};

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
  const bindings = signal<readonly CiBindingView[]>([]);
  const tokens = signal<readonly IngestTokenView[]>([]);
  const refreshFailure = signal<'unavailable' | null>(null);
  const createCiBinding = vi.fn<OnboardingDataService['createCiBinding']>();
  const refresh = vi.fn<(orgId: string, projectId: string) => Promise<void>>();

  beforeEach(async () => {
    permitted.set(true);
    bindings.set([]);
    tokens.set([]);
    refreshFailure.set(null);
    createCiBinding
      .mockReset()
      .mockResolvedValue({ binding: BINDING, binding_challenge: 'challenge-shown-once' });
    // The store clears its refresh failure on success, which is what takes the retry notice away.
    refresh.mockReset().mockImplementation(() => {
      refreshFailure.set(null);
      return Promise.resolve();
    });
    await TestBed.configureTestingModule({
      imports: [ConnectCiPage],
      providers: [
        provideZonelessChangeDetection(),
        {
          provide: OnboardingDataService,
          useValue: { createCiBinding, createIngestToken: vi.fn(), rotateIngestToken: vi.fn() },
        },
        {
          provide: OnboardingStore,
          useValue: {
            bindings,
            tokens,
            bindingsTruncated: signal(false),
            tokensTruncated: signal(false),
            project: signal<{ readonly slug: string } | null>({ slug: 'acme-web' }),
            done: computed(() => ({ 2: bindings().length + tokens().length > 0 })),
            canGoNext: computed(() => bindings().length + tokens().length > 0),
            refreshFailure,
            refresh,
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

  async function fillOidc(): Promise<void> {
    await fill({
      'connect-oidc-repo-id': '123',
      'connect-oidc-repo-name': ' acme/web ',
      'connect-oidc-refs': ' refs/heads/main \n',
      'connect-oidc-workflows': ' acme/web/.github/workflows/ci.yml@refs/heads/main \n',
    });
  }

  async function submit(): Promise<void> {
    root
      .querySelector('[data-testid="connect-oidc-panel"] form')!
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

  it('clears only the edited field server reason on edit', async () => {
    createCiBinding.mockRejectedValueOnce(
      refusal([
        { path: '/repository_id', reason: 'No such repository ID.' },
        { path: '/repository_full_name', reason: 'That repository is not visible to the app.' },
      ]),
    );
    await fillOidc();
    await submit();
    expect(field('connect-oidc-repo-id').getAttribute('aria-invalid')).toBe('true');
    expect(field('connect-oidc-repo-name').getAttribute('aria-invalid')).toBe('true');

    await fill({ 'connect-oidc-repo-name': ' acme/api ' });
    // The edited field answers for itself: its reason and its invalidity go.
    expect(field('connect-oidc-repo-name').getAttribute('aria-invalid')).toBeNull();
    expect(field('connect-oidc-repo-name').hasAttribute('aria-describedby')).toBe(false);
    expect(root.querySelector('#connect-repo-name-error')?.textContent?.trim()).toBe('');
    // The field the visitor did not touch keeps its reason, and the alert keeps the rows it spoke.
    expect(field('connect-oidc-repo-id').getAttribute('aria-invalid')).toBe('true');
    expect(field('connect-oidc-repo-id').getAttribute('aria-describedby')).toBe(
      'connect-repo-id-error',
    );
    expect(root.querySelector('#connect-repo-id-error')?.textContent).toContain(
      'No such repository ID.',
    );
    const alert = root.querySelector<HTMLElement>('[data-testid="connect-ci-details"]')!;
    expect(alert.textContent).toContain('That repository is not visible to the app.');

    // An edited field that is still invalid stays invalid, now for its own reason.
    await fill({ 'connect-oidc-repo-id': 'abc' });
    expect(field('connect-oidc-repo-id').getAttribute('aria-invalid')).toBe('true');
    expect(root.querySelector('#connect-repo-id-error')?.textContent).toContain(
      'positive whole-number',
    );
    for (const described of root.querySelectorAll<HTMLElement>('[aria-describedby]')) {
      for (const id of (described.getAttribute('aria-describedby') ?? '').split(/\s+/)) {
        if (id) expect(root.querySelector(`#${id}`), `${id} must be rendered`).not.toBeNull();
      }
    }
  });

  it('associates a field error id only while that reason is shown', () => {
    // Nothing is refused yet, so nothing describes a control with an error nobody renders text in.
    expect(field('connect-oidc-repo-id').hasAttribute('aria-describedby')).toBe(false);
    expect(field('connect-oidc-repo-name').hasAttribute('aria-describedby')).toBe(false);
    expect(field('connect-token-name').hasAttribute('aria-describedby')).toBe(false);
    expect(field('connect-oidc-refs').getAttribute('aria-describedby')).toBe('connect-refs-hint');
    expect(field('connect-oidc-workflows').getAttribute('aria-describedby')).toBe(
      'connect-workflows-hint',
    );
  });

  it('refresh keeps focus when focused control still enabled', async () => {
    refreshFailure.set('unavailable');
    await fixture.whenStable();
    const nameField = field('connect-token-name');
    nameField.focus();
    // The press does not move the focus, so the visitor's place is still the field they typed in.
    retryButton().click();
    await settle();
    expect(refresh).toHaveBeenCalledWith('org-1', 'project-1');
    // The notice the press came from is gone, and the field the visitor was in keeps the place.
    expect(root.querySelector('[data-testid="connect-ci-refresh-failed"]')).toBeNull();
    expect(document.activeElement).toBe(nameField);
  });

  it('refresh restores focus when focused control disabled', async () => {
    tokens.set([TOKEN]);
    refreshFailure.set('unavailable');
    await fixture.whenStable();
    const rotate = root.querySelector<HTMLButtonElement>('[data-testid="connect-token-rotate"]')!;
    rotate.focus();
    refresh.mockImplementationOnce(() => {
      // The read took the permission the control was usable with, so the place it held is gone.
      permitted.set(false);
      refreshFailure.set(null);
      return Promise.resolve();
    });
    retryButton().click();
    await settle();
    expect(rotate.disabled).toBe(true);
    // A token-row control belongs to the token panel, so that panel's heading takes the place.
    expect(document.activeElement).toBe(root.querySelector('#connect-token-heading'));
  });

  it('refresh keeps the focus the visitor moved to while the re-read ran', async () => {
    refreshFailure.set('unavailable');
    await fixture.whenStable();
    const nameField = field('connect-token-name');
    refresh.mockImplementationOnce(() => {
      // The visitor moved on while the read was running: that focus is theirs to keep.
      nameField.focus();
      refreshFailure.set(null);
      return Promise.resolve();
    });
    const pressed = retryButton();
    pressed.focus();
    pressed.click();
    await settle();
    expect(root.querySelector('[data-testid="connect-ci-refresh-failed"]')).toBeNull();
    expect(document.activeElement).toBe(nameField);
  });

  it('releases the re-read focus listener when the page is destroyed mid-read', async () => {
    refreshFailure.set('unavailable');
    await fixture.whenStable();
    field('connect-token-name').focus();
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
      expect(document.activeElement).toBe(field('connect-token-name'));
    } finally {
      removed.mockRestore();
    }
  });

  it('keeps the focus the visitor moved when the read disables the pressed control', async () => {
    tokens.set([TOKEN]);
    refreshFailure.set('unavailable');
    await fixture.whenStable();
    const rotate = root.querySelector<HTMLButtonElement>('[data-testid="connect-token-rotate"]')!;
    const elsewhere = document.createElement('button');
    document.body.append(elsewhere);
    try {
      refresh.mockImplementationOnce(() => {
        // The visitor moved on before the read took the permission the control was usable with.
        elsewhere.focus();
        permitted.set(false);
        refreshFailure.set(null);
        return Promise.resolve();
      });
      rotate.focus();
      retryButton().click();
      await settle();
      expect(rotate.disabled).toBe(true);
      // A focus the visitor moved themselves is never taken back to the panel heading.
      expect(document.activeElement).toBe(elsewhere);
    } finally {
      elsewhere.remove();
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

  it('leaves the visitor alone when the read takes the place they had moved to', async () => {
    tokens.set([TOKEN]);
    refreshFailure.set('unavailable');
    await fixture.whenStable();
    const rotate = root.querySelector<HTMLButtonElement>('[data-testid="connect-token-rotate"]')!;
    const tokenHeading = root.querySelector('#connect-token-heading');
    const elsewhere = document.createElement('button');
    document.body.append(elsewhere);
    try {
      refresh.mockImplementationOnce(() => {
        // The visitor moved on and the read then took that place too, leaving them on `<body>`:
        // their own move is not a place this handoff is owed, so no heading takes it back.
        elsewhere.focus();
        permitted.set(false);
        elsewhere.remove();
        refreshFailure.set(null);
        return Promise.resolve();
      });
      rotate.focus();
      retryButton().click();
      await settle();
      expect(rotate.disabled).toBe(true);
      expect(document.activeElement).toBe(document.body);
      expect(document.activeElement).not.toBe(tokenHeading);
    } finally {
      elsewhere.remove();
    }
  });
});
