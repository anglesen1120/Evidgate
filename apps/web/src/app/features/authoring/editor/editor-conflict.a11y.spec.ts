import { ComponentHarness, parallel } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap } from '@angular/router';
import { of } from 'rxjs';
import type { ManualCaseDetailDto, UpdateManualCaseRequest } from '@evidgate/sdk';
import type { Mock } from 'vitest';
import { AuthoringEditorDataService } from '../../../core/data/authoring-editor-data.service';
import { ApiRequestError } from '../../../core/http/api-error';
import { SessionStore } from '../../../core/session/session.store';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow';
import { EditorPage } from './editor.page';
import { EDITOR_WRITE } from './editor.model';

const HOSTILE = '<img src=x onerror="globalThis.pwned=1"> <script>globalThis.pwned=1</script>';

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
  async focus(selector: string) {
    await (await this.locatorFor(selector)()).focus();
  }
  async focused(selector: string) {
    return (await this.locatorFor(selector)()).isFocused();
  }
  async saveState() {
    const save = await this.locatorFor('[data-editor-save]')();
    // These properties describe one settled control, so share a change-detection pass.
    const [nativeDisabled, ariaDisabled, dataDisabled] = await parallel(() => [
      save.getProperty<boolean>('disabled'),
      save.getAttribute('aria-disabled'),
      save.getAttribute('data-disabled'),
    ]);
    return { nativeDisabled, ariaDisabled, dataDisabled };
  }
  async click(selector: string) {
    await (await this.locatorFor(selector)()).click();
  }
}

function conflictDialog(): HTMLElement {
  const dialog = document.querySelector<HTMLElement>('[role="dialog"]');
  if (!dialog) throw new Error('Expected the editor conflict dialog');
  return dialog;
}

function refused(reason: string) {
  return new ApiRequestError({
    kind: 'precondition_failed',
    status: 412,
    code: 'precondition_failed',
    message: 'the case changed since it was read (current version 7)',
    requestId: 'request-412',
    retryable: false,
    details: [
      { path: '/version', reason: '7' },
      { path: '/title', reason },
    ],
    retryAfterSeconds: null,
  });
}

provideLayoutTestStubs();
describe('EditorPage conflict dialog accessibility', () => {
  let data: {
    readCase: Mock;
    updateCase: Mock;
    versions: Mock;
    version: Mock;
    restoreVersion: Mock;
  };
  beforeEach(() => {
    data = {
      readCase: vi.fn().mockResolvedValue({ view: CASE, etag: '"2"' }),
      updateCase: vi
        .fn()
        .mockImplementation((_p: string, _id: string, body: UpdateManualCaseRequest) =>
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
            currentOrg: signal({ id: 'o1' }),
            currentProject: signal({ id: 'p1' }),
            has: (code: string) => code === EDITOR_WRITE || code.endsWith(':read'),
          },
        },
        {
          provide: NarrowMutationPolicy,
          useValue: {
            narrow: signal(false),
            canMutate: () => true,
            allowsMutation: () => true,
          },
        },
      ],
    });
  });
  afterEach(() => {
    TestBed.resetTestingModule();
    vi.restoreAllMocks();
  });
  async function openConflict(server: ManualCaseDetailDto = CASE) {
    data.updateCase.mockRejectedValue(refused(HOSTILE));
    const fixture = TestBed.createComponent(EditorPage);
    await fixture.whenStable();
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, EditorHarness);
    // The dialog compares the draft with the authoritative head it reloads after the 412.
    data.readCase.mockResolvedValue({ view: server, etag: '"7"' });
    await harness.fill('case-title', 'Retained title');
    await harness.focus('[data-editor-save]');
    await harness.click('[data-editor-save]');
    await fixture.whenStable();
    return { fixture, harness };
  }
  it('renders hostile server values as inert text inside the conflict dialog', async () => {
    const hostile = { ...CASE, title: HOSTILE, description: HOSTILE, version: 7 };
    const { harness } = await openConflict(hostile);
    const dialog = conflictDialog();
    expect(dialog.textContent).toContain('<img src=x onerror="globalThis.pwned=1">');
    expect(dialog.textContent).toContain('<script>globalThis.pwned=1</script>');
    expect(dialog.querySelector('img')).toBeNull();
    expect(dialog.querySelector('script')).toBeNull();
    expect(document.querySelector('[onerror]')).toBeNull();
    expect(Object.hasOwn(globalThis, 'pwned')).toBe(false);
    expect(await harness.value('case-title')).toBe('Retained title');
  });
  it('keeps the Save opener focusable with one trap and retains the draft on Escape', async () => {
    const { fixture, harness } = await openConflict();
    const dialog = conflictDialog();
    expect(dialog.getAttribute('aria-modal')).toBe('true');
    expect(await harness.saveState()).toEqual({
      nativeDisabled: false,
      ariaDisabled: 'true',
      dataDisabled: '',
    });
    const anchors = document.querySelectorAll<HTMLElement>('.cdk-focus-trap-anchor');
    expect(anchors).toHaveLength(2);
    // jsdom cannot model disabled-opener focus loss or Tab wrapping. The routed browser spec
    // proves those behaviors; this unit also requires a natively focusable, aria-disabled opener.
    for (const anchor of anchors) expect(anchor.parentElement).toBe(dialog.parentElement);
    const first = dialog.querySelector<HTMLElement>('button.close');
    if (!first) throw new Error('Expected the conflict Close control');
    first.focus();
    first.dispatchEvent(
      new KeyboardEvent('keydown', {
        key: 'Escape',
        keyCode: 27,
        bubbles: true,
        cancelable: true,
      }),
    );
    await fixture.whenStable();
    expect(document.querySelector('[role="dialog"]')).toBeNull();
    expect(await harness.focused('[data-editor-save]')).toBe(true);
    expect(await harness.value('case-title')).toBe('Retained title');
    expect(data.updateCase).toHaveBeenCalledTimes(1);
  });
});
