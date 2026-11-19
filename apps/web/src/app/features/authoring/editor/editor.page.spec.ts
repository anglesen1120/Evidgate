import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap } from '@angular/router';
import { of } from 'rxjs';
import type { ManualCaseDetailDto } from '@evidgate/sdk';
import type { Mock } from 'vitest';
import { AuthoringEditorDataService } from '../../../core/data/authoring-editor-data.service';
import { ApiRequestError, type ApiErrorKind } from '../../../core/http/api-error';
import { SessionStore } from '../../../core/session/session.store';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow';
import { EditorPage } from './editor.page';
import { EDITOR_WRITE } from './editor.model';

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
    writable.set(true);
    narrow.set(false);
    data = {
      readCase: vi.fn().mockResolvedValue({ view: CASE, etag: '"2"' }),
      updateCase: vi.fn().mockImplementation(async (_project, _id, body) => ({
        view: { ...CASE, ...body, version: 3 },
        etag: '"3"',
      })),
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
            currentOrg: signal({ id: 'o1' }),
            currentProject: signal({ id: 'p1' }),
            has: (code: string) => (code === EDITOR_WRITE ? writable() : true),
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
}
