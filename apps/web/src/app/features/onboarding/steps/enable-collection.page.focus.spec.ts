import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import type { PolicyActivationDto, PolicyActivationStateDto } from '@evidgate/sdk';

import {
  ACTIVATION,
  ACTIVATION_STATE,
  ACTIVATION_STATE_ENFORCING,
  createActivationReadContext,
  deferredResult,
  drain,
  failure,
} from './enable-collection.page.test-support';

class EnableFocusHarness extends ComponentHarness {
  static hostSelector = 'app-onboarding-enable-collection';
  readonly enabledHeading = this.locatorFor('#enable-enabled-heading');
  readonly optionalEnabledHeading = this.locatorForOptional('#enable-enabled-heading');
  readonly confirm = this.locatorFor('[data-testid="enable-collection-confirm"]');
  readonly retry = this.locatorFor('app-error-pattern button');
  readonly readAgain = this.locatorFor('[data-testid="enable-collection-read-again"]');
  readonly failedHeading = this.locatorFor('#enable-confirm-failed-heading');
}

describe('EnableCollectionPage focus ownership', () => {
  const { readPolicyActivation, activatePolicy, createPage } = createActivationReadContext();

  it.each([ACTIVATION_STATE, ACTIVATION_STATE_ENFORCING])(
    'preserves focus on the first read of an already-enabled $mode project',
    async (state) => {
      readPolicyActivation.mockResolvedValue(state);
      const initialFocus = document.activeElement;
      const fixture = await createPage();
      const page = await TestbedHarnessEnvironment.harnessForFixture(fixture, EnableFocusHarness);

      expect(await (await page.enabledHeading()).isFocused()).toBe(false);
      expect(document.activeElement).toBe(initialFocus);
    },
  );

  it('does not take focus when an already-enabled initial read settles later', async () => {
    const read = deferredResult<PolicyActivationStateDto | null>();
    readPolicyActivation.mockReturnValue(read.promise);
    const fixture = await createPage();
    const page = await TestbedHarnessEnvironment.harnessForFixture(fixture, EnableFocusHarness);
    const initialFocus = document.activeElement;
    expect(await page.optionalEnabledHeading()).toBeNull();

    read.resolve(ACTIVATION_STATE);
    await drain(fixture);

    expect(await (await page.enabledHeading()).isFocused()).toBe(false);
    expect(document.activeElement).toBe(initialFocus);
  });

  it('does not focus the enabled heading after a read-only recovery finds an activation', async () => {
    readPolicyActivation
      .mockRejectedValueOnce(failure('forbidden', 403))
      .mockResolvedValueOnce(ACTIVATION_STATE);
    const fixture = await createPage();
    const page = await TestbedHarnessEnvironment.harnessForFixture(fixture, EnableFocusHarness);
    await (await page.readAgain()).click();
    await drain(fixture);

    expect(await (await page.enabledHeading()).isFocused()).toBe(false);
    expect(activatePolicy).not.toHaveBeenCalled();
  });

  it.each([true, false])(
    'focuses the enabled heading after this page confirms successfully (changed=%s)',
    async (changed) => {
      readPolicyActivation.mockResolvedValue(null);
      activatePolicy.mockResolvedValue({ ...ACTIVATION, changed });
      const fixture = await createPage();
      const page = await TestbedHarnessEnvironment.harnessForFixture(fixture, EnableFocusHarness);
      const confirm = await page.confirm();
      await confirm.focus();
      await confirm.click();
      await drain(fixture);

      expect(await (await page.enabledHeading()).isFocused()).toBe(true);
    },
  );

  it('focuses only the refusal after failure and the enabled heading after an explicit retry succeeds', async () => {
    readPolicyActivation.mockResolvedValue(null);
    activatePolicy
      .mockRejectedValueOnce(failure('unavailable', 503))
      .mockResolvedValueOnce(ACTIVATION);
    const fixture = await createPage();
    const page = await TestbedHarnessEnvironment.harnessForFixture(fixture, EnableFocusHarness);
    await (await page.confirm()).click();
    await drain(fixture);

    expect(await page.optionalEnabledHeading()).toBeNull();
    expect(await (await page.failedHeading()).isFocused()).toBe(true);

    await (await page.retry()).click();
    await drain(fixture);

    expect(await (await page.enabledHeading()).isFocused()).toBe(true);
  });

  it('does not transfer a pending confirmation focus request to another project', async () => {
    const activation = deferredResult<PolicyActivationDto>();
    readPolicyActivation.mockResolvedValue(null);
    activatePolicy.mockReturnValue(activation.promise);
    const fixture = await createPage();
    const page = await TestbedHarnessEnvironment.harnessForFixture(fixture, EnableFocusHarness);
    await (await page.confirm()).click();
    await drain(fixture);
    expect(activatePolicy).toHaveBeenCalledTimes(1);

    readPolicyActivation.mockResolvedValue({ ...ACTIVATION_STATE, project_id: 'project-2' });
    fixture.componentRef.setInput('projectId', 'project-2');
    await drain(fixture);
    expect(await (await page.enabledHeading()).isFocused()).toBe(false);

    activation.resolve(ACTIVATION);
    await drain(fixture);

    expect(await (await page.enabledHeading()).isFocused()).toBe(false);
  });
});
