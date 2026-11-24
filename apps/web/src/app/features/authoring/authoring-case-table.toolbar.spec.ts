import { ComponentHarness, manualChangeDetection } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { signal } from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import type { ManualCaseListItemDto, ManualCaseListResponse } from '@evidgate/sdk';
import { BehaviorSubject, firstValueFrom, of, Subject } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import { provideLayoutTestStubs } from '../../shared/testing/layout.testing';
import { NarrowMutationPolicy } from '../../shared/ui/narrow/narrow-mutation-policy';
import { AuthoringCaseTableComponent } from './authoring-case-table.component';
import { AuthoringCaseTableStore } from './authoring-case-table.store';
import { AuthoringDataService } from './authoring-data.service';

class ToolbarHarness extends ComponentHarness {
  static hostSelector = 'app-authoring-case-table';
  readonly selection = this.locatorFor('[data-select-visible] [role="checkbox"]');
  readonly counts = this.locatorForAll('[data-selection-summary]');
  readonly scope = this.locatorFor('app-field-hint [popover]#authoring-selection-help');
  readonly action = this.locatorForOptional('button[data-export-selection]');
  readonly reason = this.locatorFor('app-field-hint [popover]#authoring-export-help');
  readonly rowsAfterSelection = this.locatorFor('[data-select-visible] ~ app-data-table');
  readonly finalPager = this.locatorFor(':scope > :last-child > app-data-table-pager:last-child');
  readonly refresh = this.locatorFor('button[data-case-refresh]');
  readonly refreshIcon = this.locatorFor('button[data-case-refresh] hlm-icon');
  readonly refreshTooltip = this.locatorFor('button[data-case-refresh] [popover]');
}

const row: ManualCaseListItemDto = {
  case_id: '018f0000-0000-7000-8000-000000000001',
  code: 'CASE-1',
  node_id: '018f0000-0000-7000-8000-000000000101',
  checklist_item_id: '018f0000-0000-7000-8000-000000000201',
  title: 'First case',
  state: 'draft',
  priority: 'high',
  version: 1,
  linked_live_count: 0,
  linked_case_ids: [],
};

describe('Case table toolbar', () => {
  provideLayoutTestStubs();
  let fixture: ComponentFixture<AuthoringCaseTableComponent>;
  async function mount(
    list = vi.fn<AuthoringDataService['listManualCases']>().mockResolvedValue({
      items: [row],
      next_cursor: null,
    }),
    query = new BehaviorSubject(convertToParamMap({})),
  ) {
    TestBed.configureTestingModule({
      imports: [AuthoringCaseTableComponent],
      providers: [
        provideRouter([]),
        {
          provide: ActivatedRoute,
          useValue: {
            queryParamMap: query,
            pathFromRoot: [{ paramMap: of(convertToParamMap({ projectId: 'project' })) }],
          },
        },
        {
          provide: NarrowMutationPolicy,
          useValue: { canMutate: signal(true), narrow: signal(false), allowsMutation: () => true },
        },
        {
          provide: AuthoringDataService,
          useValue: { listManualCases: list },
        },
      ],
    });
    fixture = TestBed.createComponent(AuthoringCaseTableComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    return TestbedHarnessEnvironment.harnessForFixture(fixture, ToolbarHarness);
  }

  it('shows one visible count with the full scope only in its disclosure', async () => {
    const bar = await mount();
    const counts = await bar.counts();
    expect(counts).toHaveLength(1);
    expect(await counts[0]?.text()).toBe('Selected 0 / 1 visible cases');
    const description = await (await bar.scope()).text();
    expect(description).toContain('not unloaded cases or hidden record pages');
    expect(await (await bar.host()).text({ exclude: '[popover]' })).not.toContain(description);
    expect(await (await bar.host()).text()).not.toContain('Pending change count unavailable');
    expect(await (await bar.selection()).getAttribute('aria-describedby')).toBe(
      'authoring-selection-help',
    );
  });

  it('offers no actionable export even after a selection, but keeps the reason accessible', async () => {
    const bar = await mount();
    expect(await bar.action()).toBeNull();
    await (await bar.selection()).click();
    const action = await bar.action();
    if (!action) throw new Error('Missing selected-case export control');
    expect(await action.getAttribute('aria-disabled')).toBe('true');
    expect(await action.getProperty('tabIndex')).toBe(0);
    await action.focus();
    expect(await action.isFocused()).toBe(true);
    expect(await action.getAttribute('aria-describedby')).toBe('authoring-export-help');
    expect(await (await bar.reason()).text()).toContain('No broader scope will be exported');
    await action.click();
    expect(await (await bar.counts())[0]?.text()).toBe('Selected 1 / 1 visible cases');
  });

  it('keeps refresh focusable without dispatching a reload while a filtered read retains rows', async () => {
    const query = new BehaviorSubject(convertToParamMap({}));
    const pending = new Subject<ManualCaseListResponse>();
    const list = vi
      .fn<AuthoringDataService['listManualCases']>()
      .mockResolvedValueOnce({ items: [row], next_cursor: null })
      .mockReturnValueOnce(firstValueFrom(pending))
      .mockResolvedValueOnce({ items: [row], next_cursor: null });
    const bar = await mount(list, query);
    const reload = vi.spyOn(fixture.debugElement.injector.get(AuthoringCaseTableStore), 'reload');
    await (await bar.refresh()).focus();
    await manualChangeDetection(async () => {
      query.next(convertToParamMap({ q: 'First' }));
      fixture.detectChanges();
      await vi.waitFor(() => expect(list).toHaveBeenCalledTimes(2));
      fixture.detectChanges();
      const refresh = await bar.refresh();
      expect(await refresh.getAttribute('aria-disabled')).toBe('true');
      expect(await refresh.getProperty('tabIndex')).toBe(0);
      expect(await refresh.isFocused()).toBe(true);
      await refresh.click();
      expect(reload).not.toHaveBeenCalled();
      pending.error(new Error('Read failed'));
    });
    await fixture.whenStable();
    const refresh = await bar.refresh();
    expect(await refresh.getAttribute('aria-disabled')).toBe('false');
    await refresh.click();
    expect(reload).toHaveBeenCalledTimes(1);
    expect(list).toHaveBeenCalledTimes(3);
  });

  it('places selection before rows, leaves the pager last, and names the refresh icon and tooltip', async () => {
    const bar = await mount();
    expect(await bar.rowsAfterSelection()).toBeTruthy();
    expect(await bar.finalPager()).toBeTruthy();
    const refresh = await bar.refresh();
    expect(await refresh.getAttribute('aria-label')).toBe('Refresh cases');
    expect(await refresh.text({ exclude: '[popover]' })).toBe('');
    expect(await (await bar.refreshIcon()).getAttribute('aria-hidden')).toBe('true');
    expect(await (await bar.refreshTooltip()).text()).toBe('Refresh cases');
  });
});
