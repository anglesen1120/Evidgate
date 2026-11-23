import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';
import type { AuditEntryDto } from '@evidgate/sdk';
import { DashboardActivityComponent } from './dashboard-activity.component';

class ActivityHarness extends ComponentHarness {
  static hostSelector = 'app-dashboard-activity';
  readonly text = async () => (await this.host()).text();
  readonly view = this.locatorFor('button[aria-haspopup="dialog"]');
  readonly empty = this.locatorForOptional('app-empty-pattern');
  readonly loading = this.locatorForOptional('app-loading-pattern');
  readonly emptyIcon = this.locatorFor('app-empty-pattern hlm-icon path');
}
class EventDialogHarness extends ComponentHarness {
  static hostSelector = '[role="dialog"]';
  readonly text = async () => (await this.host()).text();
}
const event: AuditEntryDto = {
  id: 'event1',
  seq: 1,
  actor_type: 'system',
  actor_id: 'worker1',
  action: 'run.created',
  object_id: 'run1',
  prev_hash: '',
  entry_hash: 'abc',
  created_at: '2026-11-23T00:00:00Z',
};

describe('DashboardActivityComponent', () => {
  it('keeps loading and successful empty activity distinct', async () => {
    const fixture = TestBed.createComponent(DashboardActivityComponent);
    fixture.componentRef.setInput('rows', []);
    fixture.componentRef.setInput('loading', true);
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, ActivityHarness);
    expect(await harness.loading()).not.toBeNull();
    expect(await harness.empty()).toBeNull();
    fixture.componentRef.setInput('loading', false);
    expect(await harness.empty()).not.toBeNull();
    expect(await (await harness.emptyIcon()).getAttribute('d')).toBe(
      'M7 4h10v16H7zM10 8h4M10 12h4M10 16h4',
    );
  });
  it('opens loaded event details and removes the selection when rows change', async () => {
    const fixture = TestBed.createComponent(DashboardActivityComponent);
    fixture.componentRef.setInput('rows', [event]);
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, ActivityHarness);
    expect(await harness.text()).toContain('run.created');
    await (await harness.view()).click();
    const root = TestbedHarnessEnvironment.documentRootLoader(fixture);
    const dialog = await root.getHarness(EventDialogHarness);
    expect(await dialog.text()).toContain('event1');
    expect(await dialog.text()).toContain('system');
    fixture.componentRef.setInput('rows', []);
    expect(await root.getHarnessOrNull(EventDialogHarness)).toBeNull();
  });
});
