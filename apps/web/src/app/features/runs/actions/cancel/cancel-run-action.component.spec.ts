import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { RunStateDto } from '@evidgate/sdk';
import ready from '../../../../../../../../fixtures/web-api/run-detail/input.json';
import { ApiRequestError } from '../../../../core/http/api-error';
import { SessionStore } from '../../../../core/session/session.store';
import { provideLayoutTestStubs } from '../../../../shared/testing/layout.testing';
import { NarrowMutationPolicy } from '../../../../shared/ui/narrow/narrow-mutation-policy';
import { RunDataService } from '../../data/run-data.service';
import { RunDetailStore } from '../../detail/run-detail.store';
import { CancelRunActionComponent } from './cancel-run-action.component';
import { cancelReasonError } from './cancel-run-state';

class CancelActionHarness extends ComponentHarness {
  static hostSelector = 'app-cancel-run-action';
  private readonly trigger = this.locatorFor('[data-run-cancel-trigger]');
  readonly status = this.locatorForOptional('[data-run-cancel-status]');
  async open(): Promise<void> {
    const trigger = await this.trigger();
    await trigger.focus();
    await trigger.click();
  }
  async disabled(): Promise<string | null> {
    return (await this.trigger()).getAttribute('aria-disabled');
  }
  async focused(): Promise<boolean> {
    return (await this.trigger()).isFocused();
  }
}
class CancelDialogHarness extends ComponentHarness {
  static hostSelector = '[data-run-cancel-dialog]';
  private readonly reason = this.locatorFor('#run-cancel-reason');
  private readonly confirm = this.locatorFor('[data-run-cancel-confirm]');
  private readonly keep = this.locatorFor('[data-run-cancel-keep]');
  async fill(value: string): Promise<void> {
    const input = await this.reason();
    await input.setInputValue(value);
    await input.dispatchEvent('input');
  }
  async submit(): Promise<void> {
    await (await this.confirm()).click();
  }
  async close(): Promise<void> {
    await (await this.keep()).click();
  }
  async initialFocus(): Promise<boolean> {
    return (await this.keep()).isFocused();
  }
  async text(): Promise<string> {
    return (await this.host()).text();
  }
}

