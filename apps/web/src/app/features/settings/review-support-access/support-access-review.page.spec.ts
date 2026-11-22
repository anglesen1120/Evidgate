import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap } from '@angular/router';
import { BehaviorSubject } from 'rxjs';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { SupportAccessReviewPage } from './support-access-review.page';
import { supportAccessFixture } from './support-access-review.fixture';

class PageHarness extends ComponentHarness {
  static hostSelector = 'app-support-access-review';
  readonly title = this.locatorFor('h1');
  readonly snapshot = this.locatorFor('[data-snapshot-time] time');
  readonly badge = this.locatorFor('[data-inspect-status] app-status-pill');
  readonly badgeIcon = this.locatorFor('[data-inspect-status] app-status-pill hlm-icon');
  readonly pick = this.locatorFor('[data-second-record]');
  readonly decision = this.locatorForOptional('[data-grant-action]');
  readonly details = this.locatorFor('[data-record-detail]');
  readonly content = async () => (await this.host()).text();
}

describe('SupportAccessReviewPage', () => {
  provideLayoutTestStubs();

  async function render(state: string) {
    const params = new BehaviorSubject(convertToParamMap({ state }));
    TestBed.configureTestingModule({
      imports: [SupportAccessReviewPage],
      providers: [{ provide: ActivatedRoute, useValue: { queryParamMap: params } }],
    });
    const fixture = TestBed.createComponent(SupportAccessReviewPage);
    fixture.detectChanges();
    return {
      page: await TestbedHarnessEnvironment.harnessForFixture(fixture, PageHarness),
      params,
      fixture,
    };
  }
  it.each([
    ['default', 'Requested scopes'],
    ['loading', 'Loading grants'],
    ['empty', 'No support grants'],
    ['filtered', 'No results match these filters'],
    ['error', 'Request failed'],
    ['denied', 'Not authorized'],
    ['401', 'Sign in again'],
    ['403', 'Read-only access'],
    ['404', 'Not found'],
    ['412', 'Linked configuration conflict'],
    ['429', 'Too many requests'],
    ['pending', 'Submission pending'],
    ['long', 'Long scope review'],
    ['approved', 'Grant approved'],
    ['success', 'Grant approved'],
    ['expired', 'Grant expired'],
    ['revoked', 'Grant revoked'],
    ['readonly', 'Read-only access'],
    ['self', 'approver_must_differ'],
    ['stale', 'grant_not_requested'],
    ['gone', 'Grant changed during review'],
    ['banner', 'X-Evidgate-Support-Access'],
  ])('%s renders the scoped state', async (state, expected) => {
    const { page } = await render(state);
    expect(await (await page.title()).text()).toBe('Support access');
    expect(await page.content()).toContain(expected);
  });
  it.each([
    ['ready', '2026-10-13T10:42:00Z'],
    ['approved', '2026-10-13T10:50:00Z'],
    ['banner', '2026-10-13T10:50:00Z'],
    ['revoked', '2026-10-13T10:50:00Z'],
    ['expired', '2026-10-13T11:00:00Z'],
  ])('%s displays its own UTC snapshot time', async (state, expected) => {
    const { page } = await render(state);
    const time = await page.snapshot();
    expect(await time.getAttribute('datetime')).toBe(expected);
    expect(await time.text()).toContain('UTC');
  });
  it('selects a historical reference without enabling review actions', async () => {
    const { page } = await render('ready');
    expect(await (await page.badge()).text()).toContain('Requested');
    expect(await (await page.badgeIcon()).getAttribute('aria-hidden')).toBe('true');
    expect(await (await page.decision())?.getProperty('disabled')).toBe(true);
    await (await page.pick()).click();
    const text = await (await page.details()).text();
    expect(text).toContain('Read run metadata');
    expect(text).toContain('sg-40');
    expect(await page.decision()).toBeNull();
  });
  it('clears grant references on query-driven loss of read access', async () => {
    const { page, params } = await render('default');
    expect(await page.content()).toContain('req-sg-41');
    params.next(convertToParamMap({ state: 'denied' }));
    expect(await page.content()).not.toContain('sg-41');
  });
  it('renders only grant metadata even if a supplied record has extra fields', async () => {
    const { fixture, page } = await render('default');
    const grants = supportAccessFixture.grants.map((g) => ({
      ...g,
      subject: 'INC-205',
      privateValue: 'unrendered-canary',
    }));
    fixture.componentRef.setInput('viewModel', { ...supportAccessFixture, grants });
    expect(await page.content()).toContain('INC-205');
    expect(await page.content()).not.toContain('unrendered-canary');
  });
  it('does not turn an approval into an active session', async () => {
    const { page } = await render('approved');
    expect(await page.content()).not.toContain('X-Evidgate-Support-Access · Support-07');
  });
});
