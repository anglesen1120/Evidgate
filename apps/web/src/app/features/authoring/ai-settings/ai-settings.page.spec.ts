import { ComponentHarness, TestKey } from '@angular/cdk/testing';
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
import { AiSettingsStore } from './ai-settings.store';
import headDocument from '../../../../../../../fixtures/web-api/ai-settings-head/input.json';
import versionDocument from '../../../../../../../fixtures/web-api/ai-settings-version-1/input.json';
import templatesDocument from '../../../../../../../fixtures/web-api/ai-settings-templates/input.json';
import { SettingsHarness } from './ai-settings.testing';

provideLayoutTestStubs();

describe('AiSettingsPage', () => {
  const head = headDocument.body as AiSettingsView;
  const version = versionDocument.body as AiSettingsVersion;
  let writable: WritableSignal<boolean>;
  let readable: WritableSignal<boolean>;
  let project: WritableSignal<{ id: string } | null>;
  let data: { read: Mock; save: Mock; versions: Mock; version: Mock; templates: Mock };
  beforeEach(() => {
    writable = signal(true);
    readable = signal(true);
    project = signal<{ id: string } | null>({ id: 'p1' });
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
            currentProject: project,
            has: (code: string) => (code === AI_WRITE ? writable() : readable()),
          },
        },
        {
          provide: NarrowMutationPolicy,
          useValue: { canMutate: signal(true), narrow: signal(false), allowsMutation: () => true },
        },
      ],
    });
  });
  afterEach(() => {
    vi.useRealTimers();
    TestBed.resetTestingModule();
  });
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
  it('retains every field and its counter association in the grouped settings form', async () => {
    const { harness } = await mount();
    for (const section of ['context', 'guidance', 'priority', 'save']) {
      expect(await harness.count('#ai-group-' + section)).toBe(1);
    }
    for (const field of FIELDS) {
      const selector = '#ai-' + field.key;
      expect(await harness.count(selector)).toBe(1);
      expect(await harness.attribute(selector, 'aria-describedby')).toContain(
        'ai-' + field.key + '-counter',
      );
    }
    expect(await harness.count('#ai-languages')).toBe(1);
    expect(await harness.count('[data-guidance-counts] dt')).toBe(3);
    expect(await harness.count('app-ai-settings-version-card .identifier.compact')).toBe(1);
  });
  it('labels a missing author without inventing a name or a copyable identifier', async () => {
    data.read.mockResolvedValue({ view: { ...head, author: null }, etag: '"2"' });
    const { harness } = await mount();
    expect(await harness.count('app-ai-settings-version-card app-copyable-identifier')).toBe(0);
    expect(await harness.text()).toContain('Not supplied');
  });
  it('keeps a single unavailable consent status when inspecting another settings version', async () => {
    const { harness } = await mount();
    expect(await harness.count('#ai-consent-status')).toBe(1);
    expect(await harness.text()).toContain('AI consent unavailable');
    await harness.click('[data-ai-history-tab]');
    await harness.click('[data-ai-inspect]');
    expect(await harness.count('#ai-consent-status')).toBe(1);
    expect(await harness.text()).toContain('AI consent unavailable');
    expect(data.read).toHaveBeenCalledTimes(1);
  });
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
  it('explains a disabled Save and omits the first-version action without project write', async () => {
    writable.set(false);
    data.versions.mockResolvedValue({ items: [], next_cursor: null });
    const { harness } = await mount();
    expect(await (await harness.saveButton()).getProperty<boolean>('disabled')).toBe(true);
    expect(await harness.text()).toContain('project:write');
    await harness.click('[data-ai-history-tab]');
    expect(await harness.count('app-ai-settings-history app-empty-pattern button')).toBe(0);
  });
  it('renders version list and immutable detail from the detail fetch', async () => {
    const { harness } = await mount();
    await harness.click('[data-ai-history-tab]');
    expect(await harness.count('#ai-compare-reason')).toBe(0);
    expect(await harness.count('[aria-describedby="ai-compare-reason"]')).toBe(0);
    await harness.click('[data-ai-inspect]');
    expect(await harness.text()).toContain(version.settings.writing_style);
    expect(data.version).toHaveBeenCalledWith('p1', 1, expect.any(AbortSignal));
  });
  it('opens the loaded snapshot without selecting a comparison or retrieving its document', async () => {
    const reason = 'Long reason '.repeat(50).slice(0, 500);
    data.versions.mockResolvedValue({ items: [{ ...version, reason }], next_cursor: null });
    const { harness } = await mount();
    await harness.click('[data-ai-history-tab]');
    const opener = await harness.view();
    expect(await opener.getAttribute('aria-label')).toBe('View version 1');
    expect(await opener.text({ exclude: '[popover]' })).toBe('');
    expect(await opener.getProperty('tagName')).toBe('BUTTON');
    expect(
      await harness.attribute('button[aria-label="View version 1"] hlm-icon', 'aria-hidden'),
    ).toBe('true');
    await opener.focus();
    await opener.click();
    const title = await harness.dialogTitle();
    const dialog = await harness.dialog();
    expect(await dialog?.getAttribute('aria-labelledby')).toBe(await title.getAttribute('id'));
    expect(await dialog?.text()).toContain('general-en');
    expect(await dialog?.text()).toContain(reason);
    expect(await (await harness.dialogContent()).getAttribute('data-size')).toBe('lg');
    expect(await (await harness.dialogSubject()).text()).toBe('Version 1');
    expect(await (await harness.dialogStatusIcon()).getAttribute('aria-hidden')).toBe('true');
    await vi.waitFor(async () => expect(await title.isFocused()).toBe(true));
    await title.sendKeys(TestKey.ESCAPE);
    await vi.waitFor(async () => expect(await harness.dialog()).toBeNull());
    expect(await opener.isFocused()).toBe(true);
    expect(data.version).not.toHaveBeenCalled();
    expect(data.save).not.toHaveBeenCalled();
  });
  it('shows fetched guidance only for the matching history record', async () => {
    data.versions.mockResolvedValue({
      items: [
        version,
        { ...version, version_no: 2, content_hash: 'b'.repeat(64), reason: 'Other reason' },
      ],
      next_cursor: null,
    });
    const { harness } = await mount();
    await harness.click('[data-ai-history-tab]');
    expect(await harness.count('#ai-compare-reason')).toBe(1);
    const select = 'button[aria-label="Select version 1"]';
    const compare = 'button[aria-label="Compare with 2"]';
    expect(await harness.attribute(select, 'aria-pressed')).toBe('false');
    expect(await harness.attribute(compare, 'disabled')).not.toBeNull();
    expect(await harness.attribute(compare, 'aria-describedby')).toBe('ai-compare-reason');
    await harness.click(select);
    expect(await harness.attribute(select, 'aria-pressed')).toBe('true');
    expect(await harness.attribute(compare, 'disabled')).toBeNull();
    expect(await harness.attribute(select + ' hlm-icon', 'aria-hidden')).toBe('true');
    expect(await harness.attribute(compare + ' hlm-icon', 'aria-hidden')).toBe('true');
    const visiblePage = await (await harness.host()).text({ exclude: '[popover]' });
    expect(visiblePage).not.toContain('Select version 1');
    expect(visiblePage).not.toContain('Compare with 2');
    await harness.click('[data-ai-version-view="1"]');
    expect(await (await harness.dialog())?.text()).toContain(version.settings.writing_style);
    await (await harness.dialogTitle()).sendKeys(TestKey.ESCAPE);
    await vi.waitFor(async () => expect(await harness.dialog()).toBeNull());
    await harness.click('[data-ai-version-view="2"]');
    const second = await harness.dialog();
    expect(await second?.text()).toContain('Other reason');
    expect(await second?.text()).not.toContain(version.settings.writing_style);
    expect(data.version).toHaveBeenCalledTimes(1);
    await (await harness.dialogTitle()).sendKeys(TestKey.ESCAPE);
    await vi.waitFor(async () => expect(await harness.dialog()).toBeNull());
    await harness.click(compare);
    expect(data.version).toHaveBeenCalledWith('p1', 2, expect.any(AbortSignal));
    expect(data.save).not.toHaveBeenCalled();
  });
  it('moves between labelled panels on one horizontal tab stop without discarding the draft', async () => {
    const { harness } = await mount();
    await harness.fill('writing_style', 'Retained text');
    const settings = await harness.settingsTab();
    const history = await harness.historyTab();
    expect(await harness.attribute('[role="tablist"]', 'aria-orientation')).toBe('horizontal');
    await settings.focus();
    await settings.sendKeys(TestKey.RIGHT_ARROW);
    expect(await history.isFocused()).toBe(true);
    expect(await history.getAttribute('aria-selected')).toBe('true');
    expect(await settings.getAttribute('tabindex')).toBe('-1');
    const panel = await history.getAttribute('aria-controls');
    expect(await harness.attribute('#' + panel, 'aria-labelledby')).toBe(
      await history.getAttribute('id'),
    );
    await history.sendKeys(TestKey.HOME);
    expect(await settings.isFocused()).toBe(true);
    expect(await harness.value('writing_style')).toBe('Retained text');
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
    expect(await harness.text()).toContain('No versions on this page match this filter');
    expect(await harness.text()).toContain('Filter this page by reason');
    expect(await harness.text()).toContain('Fixed server order: newest version first');
    expect(await harness.count('app-ai-settings-history app-data-table')).toBe(0);
    expect(await harness.count('app-ai-settings-history app-empty-pattern button')).toBe(0);
  });
  it('keeps paging available instead of offering to create a first version on an empty cursor page', async () => {
    data.versions.mockResolvedValue({ items: [], next_cursor: 'x' });
    const { harness } = await mount();
    await harness.click('[data-ai-history-tab]');
    expect(await (await harness.emptyTitle()).text()).toBe('No versions on this page');
    expect(await harness.count('app-ai-settings-history app-empty-pattern :is(button, a)')).toBe(0);
    const next = await harness.nextHistoryPage();
    expect(await next.getProperty<boolean>('disabled')).toBe(false);
    await next.click();
    expect(data.versions).toHaveBeenCalledWith('p1', { limit: 20, cursor: 'x' }, expect.anything());
  });
  it('offers one empty state that returns focus to Settings without discarding the draft', async () => {
    data.versions.mockResolvedValue({ items: [], next_cursor: null });
    const { harness } = await mount();
    await harness.fill('writing_style', 'First version draft');
    await harness.click('[data-ai-history-tab]');
    expect(await harness.text()).toContain('No saved settings versions');
    expect(await harness.count('app-ai-settings-history app-empty-pattern')).toBe(1);
    expect(await harness.count('app-ai-settings-history app-data-table')).toBe(0);
    expect(await harness.count('#ai-compare-reason')).toBe(0);
    await harness.click('app-ai-settings-history app-empty-pattern button');
    const settings = await harness.settingsTab();
    expect(await settings.getAttribute('aria-selected')).toBe('true');
    expect(await settings.isFocused()).toBe(true);
    expect(await harness.value('writing_style')).toBe('First version draft');
    expect(data.save).not.toHaveBeenCalled();
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
  it('gives each output checkbox its own input id and visible language label', async () => {
    const { harness } = await mount();
    for (const language of ['en', 'vi']) {
      expect(await harness.count('#ai-language-' + language)).toBe(1);
      expect(await harness.accessibleLabel('#ai-language-' + language)).toContain(
        language === 'en' ? 'English' : 'Vietnamese',
      );
    }
    expect(await harness.attribute('#ai-language-label-en', 'id')).not.toBe(
      await harness.attribute('#ai-language-label-vi', 'id'),
    );
  });
  for (const kind of ['rate_limited', 'unavailable'] as const) {
    it('blocks Save and Retry until the ' + kind + ' deadline, even after editing', async () => {
      data.save.mockRejectedValue(
        new ApiRequestError({
          kind,
          status: kind === 'rate_limited' ? 429 : 503,
          code: kind,
          message: 'Wait',
          details: [],
          retryable: true,
          retryAfterSeconds: 2,
          requestId: null,
        }),
      );
      const { harness } = await mount();
      vi.useFakeTimers();
      await harness.click('[data-ai-save]');
      expect(await (await harness.saveButton()).getProperty<boolean>('disabled')).toBe(true);
      expect(await (await harness.retryButton()).getProperty<boolean>('disabled')).toBe(true);
      await harness.fill('writing_style', 'Edited while waiting');
      expect(await (await harness.saveButton()).getProperty<boolean>('disabled')).toBe(true);
      await vi.advanceTimersByTimeAsync(2000);
      expect(await (await harness.saveButton()).getProperty<boolean>('disabled')).toBe(false);
      expect(await (await harness.retryButton()).getProperty<boolean>('disabled')).toBe(false);
      expect(data.save).toHaveBeenCalledTimes(1);
    });
  }
  it('replaces a permission-lost loading state with the forbidden panel and removes the form', async () => {
    const { fixture, harness } = await mount();
    readable.set(false);
    await fixture.whenStable();
    expect(await harness.count('app-loading-pattern')).toBe(0);
    expect(await harness.count('app-forbidden-pattern')).toBe(1);
    expect(await harness.count('app-error-pattern')).toBe(0);
    expect(await harness.count('#ai-writing_style')).toBe(0);
  });
  it('renders the forbidden panel when the project scope is lost, without another read', async () => {
    const { fixture, harness } = await mount();
    expect(await harness.count('app-forbidden-pattern')).toBe(0);
    project.set(null);
    await fixture.whenStable();
    expect(await harness.count('app-forbidden-pattern')).toBe(1);
    expect(await harness.count('app-error-pattern')).toBe(0);
    expect(data.read).toHaveBeenCalledTimes(1);
    expect(await harness.count('#ai-writing_style')).toBe(0);
  });
  it('offers head reload after a successful save answers without an ETag', async () => {
    data.save.mockResolvedValue({ view: head, etag: '', appended: true });
    const { fixture, harness } = await mount();
    await harness.click('[data-ai-save]');
    expect(await (await harness.saveButton()).getProperty<boolean>('disabled')).toBe(true);
    await harness.click('[data-ai-reload]');
    await fixture.whenStable();
    expect(data.read).toHaveBeenCalledTimes(2);
    expect(await (await harness.saveButton()).getProperty<boolean>('disabled')).toBe(false);
  });
  it('compares current and replacement output languages before replacing a template', async () => {
    data.read.mockResolvedValue({
      view: { ...head, settings: { ...head.settings, languages: ['vi'] } },
      etag: '"2"',
    });
    const template = templatesDocument.body.items[0];
    data.templates.mockResolvedValue({
      items: [{ ...template, settings: { ...template?.settings, languages: ['en'] } }],
      next_cursor: null,
    });
    const { fixture, harness } = await mount();
    await harness.click('[data-ai-template]');
    const dialog = await TestbedHarnessEnvironment.documentRootLoader(fixture).getHarness(
      class extends ComponentHarness {
        static hostSelector = '[role="dialog"]';
        readonly before = this.locatorFor('[data-ai-languages-before]');
        readonly after = this.locatorFor('[data-ai-languages-after]');
      },
    );
    expect(await (await harness.dialogContent()).getAttribute('data-size')).toBe('lg');
    expect(await (await harness.dialogSubject()).text()).toContain(template?.name);
    expect(await (await harness.dialogStatusIcon()).getAttribute('aria-hidden')).toBe('true');
    expect(await (await dialog.before()).text()).toContain('Before');
    expect(await (await dialog.before()).text()).toContain('vi');
    expect(await (await dialog.after()).text()).toContain('After');
    expect(await (await dialog.after()).text()).toContain('en');
    expect(data.save).not.toHaveBeenCalled();
  });
  it('refuses direct store saves before Retry-After and permits them at the deadline', async () => {
    const { fixture } = await mount();
    const store = fixture.debugElement.injector.get(AiSettingsStore);
    data.save.mockRejectedValueOnce(
      new ApiRequestError({
        kind: 'unavailable',
        status: 503,
        code: 'unavailable',
        message: 'Wait',
        details: [],
        retryable: true,
        retryAfterSeconds: 2,
        requestId: null,
      }),
    );
    vi.useFakeTimers();
    await store.save();
    store.setReason('Edited reason');
    await vi.advanceTimersByTimeAsync(1999);
    expect(await store.save()).toBe(false);
    expect(data.save).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(1);
    expect(await store.save()).toBe(true);
    expect(data.save).toHaveBeenCalledTimes(2);
  });
  it('resets history to the first page after saving from the second page', async () => {
    data.versions.mockResolvedValue({ items: [version], next_cursor: 'page-two' });
    const { fixture } = await mount();
    const store = fixture.debugElement.injector.get(AiSettingsStore);
    store.nextPage();
    await fixture.whenStable();
    expect(store.cursor()).toBe('page-two');
    await store.save();
    await fixture.whenStable();
    expect(store.cursor()).toBeUndefined();
    expect(store.cursorPath()).toEqual([]);
    expect(data.versions).toHaveBeenLastCalledWith('p1', { limit: 20 }, expect.any(AbortSignal));
  });
  it('resets cursor paging when the reason filter changes and clears the filter on navigation', async () => {
    data.versions.mockImplementation((_project: string, query: { cursor?: string }) =>
      Promise.resolve({
        items: [{ ...version, reason: query.cursor ? 'Older reason' : 'First reason' }],
        next_cursor: query.cursor ? null : 'older',
      }),
    );
    const { fixture, harness } = await mount();
    const store = fixture.debugElement.injector.get(AiSettingsStore);
    await harness.click('[data-ai-history-tab]');
    await (await harness.nextHistoryPage()).click();
    expect(store.page()).toBe(2);
    await harness.fill('history-filter', 'First');
    expect(store.page()).toBe(1);
    expect(store.cursorPath()).toEqual([]);
    expect(await harness.text()).toContain('First reason');
    await (await harness.nextHistoryPage()).click();
    expect(await harness.value('history-filter')).toBe('');
    expect(await harness.text()).toContain('Older reason');
  });
});