describe('CancelRunActionComponent', () => {
  provideLayoutTestStubs();
  afterEach(() => TestBed.resetTestingModule());
  async function setup(permission = true, state: RunStateDto['state'] = 'collecting') {
    const run = signal<RunStateDto | null>({ ...ready.body, state } as RunStateDto);
    const canMutate = signal(true);
    const allowed = signal(permission);
    const cancelRun = vi.fn<RunDataService['cancelRun']>().mockResolvedValue(undefined);
    const refreshRun = vi.fn().mockResolvedValue(undefined);
    TestBed.configureTestingModule({
      imports: [CancelRunActionComponent],
      providers: [
        provideZonelessChangeDetection(),
        {
          provide: RunDataService,
          useValue: { cancelRun, newIdempotencyKey: () => 'intent-dialog' },
        },
        { provide: RunDetailStore, useValue: { run, refreshRun } },
        {
          provide: SessionStore,
          useValue: {
            has: () => allowed(),
            currentOrg: () => ({ id: 'org-1' }),
            currentProject: () => ({ id: 'project-1' }),
          },
        },
        {
          provide: NarrowMutationPolicy,
          useValue: { canMutate, allowsMutation: () => canMutate() },
        },
      ],
    });
    const fixture = TestBed.createComponent(CancelRunActionComponent);
    fixture.detectChanges();
    const loader = TestbedHarnessEnvironment.documentRootLoader(fixture);
    const action = await TestbedHarnessEnvironment.harnessForFixture(fixture, CancelActionHarness);
    return { fixture, loader, action, cancelRun, run, refreshRun, canMutate, allowed };
  }

  it('exposes a disabled-with-reason permission state and opens only the authorized positive control', async () => {
    const h = await setup(false);
    expect(await h.action.disabled()).toBe('true');
    await h.action.open();
    expect(await h.loader.hasHarness(CancelDialogHarness)).toBe(false);
    expect(h.cancelRun).not.toHaveBeenCalled();
    h.allowed.set(true);
    await h.action.open();
    expect(await h.loader.hasHarness(CancelDialogHarness)).toBe(true);
    expect(h.cancelRun).not.toHaveBeenCalled();
  });

  it.each(['complete', 'incomplete', 'canceled'] as const)(
    'does not open a cancellation dialog for %s',
    async (state) => {
      const h = await setup(true, state);
      expect(await h.action.disabled()).toBe('true');
      await h.action.open();
      expect(await h.loader.hasHarness(CancelDialogHarness)).toBe(false);
      expect(h.cancelRun).not.toHaveBeenCalled();
    },
  );

  it('initially focuses Keep run and returns focus without a request when dismissed', async () => {
    const h = await setup();
    await h.action.open();
    const dialog = await h.loader.getHarness(CancelDialogHarness);
    expect(await dialog.initialFocus()).toBe(true);
    await dialog.close();
    expect(h.cancelRun).not.toHaveBeenCalled();
    expect(await h.action.focused()).toBe(true);
  });

  it('dismisses when permission is revoked and returns keyboard focus to the trigger', async () => {
    const h = await setup();
    await h.action.open();
    h.allowed.set(false);
    expect(await h.loader.hasHarness(CancelDialogHarness)).toBe(false);
    expect(await h.action.focused()).toBe(true);
    expect(h.cancelRun).not.toHaveBeenCalled();
  });

  it('records an optional unchanged reason only after confirmation and announces recorded, not stopped', async () => {
    const h = await setup();
    await h.action.open();
    const dialog = await h.loader.getHarness(CancelDialogHarness);
    await dialog.fill('  New build available  ');
    expect(h.cancelRun).not.toHaveBeenCalled();
    await dialog.submit();
    expect(h.cancelRun.mock.calls[0]?.slice(0, 3)).toEqual([
      ready.body.run_id,
      '  New build available  ',
      'intent-dialog',
    ]);
    const status = await h.action.status();
    expect(await status?.text()).toContain('202 · Cancel request recorded');
    expect(await status?.text()).toContain('does not mean execution has stopped');
    expect(await h.action.focused()).toBe(true);
  });

  it('keeps the entered reason and typed refusal visible without another POST', async () => {
    const h = await setup();
    h.cancelRun.mockRejectedValue(
      new ApiRequestError({
        kind: 'validation',
        status: 422,
        code: 'request_invalid',
        message: 'Reason refused.',
        requestId: 'cancel-refused',
        retryable: false,
        retryAfterSeconds: null,
        details: [{ path: '/reason', reason: 'invalid_value' }],
      }),
    );
    await h.action.open();
    const dialog = await h.loader.getHarness(CancelDialogHarness);
    await dialog.fill('Keep this draft');
    await dialog.submit();
    expect(await dialog.text()).toContain('request_invalid');
    expect(await dialog.text()).toContain('Reason refused.');
    expect(h.cancelRun).toHaveBeenCalledTimes(1);
  });

  it('shows inline validation for whitespace and rejects a resized narrow submission', async () => {
    const h = await setup();
    await h.action.open();
    const dialog = await h.loader.getHarness(CancelDialogHarness);
    await dialog.fill('   ');
    await dialog.submit();
    expect(await dialog.text()).toContain('A reason cannot contain only spaces.');
    expect(h.cancelRun).not.toHaveBeenCalled();
    await dialog.fill('Reason');
    h.canMutate.set(false);
    await dialog.submit();
    expect(h.cancelRun).not.toHaveBeenCalled();
    h.canMutate.set(true);
    await dialog.submit();
    expect(h.cancelRun).toHaveBeenCalledTimes(1);
  });
});

describe('cancel reason validation', () => {
  it('counts Unicode code points, accepts omitted/1,000 characters and refuses 1,001', () => {
    expect(cancelReasonError('')).toBeNull();
    expect(cancelReasonError('😀'.repeat(1000))).toBeNull();
    expect(cancelReasonError('😀'.repeat(1001))).toBe('too-long');
    expect(cancelReasonError(' \t\n')).toBe('blank');
  });
});
