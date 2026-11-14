// Both server-confirmed paths, refusal and validation boundaries.
import type { ComponentFixture } from '@angular/core/testing';
import type { CiBindingCreatedResponse } from '@evidgate/sdk';

import type { RevealSlot } from '../../../shared/ui/reveal/one-time-reveal.slot';
import type { ConnectCiPage } from './connect-ci.page';
import {
  BINDING,
  TOKEN,
  CHALLENGE,
  SECRET,
  createConnectionPageContext,
  failure,
} from './connect-ci.page.test-support';

describe('ConnectCiPage', () => {
  const context = createConnectionPageContext();
  const {
    bindings,
    tokens,
    refreshFailure,
    canGoNext,
    createCiBinding,
    createIngestToken,
    rotateIngestToken,
    refresh,
    fill,
    fillOidc,
    submit,
  } = context;
  let fixture: ComponentFixture<ConnectCiPage>;
  let root: HTMLElement;
  let slot: RevealSlot;

  beforeEach(async () => {
    await context.mount();
    fixture = context.fixture;
    root = context.root;
    slot = context.slot;
  });

  it('creates OIDC once with the exact numeric/trimmed SDK body, reveals, then refreshes', async () => {
    expect(canGoNext()).toBe(false);
    refresh.mockImplementationOnce(() => {
      expect(slot.current()).toEqual({ source: 'binding-challenge', value: CHALLENGE });
      expect(canGoNext()).toBe(false);
      bindings.set([BINDING]);
      return Promise.resolve();
    });
    await fillOidc();
    await submit('oidc');
    expect(createCiBinding).toHaveBeenCalledTimes(1);
    expect(createCiBinding).toHaveBeenCalledWith('project-1', {
      provider: 'github_actions',
      repository_id: 123,
      repository_full_name: 'acme/web',
      allowed_ref_patterns: ['refs/heads/main', 'refs/tags/v*'],
      allowed_workflow_refs: ['acme/web/.github/workflows/ci.yml@refs/heads/main'],
    });
    expect(refresh).toHaveBeenCalledWith('org-1', 'project-1');
    expect(canGoNext()).toBe(true);
    expect(
      root.querySelector('[data-testid="connect-binding-row"]')?.getAttribute('data-status'),
    ).toBe('pending_verification');
    expect(root.textContent).not.toMatch(/\bPASS\b|\bFAIL\b|Binding verified/);
  });

  it('creates an ingest token with name only and enables Next through the same refresh', async () => {
    expect(canGoNext()).toBe(false);
    refresh.mockImplementationOnce(() => {
      expect(slot.current()).toEqual({ source: 'ingest-token', value: SECRET });
      expect(canGoNext()).toBe(false);
      tokens.set([TOKEN]);
      return Promise.resolve();
    });
    await fill({ 'connect-token-name': ' Build upload ' });
    await submit('token');
    expect(createIngestToken).toHaveBeenCalledExactlyOnceWith('project-1', {
      name: 'Build upload',
    });
    expect(refresh).toHaveBeenCalledWith('org-1', 'project-1');
    expect(canGoNext()).toBe(true);
    expect(
      root.querySelector<HTMLTextAreaElement | HTMLInputElement>('[data-testid="reveal-value"]')
        ?.value,
    ).toBe(SECRET);
  });

  it.each(['', '0', '-1', '1.5', 'abc', '9007199254740992'])(
    'rejects invalid repository ID %j inline',
    async (id) => {
      await fillOidc({ 'connect-oidc-repo-id': id });
      await submit('oidc');
      expect(createCiBinding).not.toHaveBeenCalled();
      expect(root.querySelector('#connect-oidc-repo-id-error')?.textContent?.trim()).not.toBe('');
      expect(root.querySelector('#connect-oidc-repo-id')?.getAttribute('aria-invalid')).toBe(
        'true',
      );
    },
  );

  it('accepts the maximum safe integer repository ID', async () => {
    await fillOidc({ 'connect-oidc-repo-id': '9007199254740991' });
    await submit('oidc');
    expect(createCiBinding.mock.calls[0]?.[1].repository_id).toBe(9007199254740991);
    expect(root.querySelector('#connect-oidc-repo-id')?.getAttribute('aria-invalid')).toBeNull();
  });

  it('marks a refused field invalid and names its rendered reason, which is not a live region', async () => {
    createCiBinding.mockRejectedValueOnce(
      failure('validation', 422, {
        details: [
          { path: '/repository_full_name', reason: 'That repository is not visible to the app.' },
        ],
      }),
    );
    await fillOidc();
    await submit('oidc');
    const input = root.querySelector<HTMLInputElement>('#connect-oidc-repo-name')!;
    const reason = root.querySelector('#connect-oidc-repo-name-error')!;
    expect(reason.textContent).toContain('That repository is not visible to the app.');
    expect(input.getAttribute('aria-invalid')).toBe('true');
    expect(input.getAttribute('aria-describedby')).toBe(
      'connect-oidc-repo-name-hint connect-oidc-repo-name-error',
    );
    // The details alert says the same sentence; a live region here would say it twice.
    expect(reason.hasAttribute('aria-live')).toBe(false);
    for (const field of root.querySelectorAll<HTMLElement>('[aria-describedby]')) {
      for (const id of (field.getAttribute('aria-describedby') ?? '').split(/\s+/)) {
        if (id) expect(root.querySelector(`#${id}`), `${id} must be rendered`).not.toBeNull();
      }
    }
  });

  it('holds focus on the waiting line during the command, then on the refusal heading', async () => {
    let rejectPending: (error: unknown) => void = () => undefined;
    createCiBinding.mockImplementationOnce(
      () =>
        new Promise((_resolve, reject) => {
          rejectPending = reject;
        }),
    );
    await fillOidc();
    await submit('oidc');
    expect(document.activeElement).toBe(root.querySelector('[data-testid="connect-ci-busy"]'));
    rejectPending(failure('validation', 422));
    // The refusal renders after the command's own macrotask turn; the heading's effect runs then.
    await new Promise<void>((resolve) => {
      setTimeout(resolve, 0);
    });
    await fixture.whenStable();
    expect(document.activeElement).toBe(root.querySelector('#connect-failure-heading'));
  });

  it('strips control and format characters from server paths and reasons before display', async () => {
    createIngestToken.mockRejectedValueOnce(
      failure('validation', 422, {
        details: [
          {
            path: '/name\u0007\u202e',
            reason: 'Name \u0000must\u200f not embed\u2028 separators.',
          },
        ],
      }),
    );
    await fill({ 'connect-token-name': 'Build upload' });
    await submit('token');
    const details = root.querySelector('[data-testid="connect-ci-details"]')!;
    expect(details.textContent).toContain('Token name:');
    expect(details.textContent).not.toContain('/name');
    expect(details.textContent).toContain('Name must not embed separators.');
    // The row still names its field, so the reason also appears beside that control.
    expect(root.querySelector('#connect-token-name-error')?.textContent).toContain(
      'Name must not embed separators.',
    );
    expect(root.querySelector('#connect-token-name')?.getAttribute('aria-invalid')).toBe('true');
    expect(root.textContent).not.toMatch(/[\u0000\u0007\u200f\u202e\u2028\u2029]/);
  });

  it('leaves no refusal cue behind once the refusal that armed it clears', async () => {
    createIngestToken.mockRejectedValueOnce(failure('validation', 422));
    await fill({ 'connect-token-name': 'Build upload' });
    await submit('token');
    await new Promise<void>((resolve) => {
      setTimeout(resolve, 0);
    });
    await fixture.whenStable();
    expect(document.activeElement).toBe(root.querySelector('#connect-failure-heading'));
    // Starting the next command clears the refusal, and with it the heading the cue pointed at.
    await submit('token');
    await new Promise<void>((resolve) => {
      setTimeout(resolve, 0);
    });
    await fixture.whenStable();
    expect(root.querySelector('#connect-failure-heading')).toBeNull();
    const revealValue = root.querySelector('[data-testid="reveal-value"]');
    expect(document.activeElement).toBe(
      root.querySelector(`#${revealValue?.getAttribute('aria-labelledby')}`),
    );
  });

  it('consumes a refusal cue whose heading never renders', async () => {
    refresh.mockImplementationOnce(() => {
      // The reconcile answers under a different scope, so the refusal never reaches the screen.
      fixture.componentRef.setInput('projectId', 'project-2');
      return Promise.resolve();
    });
    createIngestToken.mockRejectedValueOnce(failure('timeout', 0));
    await fill({ 'connect-token-name': 'Build upload' });
    await submit('token');
    await new Promise<void>((resolve) => {
      setTimeout(resolve, 0);
    });
    await fixture.whenStable();
    expect(root.querySelector('[data-testid="connect-ci-error"]')).toBeNull();
    // Nothing was rendered to move to: the visitor stays on the waiting line the press left.
    expect(document.activeElement).toBe(root.querySelector('[data-testid="connect-ci-busy"]'));
    // The spent cue cannot fire on a later render; the next refusal arms and moves focus once.
    createIngestToken.mockRejectedValueOnce(failure('validation', 422));
    await fill({ 'connect-token-name': 'Build upload' });
    await submit('token');
    await new Promise<void>((resolve) => {
      setTimeout(resolve, 0);
    });
    await fixture.whenStable();
    expect(document.activeElement).toBe(root.querySelector('#connect-failure-heading'));
  });

  it('returns focus to the first panel heading when a re-read removes the retry notice', async () => {
    refreshFailure.set('unavailable');
    await fixture.whenStable();
    const retry = root.querySelector<HTMLButtonElement>(
      '[data-testid="connect-ci-refresh-failed"] button',
    )!;
    // Not natively disabled: the press keeps the focus it was made with while the re-read runs.
    expect(retry.disabled).toBe(false);
    retry.focus();
    retry.click();
    await new Promise<void>((resolve) => {
      setTimeout(resolve, 0);
    });
    await fixture.whenStable();
    expect(root.querySelector('[data-testid="connect-ci-refresh-failed"]')).toBeNull();
    // The notice held the only stop the visitor was on; the first panel below it takes it back.
    expect(document.activeElement).toBe(root.querySelector('#connect-oidc-heading'));
  });

  it.each(['connect-oidc-refs', 'connect-oidc-workflows'])(
    'rejects zero and over 32 lines in %s',
    async (id) => {
      for (const value of [' \n ', Array.from({ length: 33 }, (_, i) => `ref-${i}`).join('\n')]) {
        await fillOidc({ [id]: value });
        await submit('oidc');
        expect(createCiBinding).not.toHaveBeenCalled();
        const field = root.querySelector(`[data-testid="${id}"]`)!;
        expect(field.getAttribute('aria-invalid')).toBe('true');
        const errorId = field.getAttribute('aria-describedby')!.split(' ').at(-1);
        expect(root.querySelector(`#${errorId}`)?.textContent?.trim()).not.toBe('');
      }
    },
  );

  it('accepts the 32-line boundary and rejects an invalid owner/repo', async () => {
    await fillOidc({ 'connect-oidc-repo-name': 'invalid' });
    await submit('oidc');
    expect(createCiBinding).not.toHaveBeenCalled();
    expect(root.querySelector('#connect-oidc-repo-name')?.getAttribute('aria-invalid')).toBe(
      'true',
    );
    await fillOidc({
      'connect-oidc-refs': Array.from({ length: 32 }, (_, i) => `refs/heads/branch-${i}`).join(
        '\n',
      ),
    });
    await submit('oidc');
    expect(createCiBinding.mock.calls[0]?.[1].allowed_ref_patterns).toHaveLength(32);
  });

  it.each(['', '   ', 'n'.repeat(129), '\u{10400}'.repeat(129)])(
    'blocks invalid token name (%j)',
    async (name) => {
      await fill({ 'connect-token-name': name });
      await submit('token');
      expect(createIngestToken).not.toHaveBeenCalled();
      expect(root.querySelector('#connect-token-name')?.getAttribute('aria-invalid')).toBe('true');
      expect(root.querySelector('#connect-token-name-error')?.textContent?.trim()).not.toBe('');
    },
  );

  it.each(['n'.repeat(128), '\u{10400}'.repeat(128), '\u{10400}'.repeat(65)])(
    'accepts a token name of %s using Unicode code-point length',
    async (name) => {
      await fill({ 'connect-token-name': name });
      await submit('token');
      expect(createIngestToken).toHaveBeenCalledWith('project-1', { name });
      expect(root.querySelector('#connect-token-name')?.hasAttribute('maxlength')).toBe(false);
      expect(root.querySelector('#connect-token-name')?.getAttribute('aria-invalid')).toBeNull();
    },
  );

  it('allows only one in-flight command across both panels and rotation', async () => {
    let resolvePending!: (response: CiBindingCreatedResponse) => void;
    createCiBinding.mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          resolvePending = resolve;
        }),
    );
    tokens.set([TOKEN]);
    await fillOidc();
    await fill({ 'connect-token-name': 'Build upload' });
    await submit('oidc');
    await submit('oidc');
    await submit('token');
    for (const id of ['connect-oidc-submit', 'connect-token-submit', 'connect-token-rotate']) {
      expect(root.querySelector<HTMLButtonElement>(`[data-testid="${id}"]`)?.disabled).toBe(true);
    }
    expect(createCiBinding).toHaveBeenCalledTimes(1);
    expect(createIngestToken).not.toHaveBeenCalled();
    expect(rotateIngestToken).not.toHaveBeenCalled();
    resolvePending({ binding: BINDING, binding_challenge: CHALLENGE });
    await fixture.whenStable();
  });

  it.each([
    ['conflict', 409],
    ['forbidden', 403],
    ['not_found', 404],
    ['validation', 422],
    ['rate_limited', 429],
  ] as const)(
    'renders %s verbatim, re-enables controls and never auto-retries',
    async (kind, status) => {
      createIngestToken.mockRejectedValueOnce(failure(kind, status));
      await fill({ 'connect-token-name': 'Build upload' });
      await submit('token');
      const error = root.querySelector('[data-testid="connect-ci-error"]');
      expect(error?.textContent).toContain('Exact server refusal, unchanged.');
      expect(error?.textContent?.split('Exact server refusal, unchanged.')).toHaveLength(2);
      // Two alerts, each with its own sentence: the shared pattern announces the refusal title,
      // the page's labelled block announces the field rows (which the pattern never renders).
      expect(error?.querySelectorAll('[role="alert"]')).toHaveLength(2);
      expect(error?.querySelector('[role="alert"] [role="alert"]')).toBeNull();
      // The rows the server refused are spoken from one labelled alert, not bare paragraphs.
      const details = error?.querySelector<HTMLElement>('[data-testid="connect-ci-details"]');
      expect(details?.getAttribute('role')).toBe('alert');
      const detailsLabel = details?.getAttribute('aria-labelledby') ?? '';
      expect(root.querySelector(`#${detailsLabel}`)?.textContent).toContain('The server refused');
      expect(error?.querySelector('button')).toBeNull();
      expect(error?.querySelector('[data-testid="error-request-id"]')?.textContent).toContain(
        'request-1',
      );
      // The step shell owns the h2; the page's refusal heading sits below it and the pattern's
      // title one level below that, outside every alert so it can take focus.
      const pageHeading = error?.querySelector('h3.connect__failure-heading');
      expect(pageHeading).not.toBeNull();
      // The heading renders the class field's copy, never the template ref it is bound to.
      expect(pageHeading?.textContent?.trim()).toBe('The connection was not saved');
      expect(pageHeading?.closest('[role="alert"]')).toBeNull();
      expect(error?.querySelector('h4.state-panel__title')).not.toBeNull();
      expect(error?.querySelector('h2')).toBeNull();
      expect(error?.textContent).toContain('GitHub numeric repository ID: Exact field reason.');
      expect(error?.textContent).not.toContain('/repository_id');
      expect(error?.textContent).toContain('Exact field reason.');
      expect(
        root.querySelector<HTMLButtonElement>('[data-testid="connect-token-submit"]')?.disabled,
      ).toBe(false);
      expect(createIngestToken).toHaveBeenCalledTimes(1);
      expect(refresh).not.toHaveBeenCalled();
    },
  );
});
