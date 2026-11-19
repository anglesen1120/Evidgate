import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap } from '@angular/router';
import { of } from 'rxjs';
import type { ManualCaseDetailDto, UpdateManualCaseRequest } from '@evidgate/sdk';
import type { Mock } from 'vitest';
import { AuthoringEditorDataService } from '../../../core/data/authoring-editor-data.service';
import { ApiRequestError, type ApiErrorKind } from '../../../core/http/api-error';
import { SessionStore } from '../../../core/session/session.store';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow';
import { EditorPage } from './editor.page';
import { EDITOR_WRITE } from './editor.model';
import { EditorStore } from './editor.store';

provideLayoutTestStubs();

class EditorHarness extends ComponentHarness {
  static hostSelector = 'app-authoring-editor';
  async fill(id: string, value: string) {
    const field = await this.locatorFor('#' + id)();
    await field.setInputValue(value);
    await field.dispatchEvent('input');
    await field.dispatchEvent('blur');
  }
  async value(id: string) {
    return (await this.locatorFor('#' + id)()).getProperty<string>('value');
  }
  async click(selector: string) {
    await (await this.locatorFor(selector)()).click();
  }
  async disabled(selector: string) {
    return (await this.locatorFor(selector)()).getProperty<boolean>('disabled');
  }
  async text() {
    return (await this.host()).text();
  }
  async priority(label: string) {
    await this.click('#case-priority');
    for (const option of await this.documentRootLocatorFactory().locatorForAll(
      '[role="option"]',
    )()) {
      if ((await option.text()).trim() === label) {
        await option.click();
        return;
      }
    }
    throw new Error('Priority option not found: ' + label);
  }
  async stepValues() {
    const fields = await this.locatorForAll('[data-step-field]')();
    return Promise.all(fields.map((field) => field.getProperty<string>('value')));
  }
  async tab(label: string) {
    for (const button of await this.locatorForAll('[hlmTabsList] button')()) {
      if ((await button.text()).trim() === label) {
        await button.click();
        return;
      }
    }
    throw new Error('Tab not found: ' + label);
  }
}

const CASE: ManualCaseDetailDto = {
  case_id: 'a4430000-0000-4000-8000-000000000001',
  code: 'AUTH-001',
  checklist_item_id: 'a4430000-0000-4000-8000-000000000010',
  node_id: 'a4430000-0000-4000-8000-000000000011',
  title: 'Review checkout total',
  description: 'Existing description',
  precondition: 'Checkout is open',
  priority: 'high',
  steps: [{ no: 1, action: 'Confirm order', expected: 'Total is shown', data: 'VND' }],
  state: 'draft',
  version: 2,
  created_at: '2026-11-18T00:00:00Z',
  updated_at: '2026-11-19T00:00:00Z',
};
function refused(kind: ApiErrorKind, status: number, path?: string) {
  return new ApiRequestError({
    kind,
    status,
    code: kind,
    message: 'Refused',
    requestId: 'request-1',
    retryable: kind === 'unavailable',
    retryAfterSeconds: null,
    details: path ? [{ path, reason: 'value_invalid' }] : [],
  });
}

