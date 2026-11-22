import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { BehaviorSubject } from 'rxjs';
import { AgentPoolsReviewPage } from './agent-pools-review.page';
import { agentPoolsFixture } from './agent-pools-review.fixture';

class ReviewHarness extends ComponentHarness {
  static hostSelector = 'app-agent-pools-review';
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

async function render(query: Record<string, string> = {}, model = agentPoolsFixture) {
  const params = new BehaviorSubject(convertToParamMap(query));
  TestBed.configureTestingModule({
    providers: [
      provideRouter([]),
      { provide: ActivatedRoute, useValue: { queryParamMap: params.asObservable() } },
    ],
  });
  const f = TestBed.createComponent(AgentPoolsReviewPage);
  f.componentRef.setInput('model', model);
  f.detectChanges();
  return { view: await TestbedHarnessEnvironment.harnessForFixture(f, ReviewHarness), params };
}

describe('Agent pools review', () => {
  afterEach(() => TestBed.resetTestingModule());
  it('shows the supplied record, with an icon and text for each status', async () => {
    const { view } = await render();
    expect(await (await view.title()).text()).toBe('Agent pools');
    expect(await view.text()).toContain('pool_01HX6D3K9M');
    const pills = await view.pills();
    expect(pills.length).toBeGreaterThan(0);
    expect((await view.icons()).length).toBe(pills.length);
    for (const pill of pills) expect((await pill.text()).trim()).not.toBe('');
  });
  it.each([
    ['loading', 'Loading the snapshot…'],
    ['empty', 'No agent pools configured'],
    ['no-match', 'No results match these filters'],
    ['error', 'Could not load agent pool data'],
    ['403', 'Agent pools are not authorized'],
    ['404', 'Not found'],
    ['401', 'Sign-in required'],
    ['429', 'Rate limited'],
    ['412', 'Someone else changed this — reload'],
    ['unavailable', 'Capabilities unavailable'],
    ['stale', 'Heartbeat state supplied by server'],
    ['read-only', 'Read-only · permission changed'],
    ['long', 'Long capability labels wrap'],
    ['drift', 'Runtime mismatch'],
    ['unschedulable', 'job-72 · Unschedulable'],
    ['restored', 'Snapshot received'],
    ['denied', 'Agent pools are not authorized'],
    ['success', 'Snapshot received'],
  ])('renders the %s state', async (state, label) => {
    const { view } = await render({ state });
    expect(await view.text()).toContain(label);
    if (['loading', 'empty', 'error', '401', '403', '404', '429', 'denied'].includes(state)) {
      expect(await view.text()).not.toContain('pool_01HX6D3K9M');
      expect(await view.choices()).toHaveLength(0);
    }
  });
  it('does not substitute a default record for an unknown identifier', async () => {
    const { view, params } = await render();
    expect(await view.text()).toContain('pool_01HX6D3K9M');
    params.next(convertToParamMap({ pool: 'unknown' }));
    expect(await view.text()).toContain('Not found');
    expect(await view.text()).not.toContain('pool_01HX6D3K9M');
  });
  it('selects an unmatched job without inventing registered capacity or a lease', async () => {
    const { view } = await render();
    const [, mobile] = await view.choices();
    if (!mobile) throw new Error('Missing mobile-lab pool choice');
    await mobile.click();
    expect(await view.text()).toContain('No lease was supplied');
    expect(await view.text()).not.toContain('pool_01HX6D3K9M');
  });
  it('keeps registered agent history when the capability comparison is unavailable', async () => {
    const { view } = await render({ state: 'unavailable' });
    expect(await view.text()).toContain('web-linux-02');
    expect(await view.text()).not.toContain('xctest requested');
  });
});
