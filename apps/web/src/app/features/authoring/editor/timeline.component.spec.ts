import { manualChangeDetection } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { DeferBlockBehavior, TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap } from '@angular/router';
import type { AuthoringVersionListResponse, ManualCaseDetailDto } from '@evidgate/sdk';
import type { Mock } from 'vitest';
import { firstValueFrom, of, Subject } from 'rxjs';
import {
  AuthoringEditorDataService,
  type AuthoringVersionsQuery,
} from '../../../core/data/authoring-editor-data.service';
import { SessionStore } from '../../../core/session/session.store';
import { ApiRequestError } from '../../../core/http/api-error';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow';
import { EditorStore } from './editor.store';
import { EDITOR_WRITE } from './editor.model';
import { TimelineComponent } from './timeline.component';
import { TimelineStore } from './timeline.store';
import { apiError } from '../../../shared/testing/http-data.testing';
import { CASE_ID, version, TimelineHarness, RestoreHarness } from './timeline.testing';

provideLayoutTestStubs();

describe('TimelineComponent', () => {
  const writable = signal(true);
  const narrow = signal(false);
  const versions = [version(3), version(2), version(1)];
  let appended = false;
  let data: {
    readCase: Mock;
    updateCase: Mock;
    versions: Mock;
    version: Mock;
    restoreVersion: Mock;
  };
  beforeEach(() => {
    writable.set(true);
    narrow.set(false);
    appended = false;
    data = {
      readCase: vi.fn().mockResolvedValue({
        view: {
          case_id: CASE_ID,
          version: 3,
          title: 'Case',
          description: null,
          precondition: null,
          priority: 'high',
          steps: [{ no: 1, action: 'A', expected: 'B' }],
          state: 'draft',
          code: 'AUTH-001',
          checklist_item_id: 'item',
          node_id: 'node',
          created_at: versions[0]?.created_at,
          updated_at: versions[0]?.created_at,
        } as ManualCaseDetailDto,
        etag: '"3"',
      }),
      updateCase: vi.fn(),
      versions: vi
        .fn()
        .mockImplementation((_project: string, _caseId: string, query: AuthoringVersionsQuery) =>
          Promise.resolve({
            items: query.cursor
              ? [versions[2]]
              : appended
                ? [version(4), ...versions.slice(0, 2)]
                : versions.slice(0, 2),
            next_cursor: query.cursor ? null : 'opaque-older',
          }),
        ),
      version: vi.fn().mockImplementation((_project: string, id: string) => {
        const view = versions.find((entry) => entry.version_id === id) ?? version(4);
        return Promise.resolve({ view, etag: '"' + view.content_hash + '"' });
      }),
      restoreVersion: vi.fn().mockImplementation(() => {
        appended = true;
        return Promise.resolve({ version: 4 });
      }),
    };
    TestBed.configureTestingModule({
      imports: [TimelineComponent],
      deferBlockBehavior: DeferBlockBehavior.Playthrough,
      providers: [
        provideZonelessChangeDetection(),
        EditorStore,
        {
          provide: ActivatedRoute,
          useValue: { paramMap: of(convertToParamMap({ caseId: CASE_ID })) },
        },
        { provide: AuthoringEditorDataService, useValue: data },
        {
          provide: SessionStore,
          useValue: {
            currentOrg: signal({ id: 'o1' }),
            currentProject: signal({ id: 'p1' }),
            has: (code: string) => (code === EDITOR_WRITE ? writable() : true),
          },
        },
        {
          provide: NarrowMutationPolicy,
          useValue: { narrow, canMutate: () => !narrow(), allowsMutation: () => !narrow() },
        },
      ],
    });
  });
  afterEach(() => TestBed.resetTestingModule());
  async function mount() {
    const fixture = TestBed.createComponent(TimelineComponent);
    await fixture.whenStable();
    return {
      fixture,
      harness: await TestbedHarnessEnvironment.harnessForFixture(fixture, TimelineHarness),
    };
  }
  it('loads newest first and replaces keyset pages using only the returned cursor', async () => {
    const { harness } = await mount();
    expect(await harness.ids()).toEqual(['version-3', 'version-2']);
    await harness.click('app-data-table-pager button[data-page="next"]');
    expect(await harness.ids()).toEqual(['version-1']);
    expect(data.versions).toHaveBeenLastCalledWith(
      'p1',
      CASE_ID,
      { limit: 20, cursor: 'opaque-older' },
      expect.any(AbortSignal),
    );
    await harness.click('app-data-table-pager button[data-page="previous"]');
    expect(await harness.ids()).toEqual(['version-3', 'version-2']);
    expect(data.versions).toHaveBeenLastCalledWith(
      'p1',
      CASE_ID,
      { limit: 20 },
      expect.any(AbortSignal),
    );
  });
  it.each(['success', 'retry'] as const)(
    'retains one first-page filter request and its selected snapshot on %s',
    async (outcome) => {
      const { fixture, harness } = await mount();
      const store = fixture.debugElement.injector.get(TimelineStore);
      await harness.click('[data-inspect-version="version-3"]');
      await harness.click('app-data-table-pager button[data-page="next"]');
      const pending = new Subject<AuthoringVersionListResponse>();
      data.versions.mockReturnValueOnce(firstValueFrom(pending));
      await manualChangeDetection(async () => {
        for (const value of ['R', 'Re', 'Reason 3']) {
          await harness.filter(value);
          fixture.detectChanges();
          await vi.waitFor(() => expect(data.versions).toHaveBeenCalledTimes(3));
        }
        expect(store.page()).toBe(2);
        expect(store.rows()[0]?.version_no).toBe(1);
        expect(store.selected()).toBe('version-3');
        expect(data.versions.mock.lastCall?.[2]).toEqual({ limit: 20 });
        if (outcome === 'retry') pending.error(apiError('unavailable', 503));
        else pending.next({ items: versions.slice(0, 2), next_cursor: 'opaque-older' });
      });
      await fixture.whenStable();
      if (outcome === 'retry') {
        expect(store.page()).toBe(2);
        data.versions.mockResolvedValueOnce({
          items: versions.slice(0, 2),
          next_cursor: 'opaque-older',
        });
        store.history.reload();
        await fixture.whenStable();
      }
      expect(store.page()).toBe(1);
      expect(await harness.ids()).toEqual(['version-3']);
      expect(store.selected()).toBe('version-3');
      expect(store.versionDetail()?.view.version_no).toBe(3);
      store.refresh();
      await fixture.whenStable();
      expect(data.versions).toHaveBeenCalledTimes(outcome === 'retry' ? 5 : 4);
      expect(store.selected()).toBe('version-3');
    },
  );
  it('fetches immutable detail with its ETag and compares two stored snapshots', async () => {
    const { harness } = await mount();
    const view = await harness.action('View version 3');
    const compare = await harness.action('Compare with 2');
    expect(await view.text({ exclude: '[popover]' })).toBe('');
    expect(await compare.text({ exclude: '[popover]' })).toBe('');
    expect(await (await harness.actionIcon('View version 3')).getAttribute('aria-hidden')).toBe(
      'true',
    );
    expect(await compare.getProperty('disabled')).toBe(true);
    expect(await compare.getAttribute('aria-describedby')).toBe('case-comparison-reason');
    await view.click();
    expect(await (await harness.action('Compare with 3')).getProperty('disabled')).toBe(true);
    expect(await compare.getProperty('disabled')).toBe(false);
    expect(await (await harness.actionIcon('Compare with 2')).getAttribute('aria-hidden')).toBe(
      'true',
    );
    await compare.click();
    expect(await harness.text()).toContain('sha256:' + '3'.repeat(64));
    expect(await harness.text()).toContain('Title 2');
    expect(await harness.text()).toContain('Title 3');
    expect(data.version).toHaveBeenCalledWith('p1', 'version-3', expect.any(AbortSignal));
  });
  it('restore requires confirmation, exposes unavailable reason, and sends no unsupported body', async () => {
    const { fixture, harness } = await mount();
    await harness.click('[data-inspect-version="version-2"]');
    await harness.click('[data-restore-open]');
    expect(data.restoreVersion).not.toHaveBeenCalled();
    const dialog =
      await TestbedHarnessEnvironment.documentRootLoader(fixture).getHarness(RestoreHarness);
    expect(await dialog.reasonDisabled()).toBe(true);
    await dialog.confirm();
    expect(data.restoreVersion).toHaveBeenCalledExactlyOnceWith(
      'p1',
      'version-2',
      expect.any(String),
    );
    expect(data.restoreVersion.mock.calls[0]).toHaveLength(3);
  });
  it('restore appends a new head without rewriting the selected evidence snapshot', async () => {
    const { fixture, harness } = await mount();
    await harness.click('app-data-table-pager button[data-page="next"]');
    const oldRows = await harness.rowText();
    await harness.click('[data-inspect-version="version-1"]');
    await harness.click('[data-restore-open]');
    await (
      await TestbedHarnessEnvironment.documentRootLoader(fixture).getHarness(RestoreHarness)
    ).confirm();
    expect(await harness.ids()).toEqual(['version-4', 'version-3', 'version-2']);
    expect(await harness.text()).toContain('Title 1');
    expect(await harness.text()).toContain('Version 4');
    await harness.click('app-data-table-pager button[data-page="next"]');
    expect(await harness.rowText()).toEqual(oldRows);
    expect(await harness.text()).toContain('Title 1');
  });
  it('moves focus to the appended version heading after a successful restore', async () => {
    const { fixture, harness } = await mount();
    await harness.click('[data-inspect-version="version-2"]');
    await fixture.whenStable();
    await harness.click('[data-restore-open]');
    await (
      await TestbedHarnessEnvironment.documentRootLoader(fixture).getHarness(RestoreHarness)
    ).confirm();
    await fixture.whenStable();
    const active = document.activeElement;
    expect(active?.getAttribute('data-version-heading')).toBe('4');
  });
  it('Cancel never restores a version and returns focus to the opener', async () => {
    const { fixture, harness } = await mount();
    await harness.click('[data-inspect-version="version-2"]');
    await harness.click('[data-restore-open]');
    await (
      await TestbedHarnessEnvironment.documentRootLoader(fixture).getHarness(RestoreHarness)
    ).cancel();
    await fixture.whenStable();
    expect(data.restoreVersion).not.toHaveBeenCalled();
    expect(document.activeElement?.getAttribute('data-restore-open')).not.toBeNull();
  });
  it('virtual rows wrap actions exclusively and reserve a track for both buttons', async () => {
    const rows = Array.from({ length: 201 }, (_, index) => ({
      ...version(201 - index),
      version_id: 'version-' + (201 - index),
    }));
    data.versions.mockResolvedValue({ items: rows, next_cursor: null });
    const { fixture } = await mount();
    const host = fixture.nativeElement as HTMLElement;
    const actions = host.querySelector('[data-row-id] [data-inspect-version]')?.parentElement;
    expect(actions?.classList.contains('flex-nowrap')).toBe(true);
    expect(actions?.classList.contains('flex-wrap')).toBe(false);
    const tracks = [...host.querySelectorAll('table.header-table col')];
    const style = tracks[tracks.length - 1]?.getAttribute('style') ?? '';
    expect(Number(/([\d.]+)ch/.exec(style)?.[1])).toBeGreaterThanOrEqual(40);
  });
  it('keeps the author and UTC timestamp reachable in the virtual row version detail', async () => {
    const authored = { ...version(4), author: 'member-42', created_at: '2026-11-14T00:00:00Z' };
    const rows = Array.from({ length: 201 }, (_, index) => ({
      ...version(201 - index),
      version_id: 'version-' + (201 - index),
    }));
    data.versions.mockResolvedValue({ items: [authored, ...rows], next_cursor: null });
    data.version.mockResolvedValue({ view: authored, etag: '"author"' });
    const { harness } = await mount();
    expect(await harness.count('cdk-virtual-scroll-viewport')).toBe(1);
    await harness.click('[data-inspect-version="version-4"]');
    const text = await harness.text();
    expect(text).toContain('member-42');
    expect(text).toContain('2026');
  });
  it('focuses the outcome status when an active filter hides the appended row', async () => {
    const { fixture, harness } = await mount();
    await harness.filter('Reason 2');
    await harness.click('[data-inspect-version="version-2"]');
    await fixture.whenStable();
    await harness.click('[data-restore-open]');
    await (
      await TestbedHarnessEnvironment.documentRootLoader(fixture).getHarness(RestoreHarness)
    ).confirm();
    await fixture.whenStable();
    const active = document.activeElement as HTMLElement | null;
    expect(active?.getAttribute('role')).toBe('status');
    expect(active?.textContent).toContain('Version 4 was appended');
  });
  it('a restore that finishes after Cancel does not suppress the next dialog restore focus', async () => {
    let release: (value: { version: number }) => void = () => {};
    // `Promise.withResolvers` is ES2024 and this project's lib is ES2022.
    const pending = new Promise<{ version: number }>((resolve) => {
      release = resolve;
    });
    data.restoreVersion.mockReturnValue(pending);
    const { fixture, harness } = await mount();
    await harness.click('[data-inspect-version="version-2"]');
    await fixture.whenStable();
    await harness.click('[data-restore-open]');
    const loader = TestbedHarnessEnvironment.documentRootLoader(fixture);
    await (await loader.getHarness(RestoreHarness)).confirm();
    await fixture.whenStable();
    await (await loader.getHarness(RestoreHarness)).cancel();
    await fixture.whenStable();
    appended = true;
    release({ version: 4 });
    await fixture.whenStable();
    await harness.click('[data-restore-open]');
    await (await loader.getHarness(RestoreHarness)).cancel();
    await fixture.whenStable();
    expect(document.activeElement?.getAttribute('data-restore-open')).not.toBeNull();
  });
  it('moves focus to the refresh status when the appended row never arrives', async () => {
    let release: (value: { version: number }) => void = () => {};
    // `Promise.withResolvers` is ES2024 and this project's lib is ES2022.
    const pending = new Promise<{ version: number }>((resolve) => {
      release = resolve;
    });
    data.restoreVersion.mockReturnValue(pending);
    const { fixture, harness } = await mount();
    await harness.click('[data-inspect-version="version-2"]');
    await fixture.whenStable();
    await harness.click('[data-restore-open]');
    const dialog =
      await TestbedHarnessEnvironment.documentRootLoader(fixture).getHarness(RestoreHarness);
    await dialog.confirm();
    release({ version: 4 });
    await fixture.whenStable();
    expect(await harness.text()).toContain('Refresh the timeline');
    const active = document.activeElement as HTMLElement | null;
    expect(active?.getAttribute('role')).toBe('status');
    expect(active?.closest('[role="dialog"]')).toBeNull();
    expect(active?.textContent).toContain('the appended version is not yet visible');
  });
  it('write denial keeps history inspectable and explains disabled restore', async () => {
    writable.set(false);
    const { harness } = await mount();
    await harness.click('[data-inspect-version="version-2"]');
    expect(await harness.disabled('[data-restore-open]')).toBe(true);
    expect(await harness.text()).toContain('authoring:write');
    expect(data.restoreVersion).not.toHaveBeenCalled();
  });
  it('a repeated cursor stops paging without duplicating rows', async () => {
    data.versions.mockResolvedValue({ items: versions.slice(0, 2), next_cursor: 'same' });
    const { harness } = await mount();
    await harness.click('app-data-table-pager button[data-page="next"]');
    expect(await harness.disabled('app-data-table-pager button[data-page="next"]')).toBe(true);
    expect(await harness.ids()).toEqual(['version-3', 'version-2']);
    expect(await harness.text()).toContain('repeated');
  });
  it('empty history is a successful empty state rather than a permission error', async () => {
    data.versions.mockResolvedValue({ items: [], next_cursor: null });
    const { harness } = await mount();
    expect(await harness.ids()).toEqual([]);
    expect(await harness.text()).toContain('No versions recorded');
  });
  it('a failed page retains earlier rows and offers an explicit retry', async () => {
    data.versions
      .mockResolvedValueOnce({ items: versions.slice(0, 2), next_cursor: 'older' })
      .mockRejectedValueOnce(
        new ApiRequestError({
          kind: 'unavailable',
          status: 503,
          code: 'unavailable',
          message: 'Unavailable',
          details: [],
          retryable: true,
          retryAfterSeconds: null,
          requestId: null,
        }),
      );
    const { harness } = await mount();
    await harness.click('app-data-table-pager button[data-page="next"]');
    expect(await harness.ids()).toEqual(['version-3', 'version-2']);
    expect(await harness.text()).toContain('Previously loaded versions are retained');
    expect(data.restoreVersion).not.toHaveBeenCalled();
  });
  it('a failed restore leaves confirmation open without fabricating an appended row', async () => {
    data.restoreVersion.mockRejectedValue(
      new ApiRequestError({
        kind: 'unavailable',
        status: 503,
        code: 'unavailable',
        message: 'Unavailable',
        details: [],
        retryable: true,
        retryAfterSeconds: null,
        requestId: null,
      }),
    );
    const { fixture, harness } = await mount();
    await harness.click('[data-inspect-version="version-2"]');
    await harness.click('[data-restore-open]');
    const dialog =
      await TestbedHarnessEnvironment.documentRootLoader(fixture).getHarness(RestoreHarness);
    await dialog.confirm();
    expect(await dialog.reasonDisabled()).toBe(true);
    expect(await harness.ids()).toEqual(['version-3', 'version-2']);
    expect(data.restoreVersion).toHaveBeenCalledTimes(1);
    expect(appended).toBe(false);
  });
  it('a local no-match keeps page one, and changing the filter on a later page resets cursors', async () => {
    const { harness } = await mount();
    await harness.filter('no such reason');
    expect(await harness.text()).toContain('No versions on this page match this filter');
    expect(await harness.text()).toContain('Filter this page by reason');
    expect(await harness.text()).toContain('Fixed server order: newest first');
    expect(data.versions).toHaveBeenCalledTimes(1);
    await harness.click('app-data-table-pager button[data-page="next"]');
    expect(await harness.ids()).toEqual(['version-1']);
    await harness.filter('Reason 2');
    expect(await harness.ids()).toEqual(['version-2']);
    expect(data.versions).toHaveBeenLastCalledWith(
      'p1',
      CASE_ID,
      { limit: 20 },
      expect.any(AbortSignal),
    );
    await harness.click('app-data-table-pager button[data-page="next"]');
    expect(await harness.ids()).toEqual(['version-1']);
  });
  it('403 hides history content without turning it into an empty success', async () => {
    data.versions.mockRejectedValue(
      new ApiRequestError({
        kind: 'forbidden',
        status: 403,
        code: 'forbidden',
        message: 'Forbidden',
        details: [],
        retryable: false,
        retryAfterSeconds: null,
        requestId: null,
      }),
    );
    const { harness } = await mount();
    expect(await harness.ids()).toEqual([]);
    expect(await harness.text()).not.toContain('No versions recorded');
  });
  it('replaces a full server page rather than accumulating enough rows to virtualize', async () => {
    const row = (no: number) => ({ ...version(no), version_id: 'version-' + no });
    const first = Array.from({ length: 200 }, (_, index) => row(201 - index));
    data.versions.mockImplementation(
      (_project: string, _caseId: string, query: AuthoringVersionsQuery) =>
        Promise.resolve(
          query.cursor
            ? { items: [row(1)], next_cursor: null }
            : { items: first, next_cursor: 'older' },
        ),
    );
    const { harness } = await mount();
    expect(await harness.count('[data-row-id]')).toBe(200);
    expect(await harness.count('cdk-virtual-scroll-viewport')).toBe(0);
    await harness.click('app-data-table-pager button[data-page="next"]');
    expect(await harness.ids()).toEqual(['version-1']);
    expect(await harness.count('cdk-virtual-scroll-viewport')).toBe(0);
    expect(await harness.disabled('app-data-table-pager button[data-page="next"]')).toBe(true);
  });
  it('announces restore progress inside the busy dialog and keeps focus off the body', async () => {
    // The deferred resolver by hand: `Promise.withResolvers` is ES2024 and this project's lib is ES2022.
    let release: (value: { version: number }) => void = () => {};
    const pending = new Promise<{ version: number }>((resolve) => {
      release = resolve;
    });
    data.restoreVersion.mockReturnValue(pending);
    const { fixture, harness } = await mount();
    await harness.click('[data-inspect-version="version-2"]');
    await fixture.whenStable();
    await harness.click('[data-restore-open]');
    const dialog =
      await TestbedHarnessEnvironment.documentRootLoader(fixture).getHarness(RestoreHarness);
    await dialog.confirm();
    await fixture.whenStable();
    expect(await dialog.busy()).toBe('true');
    expect(await dialog.text()).toContain('Restoring version…');
    expect(await harness.text()).not.toContain('Restoring version…');
    const active = document.activeElement;
    expect(active?.tagName).not.toBe('BODY');
    expect(active?.closest('[role="dialog"]')).not.toBeNull();
    release({ version: 4 });
    appended = true;
    await fixture.whenStable();
    expect(await harness.text()).toContain('Version 4 was appended');
  });
});
