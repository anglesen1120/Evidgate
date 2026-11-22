import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { ComponentHarness } from '@angular/cdk/testing';
import { TestBed } from '@angular/core/testing';
import { ReviewActionPreviewComponent } from './review-action-preview.component';
import { mergeReviewView } from './studio-review-merge.fixture';

class ActionHarness extends ComponentHarness {
  static hostSelector = 'app-review-action-preview';
  readonly trigger = this.locatorFor('button[hlmDialogTrigger]');
}

class DialogHarness extends ComponentHarness {
  static hostSelector = 'hlm-dialog-content';
  readonly close = this.locatorFor('button[hlmDialogClose]');
  readonly confirmation = this.locatorFor('button[data-confirm]');
  readonly text = async () => (await this.host()).text();
}

describe('ReviewActionPreviewComponent', () => {
  it.each(['submit', 'approve', 'changes', 'merge'] as const)(
    'inspects %s without writing a decision',
    async (kind) => {
      const fixture = TestBed.createComponent(ReviewActionPreviewComponent);
      fixture.componentRef.setInput('kind', kind);
      fixture.componentRef.setInput(
        'view',
        mergeReviewView(kind === 'merge' ? 'eligible' : 'default'),
      );
      fixture.detectChanges();
      const action = await TestbedHarnessEnvironment.harnessForFixture(fixture, ActionHarness);
      const doc = TestbedHarnessEnvironment.documentRootLoader(fixture);
      await (await action.trigger()).click();
      const dialog = await doc.getHarness(DialogHarness);
      expect(await dialog.text()).toContain('ah-1f9b…473a');
      expect(await dialog.text()).toContain('Preview only');
      expect(await (await dialog.confirmation()).getProperty<boolean>('disabled')).toBe(true);
      await (await dialog.close()).click();
      expect(await doc.hasHarness(DialogHarness)).toBe(false);
    },
  );
});
