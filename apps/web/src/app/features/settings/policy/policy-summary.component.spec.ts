// The Policy tab's project summary: one state per read, and one retry control for both. Renders the
// states `policy-reads.spec.ts` settles to, so the copy and the hooks the browser suite uses
// (`data-policy-*`) are pinned here — including that the control is inert rather than disabled
// while a read runs, keeps the focus its own press gave it, and says when the server's wait runs.

import { TestBed } from '@angular/core/testing';
import type { PolicyActivationStateDto, PolicyRevisionSummaryDto } from '@evidgate/sdk';

import type { PolicyReadView } from './policy-reads';
import { rootElement } from './policy-reads.test-support';
import { PolicySummaryComponent } from './policy-summary.component';

const ACTIVATION: PolicyActivationStateDto = {
  project_id: '018f0000-0000-7000-8000-000000000011',
  policy_revision_id: '018f0000-0000-7000-8000-00000000a004',
  hash: 'sha256:f250bbce48fca2a9a5f93b45236c0f330bb471f5d3f0d1396ddc5ac964928064',
  mode: 'advisory',
  activated_by: '018f0000-0000-7000-8000-0000000000e1',
  activated_at: '2026-10-26T08:22:00Z',
};

const HEAD: PolicyRevisionSummaryDto = {
  id: '018f0000-0000-7000-8000-00000000a003',
  revision_no: 3,
  hash: 'sha256:d05c091a94bfe1c59087f5a38ef026b200d2edcbee0fab69a8d080fb461e377e',
  state: 'active',
  created_by: '018f0000-0000-7000-8000-0000000000e1',
  created_at: '2026-10-08T06:45:00Z',
  version: 2,
};

const IDLE_ACTIVATION: PolicyReadView<PolicyActivationStateDto> = {
  phase: 'idle',
  value: null,
  error: null,
};
const IDLE_HEAD: PolicyReadView<PolicyRevisionSummaryDto> = {
  phase: 'idle',
  value: null,
  error: null,
};
const READY_ACTIVATION: PolicyReadView<PolicyActivationStateDto> = {
  phase: 'ready',
  value: ACTIVATION,
  error: null,
};
const READY_HEAD: PolicyReadView<PolicyRevisionSummaryDto> = {
  phase: 'ready',
  value: HEAD,
  error: null,
};

