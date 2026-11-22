import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { InteractivityChecker } from '@angular/cdk/a11y';
import { TestBed } from '@angular/core/testing';
import { stubLayoutApis } from '../../../shared/testing/layout.testing';
import { DecisionPreview } from './decision-preview.component';
import { DecisionDialogHarness, DecisionReviewHarness } from './decision-review.harness';

describe('DecisionPreview', () => {
  beforeEach(() => {
    stubLayoutApis();
    TestBed.configureTestingModule({ imports: [DecisionPreview] });
    vi.spyOn(TestBed.inject(InteractivityChecker), 'isVisible').mockReturnValue(true);
  });
  afterEach(() => {
    TestBed.resetTestingModule();
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
  });
  it.each(['request', 'approve', 'decline', 'revoke', 'remove', 'add'])(
    'opens and dismisses the %s preview without a mutation',
    async (action) => {
      const fixture = TestBed.createComponent(DecisionPreview);
      fixture.componentRef.setInput('action', action);
      const loader = TestbedHarnessEnvironment.documentRootLoader(fixture);
      const h = await TestbedHarnessEnvironment.harnessForFixture(fixture, DecisionReviewHarness);
      await (await h.find('button')).click();
      const dialog = await loader.getHarness(DecisionDialogHarness);
      expect(await dialog.text()).toContain('No request is sent.');
      if (action === 'decline')
        expect(await dialog.text()).toContain('No decline endpoint is specified');
      await (await dialog.find('[data-cancel]')).click();
      expect(await loader.getAllHarnesses(DecisionDialogHarness)).toHaveLength(0);
    },
  );
  it('finishes the approval preview without claiming approval or clearing the recorded failure', async () => {
    const fixture = TestBed.createComponent(DecisionPreview);
    fixture.componentRef.setInput('action', 'approve');
    const h = await TestbedHarnessEnvironment.harnessForFixture(fixture, DecisionReviewHarness);
    const loader = TestbedHarnessEnvironment.documentRootLoader(fixture);
    await (await h.find('button')).click();
    const dialog = await loader.getHarness(DecisionDialogHarness);
    expect(await dialog.text()).toContain('the case remains failed');
    await (await dialog.find('button[type=submit]')).click();
    expect(await (await h.find('[role=status]')).text()).toContain(
      'No exception or stored evaluation was changed.',
    );
    expect(await loader.getAllHarnesses(DecisionDialogHarness)).toHaveLength(0);
  });
});
