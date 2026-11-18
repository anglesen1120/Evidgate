import { TestBed } from '@angular/core/testing';
import { ActivatedRoute } from '@angular/router';
import {
  CREATED,
  CreateRunDialogHarness,
  MANIFEST,
  PROJECT,
  refusal,
  renderCreateRun,
  setupCreateRun,
  provideLayoutTestStubs,
  type CreateRunTestState,
} from './create-run-dialog.test-support';

describe('CreateRunDialogComponent', () => {
  provideLayoutTestStubs();
  let state: CreateRunTestState;
  beforeEach(() => {
    state = setupCreateRun();
  });
  afterEach(() => {
    TestBed.resetTestingModule();
    document.body.replaceChildren();
  });

  it('opens with the required manifest first and sends nothing before submission', async () => {
    const { dialog } = await renderCreateRun();
    expect(await dialog.value('manifestId')).toBe('');
    expect(await dialog.text()).toContain('Create run');
    expect(state.createRun).not.toHaveBeenCalled();
    expect(state.newIdempotencyKey).not.toHaveBeenCalled();
  });

  it('retains the draft and renders a typed 409 without replacing the intent on two retries', async () => {
    state.createRun.mockRejectedValue(refusal());
    const { dialog } = await renderCreateRun();
    await dialog.set('manifestId', MANIFEST);
    for (let i = 0; i < 3; i++) await dialog.submit();
    expect(await dialog.failure()).toContain('idempotency_conflict');
    expect(await dialog.failure()).toContain('req-create-1');
    expect(await dialog.value('manifestId')).toBe(MANIFEST);
    expect(state.createRun).toHaveBeenCalledTimes(3);
    const keys = state.createRun.mock.calls.map((call) => call[2]);
    expect(new Set(keys).size).toBe(1);
    expect(state.newIdempotencyKey).toHaveBeenCalledTimes(1);
  });

  it('mints a new key for a changed body, but not for an unchanged effective body', async () => {
    state.createRun.mockRejectedValue(refusal());
    const { dialog } = await renderCreateRun();
    await dialog.set('manifestId', MANIFEST);
    await dialog.submit();
    await dialog.set('manifestId', ` ${MANIFEST} `);
    await dialog.submit();
    expect(state.newIdempotencyKey).toHaveBeenCalledTimes(1);
    await dialog.set('releaseId', MANIFEST);
    await dialog.submit();
    expect(state.newIdempotencyKey).toHaveBeenCalledTimes(2);
    expect(state.createRun.mock.calls[2]?.[2]).not.toBe(state.createRun.mock.calls[0]?.[2]);
  });

  it('maps validation details to the refused field, retains input and focuses the linked summary', async () => {
    state.createRun.mockRejectedValue(
      refusal({
        kind: 'validation',
        status: 422,
        code: 'request_invalid',
        message: 'Choose another ref name.',
        details: [{ path: '/ref/name', reason: 'invalid_value' }],
      }),
    );
    const { dialog, fixture } = await renderCreateRun();
    await dialog.set('manifestId', MANIFEST);
    await dialog.set('refName', 'release/main');
    await dialog.submit();
    expect(await dialog.invalid('refName')).toBe('true');
    expect(await dialog.value('refName')).toBe('release/main');
    expect(await dialog.failure()).toContain('request_invalid');
    expect(await dialog.text()).toContain('Choose another ref name.');
    await fixture.whenStable();
    expect(document.activeElement?.id).toBe('run-create-summary');
    await dialog.set('refName', 'release/fixed');
    expect(await dialog.refExpanded()).toBe(true);
    expect(await dialog.invalid('refName')).toBeNull();
  });

  it.each([
    ['not_found', 404, 'not_found'],
    ['payload_too_large', 413, 'payload_too_large'],
    ['forbidden', 403, 'forbidden'],
    ['validation', 422, 'manifest_invalid'],
    ['validation', 422, 'execution_key_conflict'],
    ['validation', 422, 'release_unresolvable'],
    ['validation', 422, 'release_not_active'],
    ['validation', 422, 'fork_run_not_bindable'],
    ['validation', 422, 'replay_grant_invalid'],
    ['validation', 422, 'replay_release_not_allowed'],
  ] as const)('shows the typed %s/%s/%s refusal as persistent text', async (kind, status, code) => {
    state.createRun.mockRejectedValue(
      refusal({ kind, status, code, message: 'Refused <b>as text</b>.' }),
    );
    const { dialog } = await renderCreateRun();
    await dialog.set('manifestId', MANIFEST);
    await dialog.submit();
    expect(await dialog.failure()).toContain(code);
    expect(await dialog.failure()).toContain('Refused <b>as text</b>.');
    expect(state.navigate).not.toHaveBeenCalled();
  });

  it('submits the exact body, closes on 201 and navigates to the created detail route', async () => {
    const { dialog, fixture, loader } = await renderCreateRun();
    const closed = vi.fn();
    fixture.componentInstance.closed.subscribe(closed);
    await dialog.set('manifestId', MANIFEST);
    await dialog.submit();
    expect(state.createRun).toHaveBeenCalledWith(
      PROJECT,
      { manifest_id: MANIFEST },
      expect.any(String),
      expect.any(AbortSignal),
    );
    expect(state.navigate).toHaveBeenCalledWith([CREATED.run_id], {
      relativeTo: TestBed.inject(ActivatedRoute),
      queryParams: {},
    });
    expect(await loader.hasHarness(CreateRunDialogHarness)).toBe(false);
    expect(closed).toHaveBeenCalledTimes(1);
  });

  it('returns focus to the trigger on Cancel and Escape, and reopening starts a new intent', async () => {
    state.createRun.mockRejectedValue(refusal());
    const first = await renderCreateRun();
    await first.dialog.set('manifestId', MANIFEST);
    await first.dialog.submit();
    await first.dialog.dismiss();
    expect(document.activeElement).toBe(first.opener);
    first.fixture.destroy();
    const second = await renderCreateRun();
    await second.dialog.set('manifestId', MANIFEST);
    await second.dialog.submit();
    expect(state.newIdempotencyKey).toHaveBeenCalledTimes(2);
    await second.dialog.escape();
    expect(document.activeElement).toBe(second.opener);
  });

  it('refuses invalid local fields without allocating a key or making a request', async () => {
    const { dialog } = await renderCreateRun();
    await dialog.submit();
    expect(await dialog.invalid('manifestId')).toBe('true');
    expect(state.createRun).not.toHaveBeenCalled();
    expect(state.newIdempotencyKey).not.toHaveBeenCalled();
  });

  it('blocks duplicate submissions and dismissal while the create response is pending', async () => {
    let finish: ((value: typeof CREATED) => void) | undefined;
    state.createRun.mockImplementation(
      () =>
        new Promise((resolve) => {
          finish = resolve;
        }),
    );
    const { dialog } = await renderCreateRun();
    await dialog.set('manifestId', MANIFEST);
    await dialog.submit();
    expect(await dialog.busy()).toBe('true');
    expect(await dialog.disabled()).toBe(true);
    await dialog.submit();
    await dialog.escape();
    expect(state.createRun).toHaveBeenCalledTimes(1);
    finish?.(CREATED);
  });

  it('denies a lost permission, a narrow viewport and a changed project immediately before POST', async () => {
    const { dialog } = await renderCreateRun();
    await dialog.set('manifestId', MANIFEST);
    state.allowed.set(false);
    await dialog.submit();
    expect(await dialog.text()).toContain('permission');
    expect(state.createRun).not.toHaveBeenCalled();
    state.allowed.set(true);
    state.canMutate.set(false);
    await dialog.submit();
    expect(state.createRun).not.toHaveBeenCalled();
    state.canMutate.set(true);
    state.currentProject.set({ id: 'another-project' });
    await dialog.submit();
    expect(state.createRun).not.toHaveBeenCalled();
  });

  it('does not navigate a late response after the user changes tenant scope', async () => {
    let finish: ((value: typeof CREATED) => void) | undefined;
    state.createRun.mockImplementation(
      () =>
        new Promise((resolve) => {
          finish = resolve;
        }),
    );
    const { dialog, fixture } = await renderCreateRun();
    await dialog.set('manifestId', MANIFEST);
    await dialog.submit();
    state.currentOrg.set({ id: 'another-org' });
    finish?.(CREATED);
    await fixture.whenStable();
    expect(state.navigate).not.toHaveBeenCalled();
  });
});
