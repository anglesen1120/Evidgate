import { InteractivityChecker } from '@angular/cdk/a11y';
import { ComponentHarness, TestKey } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap } from '@angular/router';
import { BehaviorSubject } from 'rxjs';
import { vi } from 'vitest';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow/narrow-mutation-policy';
import { DeployTargetsReviewPage } from './deploy-targets-review.page';

class PageHarness extends ComponentHarness {
  static hostSelector = 'app-deploy-targets-review';
  readonly title = this.locatorFor('h1');
  readonly view = this.locatorFor('button[data-view="production"]');
  readonly pick = this.locatorFor('button[data-view="preproduction"]');
  readonly edit = this.locatorFor('button[data-edit="production"]');
  readonly caption = this.locatorFor('table caption');
  readonly headers = this.locatorForAll('thead th');
  readonly selected = this.locatorFor('tr[data-state="selected"] th[scope="row"]');
  readonly content = async () => (await this.host()).text();
}

class RecordHarness extends ComponentHarness {
  static hostSelector = '[role="dialog"]';
  readonly heading = this.locatorFor('h2[data-dialog-heading]');
  readonly edit = this.locatorFor('[data-edit-preview]');
  readonly close = this.locatorFor('[data-close-record]');
  readonly footerClose = this.locatorFor('footer button:first-of-type');
  readonly cancel = this.locatorFor('#draft-cancel');
  readonly keep = this.locatorFor('#draft-keep');
  readonly discard = this.locatorFor('#draft-discard');
  readonly order = this.locatorFor('#target-order');
  async changeOrder(value: string): Promise<void> {
    const order = await this.order();
    // Testbed's setInputValue sets the DOM property, not the native input event.
    await order.setInputValue(value);
    await order.dispatchEvent('input');
  }
  readonly acknowledge = this.locatorFor('hlm-checkbox button');
  readonly submit = this.locatorFor('[data-preview-draft]');
  readonly filter = this.locatorFor('#release-filter');
  readonly clearFilter = this.locatorFor('app-empty-pattern button');
  readonly badge = this.locatorFor('[data-inspect-status] app-status-pill');
  readonly badgeIcon = this.locatorFor('[data-inspect-status] app-status-pill hlm-icon');
  readonly tone = this.locatorFor('[data-inspect-status] .status-pill');
  readonly details = this.locatorFor('[data-record-detail]');
  readonly content = async () => (await this.host()).text();
}

class DialogPaneHarness extends ComponentHarness {
  static hostSelector = '.cdk-overlay-pane:has([role="dialog"])';
  readonly boundaries = this.locatorForAll('.cdk-focus-trap-anchor');
}

class OverlayBackdropHarness extends ComponentHarness {
  static hostSelector = '.cdk-overlay-backdrop';
}

class ReleaseListHarness extends ComponentHarness {
  static hostSelector = '[role="listbox"]';
  readonly priorRelease = this.locatorFor('[data-value="rel-2026.10.12"]');
}

