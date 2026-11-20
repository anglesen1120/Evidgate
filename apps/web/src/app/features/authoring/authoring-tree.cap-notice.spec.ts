import { registerLocaleData } from '@angular/common';
import viLocale from '@angular/common/locales/vi';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { LOCALE_ID, provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { NodeDto } from '@evidgate/sdk';
import { RETAINED_ROWS_CAP } from '../../shared/keyset-retention';
import { provideLayoutTestStubs } from '../../shared/testing/layout.testing';
import { AuthoringTreePaneComponent } from './authoring-tree-pane.component';
import { AuthoringTreeHarness } from './authoring-tree.testing';

const root: NodeDto = {
  node_id: '018f0000-0000-7000-8000-000000000101',
  parent_id: null,
  kind: 'module',
  title: 'Module',
  position: 0,
  version: 1,
  child_count: 1,
  item_count: 0,
};

const SCENARIOS = [
  { locale: 'en', cap: 25, figure: '25' },
  { locale: 'vi', cap: 25, figure: '25' },
  { locale: 'en', cap: null, figure: '10,000' },
  { locale: 'vi', cap: null, figure: '10.000' },
] as const;

describe('authoring tree localized cap notice', () => {
  provideLayoutTestStubs();
  registerLocaleData(viLocale);
  afterEach(() => TestBed.resetTestingModule());

  it.each(SCENARIOS)('renders the cap as $figure in $locale', async ({ locale, cap, figure }) => {
    TestBed.configureTestingModule({
      imports: [AuthoringTreePaneComponent],
      providers: [
        provideZonelessChangeDetection(),
        { provide: LOCALE_ID, useValue: locale },
        ...(cap === null ? [] : [{ provide: RETAINED_ROWS_CAP, useValue: cap }]),
      ],
    });
    const fixture = TestBed.createComponent(AuthoringTreePaneComponent);
    fixture.componentRef.setInput('projectId', 'project');
    fixture.componentRef.setInput('roots', {
      items: [root],
      nextCursor: null,
      status: 'capped',
      error: null,
    });
    fixture.detectChanges();
    await fixture.whenStable();
    const harness = await TestbedHarnessEnvironment.harnessForFixture(
      fixture,
      AuthoringTreeHarness,
    );
    const notices = await harness.capped();
    expect(notices).toHaveLength(1);
    expect(await notices[0]?.text()).toContain(`the first ${figure} nodes`);
  });
});
