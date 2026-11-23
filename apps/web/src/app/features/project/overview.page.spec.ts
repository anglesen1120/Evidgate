import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import type { RunSummary } from '@evidgate/sdk';
import { SessionStore } from '../../core/session/session.store';
import { NavViewService } from '../../shell/nav/nav-view';
import { ProjectDashboardStore } from './dashboard/project-dashboard.store';
import { ProjectOverviewPage } from './overview.page';

class OverviewHarness extends ComponentHarness {
  static hostSelector = 'app-project-overview';
  readonly emptyTitle = this.locatorForOptional('app-empty-pattern h2');
  readonly emptyActions = this.locatorForAll('app-empty-pattern a, app-empty-pattern button');
}

/** Mount the page against a store whose current project is `project`. */
function mountWith(project: ProjectDto | null, steps = NEXT_STEPS) {
  TestBed.configureTestingModule({
    imports: [ProjectOverviewPage],
    providers: [
      provideZonelessChangeDetection(),
      provideRouter([]),
      {
        provide: NavViewService,
        useValue: { groups: signal([{ id: 'design', label: 'Test design', items: steps }]) },
      },
      { provide: SessionStore, useValue: { currentProject: signal(project) } },
    ],
  });
  const fixture = TestBed.createComponent(ProjectOverviewPage);
  fixture.detectChanges();
  return fixture;
}

function renderWith(project: ProjectDto | null, steps = NEXT_STEPS): HTMLElement {
  return mountWith(project, steps).nativeElement as HTMLElement;
}

describe('ProjectOverviewPage dashboard', () => {
  it('renders all eight independent widgets without treating collecting as PASS', async () => {
    const { fixture } = mount();
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, OverviewHarness);
    expect(await harness.widgets()).toHaveLength(8);
    expect(await harness.text()).toContain('Still collecting');
    expect(await harness.text()).toContain('0%');
    expect(await harness.text()).toContain('Not evaluated');
    expect(await harness.runViews()).toHaveLength(1);
  });

  it('distinguishes successful empty runs from unavailable usage and hides denied widgets', async () => {
    const { fixture } = mount([]);
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, OverviewHarness);
    expect(await harness.text()).toContain('No runs yet.');
    expect(await harness.text()).toContain('No usage metrics returned.');
    expect(await harness.text()).toContain('No data');
    expect(await harness.runViews()).toHaveLength(0);
  });

  it('labels truncated ascending run pages as not latest instead of recent', async () => {
    const { fixture } = mount([RUN], false);
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, OverviewHarness);
    expect(await harness.text()).toContain('oldest loaded runs — not the latest');
    expect(await harness.text()).not.toContain('Recent runs');
  });

  it('shows no restricted facts when every widget is denied', async () => {
    const { fixture } = mount([], true, false);
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, OverviewHarness);
    expect(await harness.widgets()).toHaveLength(0);
    expect(await harness.text()).toContain('No dashboard data is available');
  });
});
