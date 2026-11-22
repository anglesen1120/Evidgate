import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow/narrow-mutation-policy';
import { webhooksFixture } from './webhooks-review.fixture';
import { WebhookIntentPreview } from './webhook-intent-preview';

class IntentHarness extends ComponentHarness {
  static hostSelector = 'app-webhook-intent-preview';
  readonly actions = this.locatorForAll('button');
}
class ConfirmationHarness extends ComponentHarness {
  static hostSelector = 'hlm-dialog-content';
  readonly cancel = this.locatorFor('[data-preview-cancel]');
  readonly confirmation = this.locatorFor('[data-preview-confirm]');
  readonly check = this.locatorFor('[role="checkbox"]');
  async text() {
    return (await this.host()).text();
  }
}
async function setup(rotationAllowed = true) {
  TestBed.configureTestingModule({
    providers: [{ provide: NarrowMutationPolicy, useValue: { allowsMutation: () => true } }],
  });
  const fixture = TestBed.createComponent(WebhookIntentPreview);
  fixture.componentRef.setInput('endpointId', 'wh-19');
  fixture.componentRef.setInput('rotationAllowed', rotationAllowed);
  fixture.componentRef.setInput('failedDelivery', webhooksFixture.deliveries[0]);
  fixture.detectChanges();
  const host = await TestbedHarnessEnvironment.harnessForFixture(fixture, IntentHarness);
  const overlay = TestbedHarnessEnvironment.documentRootLoader(fixture);
  const result: string[] = [];
  fixture.componentInstance.recorded.subscribe((value) => result.push(value));
  return { host, overlay, result };
}
describe('Webhook intent preview', () => {
  afterEach(() => TestBed.resetTestingModule());
  it.each([
    ['Create webhook endpoint', 'Endpoint name'],
    ['Rotate signing credential', '[REDACTED'],
    ['Redeliver this event?', 'dl-91'],
  ])(
    'opens %s as a local confirmation and cancellation records nothing',
    async (action, expected) => {
      const { host, overlay, result } = await setup();
      for (const btn of await host.actions()) if ((await btn.text()) === action) await btn.click();
      const dialog = await overlay.getHarness(ConfirmationHarness);
      expect(await dialog.text()).toContain(expected);
      await (await dialog.cancel()).click();
      expect(result).toEqual([]);
      expect(await overlay.hasHarness(ConfirmationHarness)).toBe(false);
    },
  );
  it('keeps rotation unavailable while the result is unknown', async () => {
    const { host } = await setup(false);
    const labels = await Promise.all((await host.actions()).map((b) => b.text()));
    expect(labels).toContain('Create webhook endpoint');
    expect(labels).not.toContain('Rotate signing credential');
  });
  it('records only the redelivery intent after acknowledgment', async () => {
    const { host, overlay, result } = await setup();
    const [, , redeliver] = await host.actions();
    if (!redeliver) throw new Error('Missing redelivery action');
    await redeliver.click();
    const dialog = await overlay.getHarness(ConfirmationHarness);
    expect(await (await dialog.confirmation()).getProperty<boolean>('disabled')).toBe(true);
    await (await dialog.check()).click();
    await (await dialog.confirmation()).click();
    expect(result).toEqual(['saved']);
  });
});
