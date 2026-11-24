import { TestBed } from '@angular/core/testing';
import type { AiSettingsVersion } from '@evidgate/sdk';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { AiSettingsStore } from './ai-settings.store';
import { setupAiSettingsPage, type AiSettingsPageSetup } from './ai-settings.page.testing';
import versionDocument from '../../../../../../../fixtures/web-api/ai-settings-version-1/input.json';

provideLayoutTestStubs();

describe('AiSettingsPage history paging', () => {
  const version = versionDocument.body as AiSettingsVersion;
  let data: AiSettingsPageSetup['data'];
  let mount: AiSettingsPageSetup['mount'];
  beforeEach(() => {
    ({ data, mount } = setupAiSettingsPage());
  });
  afterEach(() => {
    vi.useRealTimers();
    TestBed.resetTestingModule();
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
