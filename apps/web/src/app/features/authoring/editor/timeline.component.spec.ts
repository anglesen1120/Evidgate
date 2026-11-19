import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { DeferBlockBehavior, TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap } from '@angular/router';
import type { AuthoringVersionDetailDto, ManualCaseDetailDto } from '@evidgate/sdk';
import type { Mock } from 'vitest';
import { of } from 'rxjs';
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

provideLayoutTestStubs();
const CASE_ID = 'a4430000-0000-4000-8000-000000000001';
function version(no: number): AuthoringVersionDetailDto {
  return {
    version_id: 'version-' + no,
    entity_kind: 'manual_test_case',
    entity_id: CASE_ID,
    version_no: no,
    batch_id: 'batch-' + no,
    change_kind: no === 1 ? 'create' : 'update',
    source: 'manual',
    content_hash: 'sha256:' + String(no).repeat(64),
    author: null,
    reason: 'Reason ' + no,
    ai_run_id: null,
    created_at: '2026-11-' + (10 + no) + 'T00:00:00Z',
    content: {
      title: 'Title ' + no,
      steps: [{ no: 1, action: 'Action ' + no, expected: 'Expected ' + no }],
    },
  };
}
class TimelineHarness extends ComponentHarness {
  static hostSelector = 'app-editor-timeline';
  async click(selector: string) {
    await (await this.locatorFor(selector)()).click();
  }
  async ids() {
    return Promise.all(
      (await this.locatorForAll('[data-version-row]')()).map((row) =>
        row.getAttribute('data-version-row'),
      ),
    );
  }
  async text() {
    return (await this.host()).text();
  }
  async disabled(selector: string) {
    return (await this.locatorFor(selector)()).getProperty<boolean>('disabled');
  }
  async rowText() {
    return Promise.all((await this.locatorForAll('[data-version-row]')()).map((row) => row.text()));
  }
  async filter(value: string) {
    const input = await this.locatorFor('#case-history-filter')();
    await input.setInputValue(value);
    await input.dispatchEvent('input');
  }
  async count(selector: string) {
    return (await this.locatorForAll(selector)()).length;
  }
}
class RestoreHarness extends ComponentHarness {
  static hostSelector = '[role="dialog"], [role="alertdialog"]';
  async confirm() {
    await (await this.locatorFor('[data-restore-confirm]')()).click();
  }
  async cancel() {
    await (await this.locatorFor('[data-restore-cancel]')()).click();
  }
  async reasonDisabled() {
    return (await this.locatorFor('#case-restore-reason')()).getProperty<boolean>('disabled');
  }
  async busy() {
    return (await this.locatorFor('[aria-busy]')()).getAttribute('aria-busy');
  }
  async text() {
    return (await this.host()).text();
  }
}

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
  it('loads newest first and appends keyset pages using only the returned cursor', async () => {
    const { harness } = await mount();
    expect(await harness.ids()).toEqual(['version-3', 'version-2']);
    await harness.click('[data-versions-more]');
    expect(await harness.ids()).toEqual(['version-3', 'version-2', 'version-1']);
    expect(data.versions).toHaveBeenLastCalledWith(
      'p1',
      CASE_ID,
      { limit: 50, cursor: 'opaque-older' },
      expect.any(AbortSignal),
    );
  });
  it('fetches immutable detail with its ETag and compares two stored snapshots', async () => {
    const { harness } = await mount();
    await harness.click('[data-inspect-version="version-3"]');
    await harness.click('[data-compare-version="version-2"]');
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
  it('restore appends a new id and leaves all previously loaded rows identical', async () => {
    const { fixture, harness } = await mount();
    await harness.click('[data-versions-more]');
    const oldRows = await harness.rowText();
    await harness.click('[data-inspect-version="version-1"]');
    await harness.click('[data-restore-open]');
    await (
      await TestbedHarnessEnvironment.documentRootLoader(fixture).getHarness(RestoreHarness)
    ).confirm();
    expect(await harness.ids()).toEqual(['version-4', 'version-3', 'version-2', 'version-1']);
    expect((await harness.rowText()).slice(1)).toEqual(oldRows);
    expect(await harness.text()).toContain('Version 4');
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
    await harness.click('[data-versions-more]');
    expect(await harness.disabled('[data-versions-more]')).toBe(true);
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
    await harness.click('[data-versions-more]');
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
  it('no-match differs from empty history and does not alter server pagination', async () => {
    const { harness } = await mount();
    await harness.filter('no such reason');
    expect(await harness.text()).toContain('No loaded versions match');
    expect(data.versions).toHaveBeenCalledTimes(1);
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
  it('virtualizes the shared table at 201 loaded versions and keeps rich rows at 200', async () => {
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
    const { fixture, harness } = await mount();
    expect(await harness.count('[data-version-row]')).toBe(200);
    expect(await harness.count('cdk-virtual-scroll-viewport')).toBe(0);
    await harness.click('[data-versions-more]');
    await fixture.whenStable();
    expect(await harness.count('cdk-virtual-scroll-viewport')).toBe(1);
    const rendered = await harness.count('[data-row-id]');
    expect(rendered).toBeGreaterThan(0);
    expect(rendered).toBeLessThan(201);
    expect(await harness.text()).toContain('201 versions loaded');
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
