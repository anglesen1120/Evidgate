import { InteractivityChecker } from '@angular/cdk/a11y';
import { ComponentHarness, TestKey } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap } from '@angular/router';
import { BehaviorSubject } from 'rxjs';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow/narrow-mutation-policy';
import { SupportAccessReviewPage } from './support-access-review.page';
import { supportAccessFixture } from './support-access-review.fixture';

class PageHarness extends ComponentHarness {
  static hostSelector = 'app-support-access-review';
  readonly title = this.locatorFor('h1');
  readonly snapshot = this.locatorFor('[data-snapshot-time] time');
  readonly choices = this.locatorForAll('button[data-view-record]');
  readonly dialog = this.documentRootLocatorFactory().locatorForOptional('[role="dialog"]');
  async open(index = 0) {
    const trigger = (await this.choices())[index];
    if (!trigger) throw new Error('Missing View button');
    await trigger.focus();
    await trigger.click();
    return trigger;
  }
  readonly badge = this.locatorFor('tbody app-status-pill');
  readonly badgeIcon = this.locatorFor('tbody app-status-pill hlm-icon');
  readonly decision = this.locatorForOptional('button[data-grant-action]');
  readonly decisionIcon = this.locatorFor('button[data-grant-action] hlm-icon');
  readonly viewIcons = this.locatorForAll('button[data-view-record] hlm-icon');
  readonly decisionDialog = this.documentRootLocatorFactory().locatorForOptional(
    '[role="dialog"], [role="alertdialog"]',
  );
  readonly cancel = this.documentRootLocatorFactory().locatorFor('#grant-cancel');
  readonly confirm = this.documentRootLocatorFactory().locatorFor(
    '[role="dialog"] footer button:not(#grant-cancel), [role="alertdialog"] footer button:not(#grant-cancel)',
  );
  readonly historicalAction = this.locatorForOptional(
    '[data-grant-row="sg-40"] [data-grant-action]',
  );
  readonly content = async () => (await this.host()).text();
}

describe('SupportAccessReviewPage', () => {
  provideLayoutTestStubs();

  it.each([
    ['ready', 'Approve support access · sg-41', false],
    ['approved', 'Revoke support access · sg-41', true],
  ] as const)(
    'keeps the %s decision contextual and requires confirmation',
    async (state, name, danger) => {
      const { page, params } = await render(state);
      params.next(convertToParamMap({ state, section: 'admin-preview' }));
      const action = await page.decision();
      if (!action) throw new Error('Expected support decision action');
      expect(await action.text({ exclude: '[popover]' })).toBe('');
      expect(await action.getAttribute('aria-label')).toBe(name);
      expect(await action.hasClass('row-action-danger')).toBe(danger);
      expect(await (await page.decisionIcon()).getAttribute('aria-hidden')).toBe('true');
      expect(await action.getProperty('disabled')).toBe(false);
      await action.focus();
      await action.click();
      const dialog = await page.decisionDialog();
      if (!dialog) throw new Error('Expected support decision confirmation');
      expect(await dialog.getAttribute('role')).toBe(danger ? 'alertdialog' : 'dialog');
      expect(await dialog.text()).toContain('sg-41');
      expect(await (await page.cancel()).isFocused()).toBe(true);
      expect(await (await page.confirm()).getProperty('disabled')).toBe(true);
      await (await page.cancel()).click();
      expect(await page.decisionDialog()).toBeNull();
      expect(await action.isFocused()).toBe(true);
      expect(await action.getAttribute('aria-label')).toBe(name);
    },
  );
  async function render(state: string) {
    const params = new BehaviorSubject(convertToParamMap({ state }));
    TestBed.configureTestingModule({
      imports: [SupportAccessReviewPage],
      providers: [
        { provide: ActivatedRoute, useValue: { queryParamMap: params } },
        {
          provide: NarrowMutationPolicy,
          useValue: { canMutate: signal(true), narrow: signal(false), allowsMutation: () => true },
        },
      ],
    });
    vi.spyOn(TestBed.inject(InteractivityChecker), 'isVisible').mockReturnValue(true);
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
    if (state === 'default') {
      await page.open();
      expect(await (await page.dialog())?.text()).toContain(expected);
    } else expect(await page.content()).toContain(expected);
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
    const { page, fixture } = await render('ready');
    expect(await (await page.badge()).text()).toContain('Requested');
    expect(await (await page.badgeIcon()).getAttribute('aria-hidden')).toBe('true');
    expect(await (await page.decision())?.getProperty('disabled')).toBe(true);
    const decision = await page.decision();
    expect(await decision?.text({ exclude: '[popover]' })).toBe('');
    expect(await decision?.getAttribute('aria-label')).toBe('Approve support access · sg-41');
    await decision?.click();
    expect(await page.dialog()).toBeNull();
    const trigger = await page.open(1);
    expect(await trigger.text({ exclude: '[popover]' })).toBe('');
    expect(await trigger.getProperty('type')).toBe('button');
    expect(await trigger.getAttribute('aria-label')).toBe('View · sg-40');
    const dialog = await page.dialog();
    if (!dialog) throw new Error('Expected historical grant details');
    const text = await dialog.text();
    expect(text).toContain('Read run metadata');
    expect(text).toContain('sg-40');
    expect(await page.historicalAction()).toBeNull();
    expect(text).toContain('Support-03');
    await dialog.sendKeys(TestKey.ESCAPE);
    await fixture.whenStable();
    expect(await page.dialog()).toBeNull();
    expect(await trigger.isFocused()).toBe(true);
  });
  it('clears grant references on query-driven loss of read access', async () => {
    const { page, params } = await render('default');
    await page.open();
    expect(await (await page.dialog())?.text()).toContain('req-sg-41');
    params.next(convertToParamMap({ state: 'denied' }));
    expect(await page.content()).not.toContain('sg-41');
    expect(await page.dialog()).toBeNull();
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
    await page.open();
    expect(await (await page.dialog())?.text()).toContain('INC-205');
    expect(await (await page.dialog())?.text()).not.toContain('unrendered-canary');
  });
  it('does not turn an approval into an active session', async () => {
    const { page } = await render('approved');
    expect(await page.content()).not.toContain('X-Evidgate-Support-Access · Support-07');
  });
});
