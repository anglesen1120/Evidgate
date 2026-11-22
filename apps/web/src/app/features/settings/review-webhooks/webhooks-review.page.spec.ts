import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { BehaviorSubject } from 'rxjs';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { WebhooksReviewPage } from './webhooks-review.page';
import { webhooksFixture } from './webhooks-review.fixture';

class ReviewHarness extends ComponentHarness {
  static hostSelector = 'app-webhooks-review';
  readonly title = this.locatorFor('h1');
  readonly choices = this.locatorForAll('input[type=radio]');
  readonly pills = this.locatorForAll('app-status-pill');
  readonly icons = this.locatorForAll('app-status-pill hlm-icon');
  readonly neutral = this.locatorForAll('app-status-pill .status-pill--neutral');
  async text() {
    return (await this.host()).text();
  }
  async html() {
    return (await this.host()).getProperty<string>('outerHTML');
  }
}

async function render(query: Record<string, string> = {}, model = webhooksFixture) {
  const params = new BehaviorSubject(convertToParamMap(query));
  TestBed.configureTestingModule({
    providers: [
      provideRouter([]),
      { provide: ActivatedRoute, useValue: { queryParamMap: params.asObservable() } },
    ],
  });
  const f = TestBed.createComponent(WebhooksReviewPage);
  f.componentRef.setInput('model', model);
  f.detectChanges();
  return { view: await TestbedHarnessEnvironment.harnessForFixture(f, ReviewHarness), params };
}

describe('Webhook integrations review', () => {
  provideLayoutTestStubs();
  afterEach(() => TestBed.resetTestingModule());
  it('shows the supplied record, with an icon and text for each status', async () => {
    const { view } = await render();
    expect(await (await view.title()).text()).toBe('Webhook integrations');
    expect(await view.text()).toContain('ci.example.test/hooks/evidgate');
    const pills = await view.pills();
    expect(pills.length).toBeGreaterThan(0);
    expect((await view.icons()).length).toBe(pills.length);
    for (const pill of pills) expect((await pill.text()).trim()).not.toBe('');
  });
  it.each([
    ['loading', 'Loading the snapshot…'],
    ['empty', 'No webhook endpoints'],
    ['no-match', 'No deliveries match this filter'],
    ['error', 'Integrations could not be loaded'],
    ['403', 'Integrations are not authorized'],
    ['404', 'Integration not found'],
    ['401', 'Sign in again'],
    ['429', 'Too many requests'],
    ['412', 'Someone else changed this — reload'],
    ['submitting', 'Reviewing local intent · no request sent'],
    ['created', 'Create intent prepared · nothing created'],
    ['rotated', 'Rotation recorded · value redacted'],
    ['saved', 'Redelivery intent recorded · no request sent'],
    ['copy-fail', 'Clipboard unavailable · redacted only'],
    ['unavailable', 'Rotation result unavailable'],
    ['stale', 'Endpoint metadata may be stale'],
    ['read-only', 'Read-only · webhook:write revoked'],
    ['long', 'Long URLs wrap without clipping'],
    ['empty-history', 'No deliveries recorded'],
    ['rotation-error', 'Rotation did not complete'],
    ['rotation-lost', 'Rotation response lost · outcome unknown'],
    ['unhealthy', 'Endpoint reported unhealthy'],
    ['pending-delivery', 'Pending delivery'],
    ['denied', 'Integrations are not authorized'],
    ['success', 'Redelivery intent recorded · no request sent'],
  ])('renders the %s state', async (state, label) => {
    const { view } = await render({ state });
    expect(await view.text()).toContain(label);
    if (['loading', 'empty', 'error', '401', '403', '404', '429', 'denied'].includes(state)) {
      expect(await view.text()).not.toContain('ci.example.test/hooks/evidgate');
      expect(await view.choices()).toHaveLength(0);
    }
  });
  it('does not substitute a default record for an unknown identifier', async () => {
    const { view, params } = await render();
    expect(await view.text()).toContain('ci.example.test/hooks/evidgate');
    params.next(convertToParamMap({ endpoint: 'unknown' }));
    expect(await view.text()).toContain('Integration not found');
    expect(await view.text()).not.toContain('ci.example.test/hooks/evidgate');
  });
  it('renders only signing metadata even when extra confidential fields arrive', async () => {
    const canary = 'test-only-never-render-me';
    const endpoints = webhooksFixture.endpoints.map((e) => ({ ...e, signingValue: canary }));
    const { view } = await render({}, { ...webhooksFixture, endpoints });
    expect(await view.text()).toContain('Rotation overlap ends');
    expect(await view.html()).not.toContain(canary);
  });
  it('keeps a recorded failure and its attempt history after a redelivery intent', async () => {
    const { view } = await render({ state: 'saved' });
    expect(await view.text()).toContain('dl-91');
    expect(await view.text()).toContain('503');
    expect(await view.text()).toContain('Failed');
    expect(await view.text()).toContain('Delivered');
  });
  it('shows pending as neutral, not as delivered', async () => {
    const { view } = await render({ state: 'pending-delivery' });
    const labels = await Promise.all((await view.neutral()).map((n) => n.text()));
    expect(labels).toContain('Pending');
    expect(await view.text()).not.toContain('dl-90');
  });
  it('selects the disabled endpoint without borrowing the active endpoint history', async () => {
    const { view } = await render();
    const [, audit] = await view.choices();
    if (!audit) throw new Error('Missing audit endpoint choice');
    await audit.click();
    expect(await view.text()).toContain('https://audit.example.test/evidgate');
    expect(await view.text()).not.toContain('dl-90');
  });
});