describe('DeployTargetsReviewPage', () => {
  provideLayoutTestStubs();

  async function render(state: string, preview = false) {
    const params = new BehaviorSubject(
      convertToParamMap({ state, ...(preview ? { section: 'admin-preview' } : {}) }),
    );
    const desktop = signal(true);
    TestBed.configureTestingModule({
      imports: [DeployTargetsReviewPage],
      providers: [
        { provide: ActivatedRoute, useValue: { queryParamMap: params } },
        {
          provide: NarrowMutationPolicy,
          useValue: {
            canMutate: desktop,
            narrow: () => !desktop(),
            allowsMutation: () => desktop(),
          },
        },
      ],
    });
    const fixture = TestBed.createComponent(DeployTargetsReviewPage);
    fixture.detectChanges();
    const root = TestbedHarnessEnvironment.documentRootLoader(fixture);
    return {
      page: await TestbedHarnessEnvironment.harnessForFixture(fixture, PageHarness),
      params,
      record: () => root.getHarness(RecordHarness),
      hasRecord: () => root.hasHarness(RecordHarness),
      pane: () => root.getHarness(DialogPaneHarness),
      backdrop: () => root.getHarness(OverlayBackdropHarness),
      releaseList: () => root.getHarness(ReleaseListHarness),
      desktop,
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
    const { page, record } = await render(state);
    expect(await (await page.title()).text()).toBe('Deploy targets');
    if (['default', 'no-match', 'empty-history', 'long'].includes(state)) {
      await (await page.view()).click();
      expect(await (await record()).content()).toContain(expected);
    } else {
      expect(await page.content()).toContain(expected);
    }
  });
  it('keeps pending neutral and separate from a recorded release decision', async () => {
    const { page, record } = await render('default');
    await (await page.view()).click();
    const dialog = await record();
    expect(await (await dialog.badge()).text()).toContain('Not evaluated');
    expect(await (await dialog.badgeIcon()).getAttribute('aria-hidden')).toBe('true');
    expect(await (await dialog.tone()).hasClass('status-pill--neutral')).toBe(true);
    const text = await dialog.content();
    expect(text).toContain('No decision');
    expect(text).toContain('rd-14');
    expect(text).not.toContain('PASS');
  });
  it('does not borrow production history for a target with no checks', async () => {
    const { page, record } = await render('ready');
    await (await page.pick()).click();
    const dialog = await record();
    expect(await (await dialog.details()).text()).toContain('Pre-production');
    expect(await dialog.content()).not.toContain('pc-91');
    expect(await dialog.content()).toContain('No promotion checks');
  });
  it('retains safe target context on partial history failure but not on denial', async () => {
    const { page, params, record, hasRecord } = await render('error');
    await (await page.view()).click();
    const dialog = await record();
    expect(await dialog.content()).toContain('dt-6');
    expect(await dialog.content()).toContain('Promotion history could not be loaded');
    expect(await dialog.content()).not.toContain('pc-91');
    params.next(convertToParamMap({ state: 'denied' }));
    expect(await page.content()).not.toContain('dt-6');
    expect(await page.content()).not.toContain('storefront-web');
    expect(await hasRecord()).toBe(false);
  });

  it('opens a captioned row in a labelled View and restores focus without changing selection', async () => {
    const { page, record, hasRecord } = await render('ready');
    expect(await page.content()).toContain('storefront-web · Ordered targets: 3');
    expect(await page.content()).toContain('Records shown: 3');
    expect(await (await page.caption()).text()).toBe('Target sequence');
    expect(await Promise.all((await page.headers()).map((h) => h.text()))).toEqual([
      'Target',
      'Target order',
      'Policy',
      'Pinned policy revision',
      'Actions',
    ]);
    const opener = await page.pick();
    await opener.focus();
    await opener.click();
    const dialog = await record();
    const heading = await dialog.heading();
    expect(await (await dialog.host()).getAttribute('aria-labelledby')).toBe(
      await heading.getAttribute('id'),
    );
    expect(await heading.isFocused()).toBe(true);
    await heading.sendKeys(TestKey.ESCAPE);
    expect(await hasRecord()).toBe(false);
    expect(await opener.isFocused()).toBe(true);
    expect(await (await page.selected()).text()).toContain('Pre-production');
  });

  it('cycles focus back into View at both keyboard boundaries', async () => {
    const { page, record, pane } = await render('ready');
    // JSDOM has no layout boxes; exercise CDK's real traversal on the visible fixture controls.
    const visible = vi
      .spyOn(TestBed.inject(InteractivityChecker), 'isVisible')
      .mockReturnValue(true);
    try {
      await (await page.pick()).click();
      const dialog = await record();
      const anchors = await (await pane()).boundaries();
      expect(anchors).toHaveLength(2);
      await anchors[1]!.focus();
      expect(await (await dialog.close()).isFocused()).toBe(true);
      await anchors[0]!.focus();
      expect(await (await dialog.footerClose()).isFocused()).toBe(true);
    } finally {
      visible.mockRestore();
    }
  });

  it.each(['cancel', 'close', 'escape', 'backdrop', 'record switch'])(
    'guards dirty %s and keeps or discards the draft explicitly',
    async (exit) => {
      const { page, record, hasRecord, backdrop } = await render('ready', true);
      const opener = await page.edit();
      await opener.focus();
      await opener.click();
      const dialog = await record();
      const heading = await dialog.heading();
      expect(await (await dialog.host()).getAttribute('aria-labelledby')).toBe(
        await heading.getAttribute('id'),
      );
      expect(await heading.text()).toContain('Preview');
      const order = await dialog.order();
      expect(await order.isFocused()).toBe(true);
      await dialog.changeOrder('4');
      if (exit === 'escape') await order.sendKeys(TestKey.ESCAPE);
      if (exit === 'cancel') await (await dialog.cancel()).click();
      if (exit === 'close') await (await dialog.close()).click();
      if (exit === 'backdrop') await (await (await backdrop()).host()).click();
      if (exit === 'record switch') await (await page.pick()).click();
      expect(await (await dialog.heading()).text()).toBe('Discard preview changes?');
      expect(await (await dialog.keep()).isFocused()).toBe(true);
      await (await dialog.keep()).click();
      expect(await (await dialog.order()).getProperty('value')).toBe('4');
      expect(await (await dialog.order()).isFocused()).toBe(true);
      await (await dialog.cancel()).click();
      await (await dialog.discard()).click();
      expect(await hasRecord()).toBe(false);
      expect(await opener.isFocused()).toBe(true);
      expect(await page.content()).not.toContain('Draft reviewed');
      await opener.click();
      expect(await (await (await record()).order()).getProperty('value')).toBe('3');
    },
  );

  it('moves from View to Edit preview in one dialog and reviews without changing the snapshot', async () => {
    const { page, record, hasRecord } = await render('ready', true);
    const opener = await page.view();
    await opener.focus();
    await opener.click();
    const dialog = await record();
    await (await dialog.edit()).click();
    expect(await (await dialog.order()).isFocused()).toBe(true);
    expect(await (await dialog.submit()).getProperty('disabled')).toBe(true);
    await (await dialog.acknowledge()).click();
    expect(await (await dialog.submit()).getProperty('disabled')).toBe(true);
    await dialog.changeOrder('4');
    await (await dialog.submit()).click();
    expect(await hasRecord()).toBe(false);
    expect(await opener.isFocused()).toBe(true);
    expect(await page.content()).toContain('Draft reviewed');
    await opener.click();
    expect(await (await record()).content()).toContain('pc-91');
  });

  it('keeps the release filter in context across open, Escape from the picker, and return', async () => {
    const { page, record, releaseList, hasRecord } = await render('no-match');
    const opener = await page.view();
    await opener.click();
    let dialog = await record();
    expect(await dialog.content()).toContain('No promotion checks match this release');
    await (await dialog.clearFilter()).click();
    const filter = await dialog.filter();
    await filter.click();
    await filter.sendKeys(TestKey.ESCAPE);
    expect(await filter.getAttribute('aria-expanded')).toBe('false');
    expect(await hasRecord()).toBe(true);
    await filter.click();
    await (await (await releaseList()).priorRelease()).click();
    expect(await dialog.content()).toContain('pc-89');
    expect(await dialog.content()).not.toContain('pc-91');
    await (await dialog.close()).click();
    await opener.click();
    dialog = await record();
    expect(await (await dialog.filter()).text()).toContain('rel-2026.10.12');
    expect(await dialog.content()).not.toContain('pc-91');
  });

  it('drops an edit on losing desktop eligibility and returns to the readable target', async () => {
    const { page, record, hasRecord, desktop } = await render('ready', true);
    await (await page.edit()).click();
    await (await record()).changeOrder('4');
    desktop.set(false);
    expect(await hasRecord()).toBe(false);
    const view = await page.view();
    expect(await view.isFocused()).toBe(true);
    await view.click();
    expect(await (await record()).content()).toContain('Desktop required');
    expect(await page.content()).not.toContain('Draft reviewed');
  });
});
