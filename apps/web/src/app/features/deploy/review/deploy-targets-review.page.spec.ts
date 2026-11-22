import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap } from '@angular/router';
import { BehaviorSubject } from 'rxjs';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { DeployTargetsReviewPage } from './deploy-targets-review.page';

class PageHarness extends ComponentHarness {
  static hostSelector = 'app-deploy-targets-review';
  readonly title = this.locatorFor('h1');
  readonly badge = this.locatorFor('[data-inspect-status] app-status-pill');
  readonly badgeIcon = this.locatorFor('[data-inspect-status] app-status-pill hlm-icon');
  readonly tone = this.locatorFor('[data-inspect-status] .status-pill');
  readonly pick = this.locatorFor('[data-second-record]');
  readonly details = this.locatorFor('[data-record-detail]');
  readonly content = async () => (await this.host()).text();
}

describe('DeployTargetsReviewPage', () => {
  provideLayoutTestStubs();

  async function render(state: string) {
    const params = new BehaviorSubject(convertToParamMap({ state }));
    TestBed.configureTestingModule({
      imports: [DeployTargetsReviewPage],
      providers: [{ provide: ActivatedRoute, useValue: { queryParamMap: params } }],
    });
    const fixture = TestBed.createComponent(DeployTargetsReviewPage);
    fixture.detectChanges();
    return {
      page: await TestbedHarnessEnvironment.harnessForFixture(fixture, PageHarness),
      params,
      fixture,
    };
  }
  it.each([
    ['default', 'Promotion history'],
    ['loading', 'Loading the snapshot'],
    ['empty', 'No deploy targets'],
    ['no-match', 'No promotion checks match this release'],
    ['error', 'Promotion history could not be loaded'],
    ['denied', 'Not authorized'],
    ['401', 'Sign in again'],
    ['403', 'Not authorized'],
    ['404', 'Not found'],
    ['429', 'Too many requests'],
    ['412', 'Someone else changed this target'],
    ['submitting', 'Reviewing local draft'],
    ['success', 'Draft reviewed'],
    ['validation', 'Order must be between 1 and 20'],
    ['unavailable', 'Pinned revision unavailable'],
    ['stale', 'Target metadata may be stale'],
    ['read-only', 'Read-only access'],
    ['long', 'production-eu-west-checkout-primary-with-intentionally-long-name'],
    ['empty-history', 'No promotion checks'],
    ['no-policy-read', 'Policy metadata only'],
  ])('%s renders the scoped state', async (state, expected) => {
    const { page } = await render(state);
    expect(await (await page.title()).text()).toBe('Deploy targets');
    expect(await page.content()).toContain(expected);
  });
  it('keeps pending neutral and separate from a recorded release decision', async () => {
    const { page } = await render('default');
    expect(await (await page.badge()).text()).toContain('Not evaluated');
    expect(await (await page.badgeIcon()).getAttribute('aria-hidden')).toBe('true');
    expect(await (await page.tone()).hasClass('status-pill--neutral')).toBe(true);
    const text = await page.content();
    expect(text).toContain('No decision');
    expect(text).toContain('rd-14');
    expect(text).not.toContain('PASS');
  });
  it('does not borrow production history for a target with no checks', async () => {
    const { page } = await render('ready');
    await (await page.pick()).click();
    expect(await (await page.details()).text()).toContain('Pre-production');
    expect(await page.content()).not.toContain('pc-91');
    expect(await page.content()).toContain('No promotion checks');
  });
  it('retains safe target context on partial history failure but not on denial', async () => {
    const { page, params } = await render('error');
    expect(await page.content()).toContain('dt-6');
    expect(await page.content()).not.toContain('pc-91');
    params.next(convertToParamMap({ state: 'denied' }));
    expect(await page.content()).not.toContain('dt-6');
  });
});