describe('PolicySummaryComponent', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
  });

  async function render(
    activation: PolicyReadView<PolicyActivationStateDto>,
    head: PolicyReadView<PolicyRevisionSummaryDto>,
    gates: { busy?: boolean; waiting?: boolean } = {},
  ) {
    const fixture = TestBed.createComponent(PolicySummaryComponent);
    fixture.componentRef.setInput('activation', activation);
    fixture.componentRef.setInput('head', head);
    fixture.componentRef.setInput('busy', gates.busy ?? false);
    fixture.componentRef.setInput('waiting', gates.waiting ?? false);
    fixture.detectChanges();
    await fixture.whenStable();
    return { fixture, root: rootElement(fixture) };
  }

  /** A typed 429 with the server's interval, the only failure that opens a wait. */
  const RATE_LIMITED = {
    kind: 'rate_limited' as const,
    status: 429,
    code: 'rate_limited',
    message: 'the read burst limit for this project was reached',
    requestId: null,
    retryable: true,
    details: [],
    retryAfterSeconds: 30,
  };

  it('renders nothing before either read starts', async () => {
    const { root } = await render(IDLE_ACTIVATION, IDLE_HEAD);
    expect(root.querySelector('[data-policy-summary]')).toBeNull();
  });

  it('shows the mode, the active revision and the head revision', async () => {
    const { root } = await render(READY_ACTIVATION, READY_HEAD);
    const chip = root.querySelector('[data-policy-mode]');
    expect(chip?.getAttribute('data-mode')).toBe('advisory');
    expect(
      root.querySelector('[data-policy-activation-revision] .identifier-accessible')?.textContent,
    ).toContain(ACTIVATION.policy_revision_id);
    expect(
      root
        .querySelector('[data-policy-activation-state]')
        ?.getAttribute('data-policy-activation-state'),
    ).toBe('ready');
    expect(root.querySelector('[data-policy-head-no]')?.textContent).toContain(
      String(HEAD.revision_no),
    );
    expect(
      root.querySelector('[data-policy-head-read] .identifier-accessible')?.textContent,
    ).toContain(HEAD.id);
    expect(root.querySelector('[data-policy-activation] time')?.getAttribute('datetime')).toBe(
      ACTIVATION.activated_at,
    );
    expect(root.querySelector('[data-policy-activation] time')?.textContent).toContain('UTC');
    expect(root.querySelector('[data-policy-reads-error]')).toBeNull();
  });

  it('shows the gating chip for an enforcing activation', async () => {
    const { root } = await render(
      { phase: 'ready', value: { ...ACTIVATION, mode: 'enforcing' }, error: null },
      READY_HEAD,
    );
    expect(root.querySelector('[data-policy-mode]')?.getAttribute('data-mode')).toBe('enforcing');
  });

  it('says the project has no activation, and still shows the head revision', async () => {
    const { root } = await render({ phase: 'empty', value: null, error: null }, READY_HEAD);
    expect(
      root
        .querySelector('[data-policy-activation-state]')
        ?.getAttribute('data-policy-activation-state'),
    ).toBe('empty');
    expect(root.querySelector('[data-policy-activation-empty]')?.textContent).toMatch(/\S/);
    expect(root.querySelector('[data-policy-head-no]')).not.toBeNull();
  });

  it('says the activation cannot be read, and offers no retry for that refusal', async () => {
    const { root } = await render(
      {
        phase: 'forbidden',
        value: null,
        error: {
          kind: 'forbidden',
          status: 403,
          code: 'forbidden',
          message: 'the principal is not allowed to perform this operation',
          requestId: null,
          retryable: false,
          details: [],
          retryAfterSeconds: null,
        },
      },
      READY_HEAD,
    );
    expect(root.querySelector('[data-policy-activation-forbidden]')?.textContent).toMatch(/\S/);
    expect(root.querySelector('[data-policy-reads-retry]')).toBeNull();
  });

  it('says the project has no revisions when the head read answers 404', async () => {
    const { root } = await render(READY_ACTIVATION, { phase: 'empty', value: null, error: null });
    expect(
      root.querySelector('[data-policy-head-state]')?.getAttribute('data-policy-head-state'),
    ).toBe('empty');
    expect(root.querySelector('[data-policy-head-empty]')?.textContent).toMatch(/\S/);
    expect(root.querySelector('[data-policy-reads-retry]')).toBeNull();
  });

  it.each(['activation', 'head'] as const)(
    'shows the server refusal and a retry when the %s read failed',
    async (failed) => {
      const error = {
        kind: 'rate_limited' as const,
        status: 429,
        code: 'rate_limited',
        message: 'the read burst limit for this project was reached',
        requestId: null,
        retryable: true,
        details: [],
        retryAfterSeconds: 30,
      };
      const { fixture, root } = await render(
        failed === 'activation' ? { phase: 'error', value: null, error } : READY_ACTIVATION,
        failed === 'head' ? { phase: 'error', value: null, error } : READY_HEAD,
      );
      const region = root.querySelector('[data-policy-reads-error]');
      expect(region?.textContent).toContain(error.code);
      expect(region?.textContent).toContain(error.message);
      const retry = region?.querySelector<HTMLButtonElement>('[data-policy-reads-retry]');
      expect(retry).not.toBeNull();
      const emitted = vi.fn();
      fixture.componentInstance.retry.subscribe(emitted);
      retry?.click();
      expect(emitted).toHaveBeenCalledTimes(1);
    },
  );

  it('shows each read loading until its own answer lands', async () => {
    const { root } = await render({ phase: 'loading', value: null, error: null }, READY_HEAD);
    expect(
      root
        .querySelector('[data-policy-activation-state]')
        ?.getAttribute('data-policy-activation-state'),
    ).toBe('loading');
    expect(root.querySelector('[data-policy-activation] [role="status"]')?.textContent).toMatch(
      /\S/,
    );
    expect(root.querySelector('[data-policy-head-no]')).not.toBeNull();
  });

  it('ignores a press while a read is in flight, and keeps the control on screen', async () => {
    const { fixture, root } = await render(READY_ACTIVATION, {
      phase: 'error',
      value: null,
      error: RATE_LIMITED,
    });
    const emitted = vi.fn();
    fixture.componentInstance.retry.subscribe(emitted);

    fixture.componentRef.setInput('busy', true);
    fixture.detectChanges();
    const retry = root.querySelector<HTMLButtonElement>('[data-policy-reads-retry]');
    expect(retry?.getAttribute('aria-disabled')).toBe('true');
    // Inert, not disabled: a disabled control drops the focus of the press that started the read.
    expect(retry?.disabled).toBe(false);
    retry?.click();
    await fixture.whenStable();
    expect(emitted).not.toHaveBeenCalled();
  });

  it('keeps the retry closed and says so while the server’s wait runs', async () => {
    const { fixture, root } = await render(
      READY_ACTIVATION,
      { phase: 'error', value: null, error: RATE_LIMITED },
      { waiting: true },
    );
    const waiting = root.querySelector('[data-policy-reads-waiting]');
    expect(waiting?.textContent).toMatch(/\S/);
    const emitted = vi.fn();
    fixture.componentInstance.retry.subscribe(emitted);
    const retry = root.querySelector<HTMLButtonElement>('[data-policy-reads-retry]');
    expect(retry?.getAttribute('aria-disabled')).toBe('true');
    retry?.click();
    await fixture.whenStable();
    expect(emitted).not.toHaveBeenCalled();
  });

  it('keeps the focus of its own retry across a failure that repeats', async () => {
    const { fixture, root } = await render(READY_ACTIVATION, {
      phase: 'error',
      value: null,
      error: RATE_LIMITED,
    });
    const retry = root.querySelector<HTMLButtonElement>('[data-policy-reads-retry]');
    retry?.focus();
    expect(document.activeElement).toBe(retry);

    retry?.click();
    // The read starts: the region stays, so the press keeps its focus.
    fixture.componentRef.setInput('busy', true);
    fixture.detectChanges();
    expect(root.querySelector('[data-policy-reads-retry]')).toBe(retry);
    expect(document.activeElement).toBe(retry);

    // It failed again; focus is never handed back to `body` between the two states.
    fixture.componentRef.setInput('busy', false);
    fixture.detectChanges();
    await fixture.whenStable();
    expect(document.activeElement).not.toBe(document.body);
    expect(document.activeElement).toBe(retry);
  });
});
