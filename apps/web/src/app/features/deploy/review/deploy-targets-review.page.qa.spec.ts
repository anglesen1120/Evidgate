import { ComponentHarness, TestKey } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter, Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { BehaviorSubject } from 'rxjs';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow';
import { deployTargetsFixture } from './deploy-targets-review.fixture';
import { DeployTargetsReviewPage } from './deploy-targets-review.page';

class DeployBoundaryHarness extends ComponentHarness {
  static hostSelector = 'app-deploy-targets-review';
  readonly edit = this.locatorFor('[data-edit="production"]');
  readonly view = (key: string) => this.locatorFor(`[data-view="${key}"]`)();
}

class DeployDraftBoundaryHarness extends ComponentHarness {
  static hostSelector = '[role="dialog"]';
  readonly order = this.locatorFor('#target-order');
  readonly acknowledge = this.locatorFor('hlm-checkbox button');
  readonly submit = this.locatorFor('[data-preview-draft]');
  readonly cancel = this.locatorFor('#draft-cancel');
  readonly keep = this.locatorFor('#draft-keep');
  readonly discard = this.locatorFor('#draft-discard');
  readonly close = this.locatorFor('[data-close-record]');
  async changeOrder(value: string): Promise<void> {
    const order = await this.order();
    await order.setInputValue(value);
    await order.dispatchEvent('input');
  }
}

provideLayoutTestStubs();

async function mount() {
  const params = new BehaviorSubject(convertToParamMap({ section: 'admin-preview' }));
  const desktop = signal(true);
  let permitsSubmit = true;
  TestBed.configureTestingModule({
    providers: [
      { provide: ActivatedRoute, useValue: { queryParamMap: params } },
      {
        provide: NarrowMutationPolicy,
        useValue: {
          canMutate: desktop,
          narrow: () => !desktop(),
          allowsMutation: () => desktop() && permitsSubmit,
        },
      },
    ],
  });
  const fixture = TestBed.createComponent(DeployTargetsReviewPage);
  await fixture.whenStable();
  return {
    fixture,
    desktop,
    page: await TestbedHarnessEnvironment.harnessForFixture(fixture, DeployBoundaryHarness),
    root: TestbedHarnessEnvironment.documentRootLoader(fixture),
    revokeSubmit: () => {
      permitsSubmit = false;
    },
  };
}

const scope = '/review/orgs/lotus/projects/p-storefront';
async function mountRouted() {
  const desktop = signal(true);
  TestBed.configureTestingModule({
    providers: [
      provideRouter([
        { path: 'review/orgs/:orgId/projects/:projectId', component: DeployTargetsReviewPage },
      ]),
      {
        provide: NarrowMutationPolicy,
        useValue: { canMutate: desktop, narrow: () => !desktop(), allowsMutation: desktop },
      },
    ],
  });
  const routed = await RouterTestingHarness.create(scope + '?section=admin-preview');
  return {
    page: await TestbedHarnessEnvironment.loader(routed.fixture).getHarness(DeployBoundaryHarness),
    root: TestbedHarnessEnvironment.documentRootLoader(routed.fixture),
    router: TestBed.inject(Router),
  };
}

describe('Deploy target edit-preview boundaries', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('guards a same-route query navigation while dirty instead of silently losing the draft', async () => {
    const { page, root, router } = await mountRouted();
    await router.navigateByUrl(scope + '?section=admin-preview&release=rel-2026.10.12');
    await (await page.edit()).click();
    const draft = await root.getHarness(DeployDraftBoundaryHarness);
    await draft.changeOrder('4');
    await router.navigateByUrl(scope + '?section=admin-preview');
    expect(await root.hasHarness(DeployDraftBoundaryHarness)).toBe(true);
    expect(await (await draft.keep()).isFocused()).toBe(true);
    await (await draft.keep()).click();
    expect(await (await draft.order()).getProperty('value')).toBe('4');
  });

  it('treats Escape from discard confirmation as Keep editing without losing the draft', async () => {
    const { page, root } = await mount();
    const opener = await page.edit();
    await opener.click();
    const draft = await root.getHarness(DeployDraftBoundaryHarness);
    await draft.changeOrder('4');
    await (await draft.cancel()).click();
    const keep = await draft.keep();
    expect(await keep.isFocused()).toBe(true);
    await keep.sendKeys(TestKey.ESCAPE);
    expect(await (await draft.order()).getProperty('value')).toBe('4');
    expect(await (await draft.order()).isFocused()).toBe(true);
    await (await draft.close()).click();
    await (await draft.discard()).click();
    expect(await root.hasHarness(DeployDraftBoundaryHarness)).toBe(false);
    expect(await opener.isFocused()).toBe(true);
  });

  it.each(['', '0', '21', '1.5', '1', '20'])(
    'keeps order %s within the existing integer 1–20 preview contract',
    async (value) => {
      const { page, root } = await mount();
      await (await page.edit()).click();
      const draft = await root.getHarness(DeployDraftBoundaryHarness);
      await (await draft.acknowledge()).click();
      await draft.changeOrder(value);
      expect(await (await draft.submit()).getProperty('disabled')).toBe(
        !['1', '20'].includes(value),
      );
    },
  );

  it.each([true, false])('rechecks immediate submit permission: %s', async (allowed) => {
    const { page, root, revokeSubmit } = await mount();
    await (await page.edit()).click();
    const draft = await root.getHarness(DeployDraftBoundaryHarness);
    await draft.changeOrder('4');
    await (await draft.acknowledge()).click();
    const submit = await draft.submit();
    expect(await submit.getProperty('disabled')).toBe(false);
    if (!allowed) revokeSubmit();
    await submit.click();
    expect(await root.hasHarness(DeployDraftBoundaryHarness)).toBe(!allowed);
    expect(await (await page.host()).text().then((text) => text.includes('Draft reviewed'))).toBe(
      allowed,
    );
  });

  it('returns to the nearest surviving target after the last target disappears', async () => {
    const { page, root, fixture } = await mount();
    const nearest = await page.view('preproduction');
    await (await page.view('production')).click();
    expect(await root.hasHarness(DeployDraftBoundaryHarness)).toBe(true);
    fixture.componentRef.setInput('viewModel', {
      ...deployTargetsFixture,
      targets: deployTargetsFixture.targets.filter((target) => target.key !== 'production'),
    });
    expect(await root.hasHarness(DeployDraftBoundaryHarness)).toBe(false);
    expect(await nearest.isFocused()).toBe(true);
  });
});
