import { registerLocaleData } from '@angular/common';
import localeVi from '@angular/common/locales/vi';
import { TestKey } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { LOCALE_ID, provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { EvidenceRevision } from '@evidgate/sdk';
import { stubScopeBrowserApis } from '../../../../shell/layout/scope.fixture';
import { EvidenceRevisionSelectorComponent } from './evidence-revision-selector.component';
import { EvidenceRevisionSelectorHarness } from './evidence-revision-selector.test-support';

registerLocaleData(localeVi);

const revisions: readonly EvidenceRevision[] = [4, 3].map((revision_no) => ({
  revision_no,
  created_at: '2026-05-01T10:00:00Z',
  completeness: 'complete',
  missing: [],
  evaluation_id: null,
}));

describe('EvidenceRevisionSelectorComponent', () => {
  let restoreBrowserApis: () => void;
  beforeEach(() => {
    restoreBrowserApis = stubScopeBrowserApis();
    TestBed.configureTestingModule({
      imports: [EvidenceRevisionSelectorComponent],
      providers: [provideZonelessChangeDetection()],
    });
  });
  afterEach(() => {
    TestBed.resetTestingModule();
    restoreBrowserApis();
    vi.unstubAllGlobals();
  });

  async function render(selectedRevision: number | null = 4, rows = revisions) {
    const fixture = TestBed.createComponent(EvidenceRevisionSelectorComponent);
    fixture.componentRef.setInput('revisions', rows);
    fixture.componentRef.setInput('selectedRevision', selectedRevision);
    await fixture.whenStable();
    const harness = await TestbedHarnessEnvironment.harnessForFixture(
      fixture,
      EvidenceRevisionSelectorHarness,
    );
    return { fixture, harness, root: fixture.nativeElement as HTMLElement };
  }

  it('labels and describes the actual combobox and displays the loaded pin', async () => {
    const { harness, root } = await render(3);
    const trigger = await harness.trigger();
    expect(await trigger.getAttribute('role')).toBe('combobox');
    expect(await trigger.getAttribute('aria-describedby')).toBe('evidence-pin-help');
    expect(await trigger.getAttribute('aria-expanded')).toBe('false');
    expect(root.querySelector('label')?.getAttribute('for')).toBe(await trigger.getAttribute('id'));
    expect(await trigger.text()).toBe('Revision 3 · 5/1/26, 10:00\u202fAM UTC');
    expect(root.querySelector('#evidence-pin-help')?.textContent).toContain('stays pinned');
  });

  it.each([
    ['vi', '2026-01-02T03:04:05Z', '03:04 02/01/2026'],
    ['vi-VN', '2026-01-02T03:04:05Z', '03:04 02/01/2026'],
    ['vi', '2026-12-31T23:59:59Z', '23:59 31/12/2026'],
    ['vi', '2027-01-01T00:00:00Z', '00:00 01/01/2027'],
    ['en-US', '2026-01-02T03:04:05Z', '1/2/26, 3:04\u202fAM'],
  ])(
    'renders exact short dates for %s at %s in the pin and option',
    async (locale, at, dateTime) => {
      TestBed.overrideProvider(LOCALE_ID, { useValue: locale });
      const { harness } = await render(4, [{ ...revisions[0]!, created_at: at }]);
      const expected = `Revision 4 · ${dateTime} UTC`;
      const trigger = await harness.trigger();
      expect(await trigger.text()).toBe(expected);
      await trigger.click();
      const options = await harness.options();
      expect(options).toHaveLength(1);
      const option = options[0];
      if (!option) throw new Error('Missing revision option');
      expect(await option.text()).toBe(expected);
    },
  );

  it('names a missing pin instead of silently substituting a loaded revision', async () => {
    const { fixture, harness } = await render(2);
    const selected = vi.fn();
    fixture.componentInstance.selectRevision.subscribe(selected);
    const trigger = await harness.trigger();
    expect(await trigger.text()).toContain('Revision 2 (not loaded)');
    await trigger.click();
    const placeholder = (await harness.options())[0];
    if (!placeholder) throw new Error('Missing unloaded revision placeholder');
    expect(await placeholder.getAttribute('aria-disabled')).toBe('true');
    expect(await placeholder.text()).toContain('Revision 2 (not loaded)');
    await placeholder.click();
    expect(selected).not.toHaveBeenCalled();
  });

  it('emits loaded revisions without changing the parent-owned pin', async () => {
    const { fixture, harness } = await render();
    const selected = vi.fn();
    fixture.componentInstance.selectRevision.subscribe(selected);
    await harness.choose(3);
    expect(selected).toHaveBeenCalledExactlyOnceWith(3);
    expect(fixture.componentInstance.selectedRevision()).toBe(4);
  });

  it('explores by keyboard and commits only on Enter', async () => {
    const { fixture, harness } = await render();
    const selected = vi.fn();
    fixture.componentInstance.selectRevision.subscribe(selected);
    const trigger = await harness.trigger();
    await trigger.sendKeys(TestKey.ENTER);
    expect(await trigger.getAttribute('aria-expanded')).toBe('true');
    expect(await (await harness.listbox()).getAttribute('aria-label')).toBe('Evidence revision');
    await trigger.sendKeys(TestKey.END);
    expect(selected).not.toHaveBeenCalled();
    await trigger.sendKeys(TestKey.ENTER);
    expect(selected).toHaveBeenCalledExactlyOnceWith(3);
    expect(await trigger.getAttribute('aria-expanded')).toBe('false');
  });

  it('closes with Escape without selecting a revision', async () => {
    const { fixture, harness } = await render();
    const selected = vi.fn();
    fixture.componentInstance.selectRevision.subscribe(selected);
    const trigger = await harness.trigger();
    await trigger.click();
    await trigger.sendKeys(TestKey.END);
    await trigger.sendKeys(TestKey.ESCAPE);
    expect(await trigger.getAttribute('aria-expanded')).toBe('false');
    expect(selected).not.toHaveBeenCalled();
  });

  it('keeps the popup fully opaque and option labels wrap-capable', async () => {
    const { harness } = await render();
    await (await harness.trigger()).click();
    expect(document.querySelector('hlm-select-content')?.classList.contains('animate-none')).toBe(
      true,
    );
    const option = (await harness.options())[0];
    if (!option) throw new Error('Missing revision option');
    expect(await option.hasClass('whitespace-normal')).toBe(true);
    expect(await option.text()).toContain('UTC');
  });

  it('disables an empty selector rather than inventing options', async () => {
    const { harness } = await render(null, []);
    expect(await (await harness.trigger()).getProperty('disabled')).toBe(true);
    expect(await (await harness.trigger()).text()).toContain('Evidence revision');
  });

  it('offers explicit older-page loading and prevents repeat clicks while busy', async () => {
    const { fixture, root } = await render();
    const older = vi.fn();
    fixture.componentInstance.loadOlder.subscribe(older);
    expect(root.querySelector('[data-evidence-load-older]')).toBeNull();
    fixture.componentRef.setInput('hasMore', true);
    await fixture.whenStable();
    const action = root.querySelector<HTMLButtonElement>('[data-evidence-load-older]');
    if (!action) throw new Error('Missing load older action');
    action.click();
    expect(older).toHaveBeenCalledTimes(1);
    fixture.componentRef.setInput('loadingMore', true);
    await fixture.whenStable();
    expect(action.disabled).toBe(true);
    expect(action.textContent).toContain('Loading older revisions');
    action.click();
    expect(older).toHaveBeenCalledTimes(1);
  });
});
