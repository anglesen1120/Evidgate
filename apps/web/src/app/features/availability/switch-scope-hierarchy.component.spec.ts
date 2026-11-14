import { formatDate } from '@angular/common';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import type { HealingSwitchResolutionView } from '@evidgate/sdk';

import { SessionStore } from '../../core/session/session.store';
import { DataTableComponent } from '../../shared/ui/data-table';
import { resolution, switchRow } from './healing-switch.test-support';
import { SwitchScopeHierarchy } from './switch-scope-hierarchy.component';

function mount(answer: HealingSwitchResolutionView) {
  TestBed.configureTestingModule({
    imports: [SwitchScopeHierarchy],
    providers: [
      provideZonelessChangeDetection(),
      {
        provide: SessionStore,
        useValue: {
          currentProject: signal({
            name: 'Dự án kiểm thử Nguyễn với tên rất dài ' + 'quốc tế '.repeat(8),
          }),
        },
      },
    ],
  });
  const fixture = TestBed.createComponent(SwitchScopeHierarchy);
  fixture.componentRef.setInput('resolution', answer);
  fixture.detectChanges();
  const root = fixture.nativeElement as HTMLElement;
  return { fixture, root };
}

describe('SwitchScopeHierarchy', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('keeps system redactions, absent rows and complete project provenance distinct', async () => {
    const reason = 'Giữ nguyên lý do kiểm thử đầy đủ ' + 'x'.repeat(160);
    const { fixture, root } = mount(
      resolution({
        system: switchRow('system', 'shadow', { set_by: null, reason: null }),
        tenant: null,
        project: switchRow('project', 'enabled', { set_by: 'Nguyễn Thị Ánh', reason }),
      }),
    );
    await fixture.whenStable();
    expect(root.textContent).toContain('Hidden');
    expect(root.textContent).toContain('Not recorded');
    expect(root.textContent).not.toContain('Number unavailable');
    expect(root.textContent).toContain('No switch is stored at this scope.');
    expect(root.textContent).toContain('Nguyễn Thị Ánh');
    expect(root.textContent).toContain(reason);
    expect(root.textContent).toContain('Dự án kiểm thử Nguyễn với tên rất dài');
    expect(
      [...root.querySelectorAll('[data-healing-scope]')].map((element) =>
        element.getAttribute('data-healing-scope'),
      ),
    ).toEqual(['system', 'tenant', 'project']);
    expect(root.querySelector('input, textarea, select, [data-healing-save]')).toBeNull();
  });

  it('renders the original offset instant as an explicitly labelled absolute UTC time', async () => {
    const at = '2026-11-14T02:30:00-05:00';
    const { fixture, root } = mount(resolution({ tenant: switchRow('tenant', 'shadow', { at }) }));
    await fixture.whenStable();
    const time = root.querySelector(`time[datetime="${at}"]`);
    expect(time?.textContent?.replace(/\s+/g, ' ').trim()).toBe(
      `${formatDate(at, 'medium', 'en-US', 'UTC')} UTC`.replace(/\s+/g, ' '),
    );
  });

  it('does not turn a malformed instant into a local time or invented date', async () => {
    const { fixture, root } = mount(
      resolution({
        tenant: switchRow('tenant', 'shadow', { at: 'not-an-instant' }),
      }),
    );
    await fixture.whenStable();
    expect(root.textContent).toContain('Time unavailable');
    expect(root.querySelector('time[datetime="not-an-instant"]')).toBeNull();
  });

  it('uses the shared labelled record view without dropping secondary facts', async () => {
    const { fixture, root } = mount(
      resolution({
        project: switchRow('project', 'enabled', {
          set_by: 'Nguyễn',
          reason: 'Lý do đầy đủ',
          version: 0,
        }),
      }),
    );
    await fixture.whenStable();
    const table = fixture.debugElement
      .query(By.directive(DataTableComponent))
      .injector.get(DataTableComponent);
    table.narrow.set(true);
    fixture.detectChanges();
    await fixture.whenStable();
    const records = root.querySelector('app-data-table-records');
    expect(records).not.toBeNull();
    for (const fact of [
      'Mode',
      'Set by',
      'Reason',
      'Last changed',
      'Version',
      'Nguyễn',
      'Lý do đầy đủ',
    ]) {
      expect(records?.textContent).toContain(fact);
    }
    expect(records?.textContent).toContain('0');
    expect(records?.querySelector('input, textarea, select, [data-healing-save]')).toBeNull();
  });
});