describe('EditorPage', () => {
  const org = signal<{ id: string } | null>({ id: 'o1' });
  const project = signal<{ id: string } | null>({ id: 'p1' });
  const readable = signal(true);
  const writable = signal(true);
  const narrow = signal(false);
  let data: {
    readCase: Mock;
    updateCase: Mock;
    versions: Mock;
    version: Mock;
    restoreVersion: Mock;
  };
  beforeEach(() => {
    org.set({ id: 'o1' });
    project.set({ id: 'p1' });
    readable.set(true);
    writable.set(true);
    narrow.set(false);
    data = {
      readCase: vi.fn().mockResolvedValue({ view: CASE, etag: '"2"' }),
      updateCase: vi
        .fn()
        .mockImplementation((_project: string, _id: string, body: UpdateManualCaseRequest) =>
          Promise.resolve({ view: { ...CASE, ...body, version: 3 }, etag: '"3"' }),
        ),
      versions: vi.fn().mockResolvedValue({ items: [], next_cursor: null }),
      version: vi.fn(),
      restoreVersion: vi.fn(),
    };
    TestBed.configureTestingModule({
      imports: [EditorPage],
      providers: [
        provideZonelessChangeDetection(),
        { provide: AuthoringEditorDataService, useValue: data },
        {
          provide: ActivatedRoute,
          useValue: {
            paramMap: of(convertToParamMap({ caseId: CASE.case_id })),
            snapshot: { data: { pageHeadingLevel: 1 } },
          },
        },
        {
          provide: SessionStore,
          useValue: {
            currentOrg: org,
            currentProject: project,
            has: (code: string) => readable() && (code === EDITOR_WRITE ? writable() : true),
          },
        },
        {
          provide: NarrowMutationPolicy,
          useValue: {
            narrow,
            canMutate: () => !narrow(),
            allowsMutation: () => !narrow(),
          },
        },
      ],
    });
  });
  afterEach(() => TestBed.resetTestingModule());
  async function mount() {
    const fixture = TestBed.createComponent(EditorPage);
    await fixture.whenStable();
    return {
      fixture,
      harness: await TestbedHarnessEnvironment.harnessForFixture(fixture, EditorHarness),
    };
  }
  async function editAll(harness: EditorHarness) {
    await harness.fill('case-title', 'Retained title');
    await harness.fill('case-description', 'Retained description');
    await harness.fill('case-precondition', 'Retained preconditions');
    await harness.fill('case-step-0-action', 'Retained action');
    await harness.fill('case-step-0-expected', 'Retained expected');
    await harness.fill('case-step-0-data', 'Retained data');
    await harness.priority('Critical');
  }
  async function expectDraft(harness: EditorHarness) {
    expect(await harness.value('case-title')).toBe('Retained title');
    expect(await harness.value('case-description')).toBe('Retained description');
    expect(await harness.value('case-precondition')).toBe('Retained preconditions');
    expect(await harness.stepValues()).toEqual([
      'Retained action',
      'Retained expected',
      'Retained data',
    ]);
    expect(await harness.text()).toContain('Critical');
  }
  it('retains every field after 412 and closing the conflict issues no second write', async () => {
    data.updateCase.mockRejectedValue(refused('precondition_failed', 412));
    const { fixture, harness } = await mount();
    await editAll(harness);
    await harness.click('[data-editor-save]');
    const dialog =
      await TestbedHarnessEnvironment.documentRootLoader(fixture).getHarness(ConflictHarness);
    expect(await fixture.componentInstance.confirmLeave()).toBe(false);
    expect(
      await TestbedHarnessEnvironment.documentRootLoader(fixture).getAllHarnesses(ConflictHarness),
    ).toHaveLength(1);
    await dialog.keepEditing();
    await expectDraft(harness);
    expect(data.updateCase).toHaveBeenCalledTimes(1);
    expect(data.readCase).toHaveBeenCalledTimes(2);
  });
  for (const [kind, status] of [
    ['validation', 422],
    ['unavailable', 503],
    ['network', 0],
  ] as const) {
    it('keeps the complete draft after ' + kind, async () => {
      data.updateCase.mockRejectedValue(
        refused(kind, status, kind === 'validation' ? '/title' : undefined),
      );
      const { harness } = await mount();
      await editAll(harness);
      await harness.click('[data-editor-save]');
      await expectDraft(harness);
      expect(data.updateCase).toHaveBeenCalledTimes(1);
    });
  }
  it('disables Save without write permission and explains the missing grant', async () => {
    writable.set(false);
    const { harness } = await mount();
    expect(await harness.disabled('[data-editor-save]')).toBe(true);
    expect(await harness.text()).toContain('authoring:write');
    expect(data.updateCase).not.toHaveBeenCalled();
  });
  it('sends the read ETag and no unsupported reason field', async () => {
    const { harness } = await mount();
    await harness.fill('case-title', 'Changed title');
    await harness.click('[data-editor-save]');
    expect(data.updateCase).toHaveBeenCalledWith(
      'p1',
      CASE.case_id,
      expect.objectContaining({ title: 'Changed title' }),
      '"2"',
      expect.any(String),
    );
    expect(data.updateCase.mock.calls[0]?.[2]).not.toHaveProperty('reason');
    expect(await harness.disabled('#case-reason')).toBe(true);
    expect(await harness.text()).toContain('cannot record');
  });
  it('adds, moves and removes paired steps without losing their values', async () => {
    const { harness } = await mount();
    await harness.click('[data-step-add]');
    await harness.fill('case-step-1-action', 'Second action');
    await harness.fill('case-step-1-expected', 'Second expected');
    await harness.click('[data-step-up="1"]');
    expect((await harness.stepValues()).slice(0, 2)).toEqual(['Second action', 'Second expected']);
    await harness.click('[data-step-remove="0"]');
    expect((await harness.stepValues()).slice(0, 2)).toEqual(['Confirm order', 'Total is shown']);
  });
  it('blocks invalid title and step fields with associated feedback, without a request', async () => {
    const { harness } = await mount();
    await harness.fill('case-title', '');
    await harness.fill('case-step-0-action', '');
    await harness.click('[data-editor-save]');
    expect(await harness.text()).toContain('There is a problem');
    expect(data.updateCase).not.toHaveBeenCalled();
  });
  it('projects the retained draft read-only on narrow screens', async () => {
    const { fixture, harness } = await mount();
    await harness.fill('case-title', 'Retained on resize');
    narrow.set(true);
    await fixture.whenStable();
    expect(await harness.text()).toContain('Retained on resize');
    narrow.set(false);
    await fixture.whenStable();
    expect(await harness.value('case-title')).toBe('Retained on resize');
  });
  it('requires explicit confirmation to discard an unsaved draft', async () => {
    const { fixture, harness } = await mount();
    await harness.fill('case-title', 'Unsaved title');
    const decision = fixture.componentInstance.confirmLeave();
    await fixture.whenStable();
    const dialog =
      await TestbedHarnessEnvironment.documentRootLoader(fixture).getHarness(LeaveHarness);
    await dialog.cancel();
    expect(await decision).toBe(false);
    expect(await harness.value('case-title')).toBe('Unsaved title');
  });
  it('retains the dirty draft while guards clear the scope and grants before leave consent', async () => {
    const { fixture, harness } = await mount();
    await harness.fill('case-title', 'Unsaved source title');
    org.set({ id: 'o2' });
    project.set(null);
    readable.set(false);
    await fixture.whenStable();
    const store = fixture.debugElement.injector.get(EditorStore);
    expect(store.scope()).toMatchObject({ org: 'o1', project: 'p1' });
    expect(store.scopeAvailable()).toBe(false);
    expect(store.dirty()).toBe(true);
    expect(await harness.value('case-title')).toBe('Unsaved source title');
    expect(await store.save()).toBe(false);
    org.set({ id: 'o2' });
    project.set({ id: 'p2' });
    readable.set(true);
    await fixture.whenStable();
    const decision = fixture.componentInstance.confirmLeave();
    await fixture.whenStable();
    const dialog =
      await TestbedHarnessEnvironment.documentRootLoader(fixture).getHarness(LeaveHarness);
    await dialog.cancel();
    expect(await decision).toBe(false);
    expect(store.dirty()).toBe(true);
    expect(await store.save()).toBe(false);
    org.set({ id: 'o1' });
    project.set({ id: 'p1' });
    await fixture.whenStable();
    expect(await harness.value('case-title')).toBe('Unsaved source title');
    expect(data.readCase).toHaveBeenCalledTimes(1);
    expect(fixture.debugElement.injector.get(EditorStore).dirty()).toBe(true);
  });
  it('discards the retained draft when its own scope loses read permission', async () => {
    const { fixture, harness } = await mount();
    await harness.fill('case-title', 'Revoked draft');
    readable.set(false);
    await fixture.whenStable();
    const store = fixture.debugElement.injector.get(EditorStore);
    expect(store.dirty()).toBe(false);
    expect(store.draft()).toBeUndefined();
    expect(await harness.text()).not.toContain('Revoked draft');
    expect(await fixture.componentInstance.confirmLeave()).toBe(true);
  });
  it('keeps fields read-only until the post-restore head refresh settles', async () => {
    const { fixture, harness } = await mount();
    const store = fixture.debugElement.injector.get(EditorStore);
    // The deferred resolver by hand: `Promise.withResolvers` is ES2024 and this project's lib is ES2022.
    let release: (value: { view: ManualCaseDetailDto; etag: string }) => void = () => {};
    const refreshed = new Promise<{ view: ManualCaseDetailDto; etag: string }>((resolve) => {
      release = resolve;
    });
    data.readCase.mockReturnValueOnce(refreshed);
    store.refreshAfterRestore();
    fixture.detectChanges();
    expect(store.locked()).toBe(true);
    // CDK locators stabilize the fixture, so the pending refresh is read from the DOM directly.
    const save = (fixture.nativeElement as HTMLElement).querySelector('[data-editor-save]');
    expect((save as HTMLButtonElement | null)?.disabled).toBe(true);
    store.editText('title', 'Must not be accepted during refresh');
    expect(store.draft()?.title).toBe(CASE.title);
    release({ view: { ...CASE, title: 'Restored title', version: 3 }, etag: '"3"' });
    await fixture.whenStable();
    expect(store.locked()).toBe(false);
    expect(await harness.value('case-title')).toBe('Restored title');
    expect(await harness.disabled('[data-editor-save]')).toBe(false);
    await harness.fill('case-title', 'New edit after refresh');
    expect(store.draft()?.title).toBe('New edit after refresh');
  });
  it('retains restore ownership across timeline destruction and refuses Save until it completes', async () => {
    let release: (value: { version: number }) => void = () => {};
    const restoring = new Promise<{ version: number }>((resolve) => {
      release = resolve;
    });
    data.versions.mockResolvedValue({
      items: [
        {
          version_id: 'v2',
          version_no: 2,
          source: 'manual',
          change_kind: 'update',
          created_at: '2026-11-03T00:00:00Z',
          author: null,
          reason: 'Reason 2',
        },
      ],
      next_cursor: null,
    });
    data.version.mockResolvedValue({
      view: {
        version_id: 'v2',
        version_no: 2,
        entity_kind: 'manual_test_case',
        entity_id: CASE.case_id,
        content: { title: 'Stored title' },
      },
      etag: '"snapshot"',
    });
    data.restoreVersion.mockReturnValue(restoring);
    const { fixture, harness } = await mount();
    await harness.fill('case-title', 'Unsaved draft');
    await harness.tab('Timeline');
    await harness.click('[data-inspect-version="v2"]');
    await fixture.whenStable();
    await harness.click('[data-restore-open]');
    const root = TestbedHarnessEnvironment.documentRootLoader(fixture);
    const restore = await root.getHarness(RestorePageHarness);
    await restore.confirm();
    await restore.cancel();
    await harness.tab('Editor');
    const store = fixture.debugElement.injector.get(EditorStore);
    expect(store.restoring()).toBe(true);
    expect(await harness.disabled('[data-editor-save]')).toBe(true);
    expect(await store.save()).toBe(false);
    expect(data.updateCase).not.toHaveBeenCalled();
    release({ version: 3 });
    await fixture.whenStable();
    expect(store.restoring()).toBe(false);
    expect(await harness.disabled('[data-editor-save]')).toBe(false);
    await harness.click('[data-editor-save]');
    expect(data.updateCase).toHaveBeenCalledTimes(1);
  });
  it('compares steps without inventing a difference for an omitted optional data value', async () => {
    data.updateCase.mockRejectedValue(refused('precondition_failed', 412));
    data.readCase
      .mockResolvedValueOnce({
        view: {
          ...CASE,
          steps: [{ no: 1, action: 'Confirm order', expected: 'Total is shown', data: null }],
        },
        etag: '"2"',
      })
      .mockResolvedValue({
        view: {
          ...CASE,
          steps: [{ no: 1, action: 'Confirm order', expected: 'Total is shown' }],
        },
        etag: '"2"',
      });
    const { fixture, harness } = await mount();
    await harness.fill('case-title', 'Changed title');
    await harness.click('[data-editor-save]');
    await fixture.whenStable();
    const dialog =
      await TestbedHarnessEnvironment.documentRootLoader(fixture).getHarness(ConflictHarness);
    const steps = await dialog.fieldWith('Confirm order');
    expect(steps).toContain('Same');
    expect(steps).not.toContain('Changed');
  });
  it('explains a saved read-only projection without claiming unsaved edits', async () => {
    narrow.set(true);
    const { harness } = await mount();
    const text = await harness.text();
    expect(text).toContain('These saved values are shown read-only');
    expect(text).not.toContain('Unsaved desktop edits are retained');
  });
});
class LeaveHarness extends ComponentHarness {
  static hostSelector = '[role="dialog"], [role="alertdialog"]';
  async cancel() {
    await (await this.locatorFor('[data-leave-cancel]')()).click();
  }
}
class ConflictHarness extends ComponentHarness {
  static hostSelector = '[role="dialog"]';
  async keepEditing() {
    await (await this.locatorFor('button[hlmDialogClose]:not(.close)')()).click();
  }
  async fieldWith(value: string) {
    for (const section of await this.locatorForAll('.field')()) {
      const text = await section.text();
      if (text.includes(value)) return text;
    }
    throw new Error('Conflict field not found for: ' + value);
  }
}
class RestorePageHarness extends ComponentHarness {
  static hostSelector = '[role="dialog"]';
  async confirm() {
    await (await this.locatorFor('[data-restore-confirm]')()).click();
  }
  async cancel() {
    await (await this.locatorFor('[data-restore-cancel]')()).click();
  }
}
