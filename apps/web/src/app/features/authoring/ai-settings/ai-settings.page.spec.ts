import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { signal, provideZonelessChangeDetection, type WritableSignal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute } from '@angular/router';
import type { Mock } from 'vitest';
import type { AiSettingsView, AiSettingsVersion } from '@evidgate/sdk';
import { AiSettingsDataService } from '../../../core/data/ai-settings-data.service';
import { SessionStore } from '../../../core/session/session.store';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow';
import { ApiRequestError } from '../../../core/http/api-error';
import { AiSettingsPage } from './ai-settings.page';
import { FIELDS, AI_WRITE } from './ai-settings.model';
import headDocument from '../../../../../../../fixtures/web-api/ai-settings-head/input.json';
import versionDocument from '../../../../../../../fixtures/web-api/ai-settings-version-1/input.json';

provideLayoutTestStubs();

class SettingsHarness extends ComponentHarness {
  static hostSelector = 'app-ai-settings-page';
  readonly saveButton = this.locatorFor('[data-ai-save]');
  async fill(key: string, value: string) {
    const field = await this.locatorFor('#ai-' + key)();
    await field.setInputValue(value);
    await field.dispatchEvent('input');
    await field.dispatchEvent('blur');
  }
  async value(key: string) {
    return (await this.locatorFor('#ai-' + key)()).getProperty<string>('value');
  }
  async click(selector: string) {
    await (await this.locatorFor(selector)()).click();
  }
  async compose(key: string, active: boolean) {
    await (
      await this.locatorFor('#ai-' + key)()
    ).dispatchEvent(active ? 'compositionstart' : 'compositionend');
  }
  async text() {
    return (await this.host()).text();
  }
}
describe('AiSettingsPage', () => {
  const head = headDocument.body as AiSettingsView;
  const version = versionDocument.body as AiSettingsVersion;
  let writable: WritableSignal<boolean>;
  let data: { read: Mock; save: Mock; versions: Mock; version: Mock; templates: Mock };
  beforeEach(() => {
    writable = signal(true);
    data = {
      read: vi.fn().mockResolvedValue({ view: head, etag: '"2"' }),
      save: vi.fn().mockResolvedValue({ view: head, etag: '"2"', appended: false }),
      versions: vi.fn().mockResolvedValue({ items: [version], next_cursor: null }),
      version: vi.fn().mockResolvedValue(version),
      templates: vi.fn().mockResolvedValue({ items: [], next_cursor: null }),
    };
    TestBed.configureTestingModule({
      imports: [AiSettingsPage],
      providers: [
        provideZonelessChangeDetection(),
        { provide: ActivatedRoute, useValue: { snapshot: { data: { pageHeadingLevel: 1 } } } },
        { provide: AiSettingsDataService, useValue: data },
        {
          provide: SessionStore,
          useValue: {
            currentOrg: signal({ id: 'o1' }),
            currentProject: signal({ id: 'p1' }),
            has: (code: string) => (code === AI_WRITE ? writable() : true),
          },
        },
        {
          provide: NarrowMutationPolicy,
          useValue: { canMutate: signal(true), narrow: signal(false), allowsMutation: () => true },
        },
      ],
    });
  });
  afterEach(() => TestBed.resetTestingModule());
  async function mount() {
    const fixture = TestBed.createComponent(AiSettingsPage);
    await fixture.whenStable();
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, SettingsHarness);
    return { fixture, harness };
  }
  for (const field of FIELDS) {
    for (const delta of [-1, 0, 1]) {
      it(field.key + ' accepts its cap ' + delta + ' boundary without truncation', async () => {
        const { harness } = await mount();
        const value =
          field.key === 'environment_url'
            ? 'https://example.test/' + 'x'.repeat(field.limit + delta - 21)
            : '😀'.repeat(field.limit + delta);
        await harness.fill(field.key, value);
        expect(await harness.value(field.key)).toBe(value);
        expect(await (await harness.saveButton()).getProperty<boolean>('disabled')).toBe(delta > 0);
      });
    }
  }
  it('retains the entire draft after a failed save', async () => {
    data.save.mockRejectedValue(
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
    await harness.fill('writing_style', 'Retained writing draft');
    await harness.click('[data-ai-save]');
    expect(await harness.value('writing_style')).toBe('Retained writing draft');
    expect(await harness.text()).toContain('draft');
  });
  it('explains a disabled Save without project write', async () => {
    writable.set(false);
    const { harness } = await mount();
    expect(await (await harness.saveButton()).getProperty<boolean>('disabled')).toBe(true);
    expect(await harness.text()).toContain('project:write');
  });
  it('renders version list and immutable detail from the detail fetch', async () => {
    const { harness } = await mount();
    await harness.click('[data-ai-history-tab]');
    await harness.click('[data-ai-inspect]');
    expect(await harness.text()).toContain(version.settings.writing_style);
    expect(data.version).toHaveBeenCalledWith('p1', 1, expect.any(AbortSignal));
  });
  it('maps 422 pointers to associated form errors and retains input', async () => {
    data.save.mockRejectedValue(
      new ApiRequestError({
        kind: 'validation',
        status: 422,
        code: 'invalid',
        message: 'Rejected',
        details: [
          { path: '/settings/writing_style', reason: 'max_length', limit: 250, actual: 251 },
        ],
        retryable: false,
        retryAfterSeconds: null,
        requestId: null,
      }),
    );
    const { harness } = await mount();
    await harness.fill('writing_style', 'Keep this');
    await harness.click('[data-ai-save]');
    expect(await harness.value('writing_style')).toBe('Keep this');
    expect(await harness.text()).toContain('server');
  });
  it('loads a separate authorized server snapshot for the 412 conflict', async () => {
    data.save.mockRejectedValue(
      new ApiRequestError({
        kind: 'precondition_failed',
        status: 412,
        code: 'conflict',
        message: 'Changed',
        details: [{ path: '/version_no', reason: '3' }],
        retryable: false,
        retryAfterSeconds: null,
        requestId: null,
      }),
    );
    const { fixture, harness } = await mount();
    await harness.fill('writing_style', 'My draft');
    await harness.click('[data-ai-save]');
    await fixture.whenStable();
    expect(data.read).toHaveBeenCalledTimes(2);
    expect(await harness.value('writing_style')).toBe('My draft');
    const dialog = await TestbedHarnessEnvironment.documentRootLoader(fixture).getAllHarnesses(
      class extends ComponentHarness {
        static hostSelector = 'app-conflict-dialog';
      },
    );
    expect(dialog.length).toBe(1);
  });
  it('holds Save during IME composition and keeps the untruncated committed draft', async () => {
    const { harness } = await mount();
    await harness.compose('writing_style', true);
    await harness.fill('writing_style', 'a'.repeat(251));
    expect(await (await harness.saveButton()).getProperty<boolean>('disabled')).toBe(true);
    await harness.compose('writing_style', false);
    expect(await harness.value('writing_style')).toBe('a'.repeat(251));
    expect(await (await harness.saveButton()).getProperty<boolean>('disabled')).toBe(true);
  });
  it('distinguishes an empty version history from a filter with no matches', async () => {
    const { harness } = await mount();
    await harness.click('[data-ai-history-tab]');
    await harness.fill('history-filter', 'No matching fixture reason');
    expect(await harness.text()).toContain('No loaded versions match this filter');
  });
  it('renders the empty history state', async () => {
    data.versions.mockResolvedValue({ items: [], next_cursor: null });
    const { harness } = await mount();
    await harness.click('[data-ai-history-tab]');
    expect(await harness.text()).toContain('No saved settings versions');
  });
  it('reuses the same idempotency key when retrying an unchanged failed intent', async () => {
    data.save.mockRejectedValue(
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
    await harness.fill('writing_style', 'A stable retry draft');
    await harness.click('[data-ai-save]');
    await harness.click('[data-ai-save]');
    expect(data.save).toHaveBeenCalledTimes(2);
    expect(data.save.mock.calls[0]?.[3]).toBe(data.save.mock.calls[1]?.[3]);
  });
});
