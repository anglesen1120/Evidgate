import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { computed, provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap } from '@angular/router';
import { of } from 'rxjs';
import { SessionStore } from '../../../core/session/session.store';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow/narrow-mutation-policy';
import { ImportExportDataService } from './import-export-data.service';
import { ExportPanelComponent, downloadExportFile } from './export-panel.component';
import { ExportStore } from './export.store';

class OptionListHarness extends ComponentHarness {
  static hostSelector = '[role="listbox"]';
  readonly options = this.locatorForAll('[role="option"]');
}

class ExportDialogHarness extends ComponentHarness {
  static hostSelector = '[role="dialog"]';
  readonly confirm = this.locatorFor('[data-export-confirm]');
}

class PanelHarness extends ComponentHarness {
  static hostSelector = 'app-authoring-export-panel';
  readonly scope = this.locatorFor('[data-export-scope]');
  readonly notice = this.locatorForOptional('[data-export-narrow-notice]');
  readonly scopeTrigger = this.locatorFor('#authoring-export-scope');
  readonly formatTrigger = this.locatorFor('#authoring-export-format');
  async scopeText(): Promise<string> {
    return (await this.scope()).text();
  }
  async focusScopeTrigger(): Promise<void> {
    await (await this.scopeTrigger()).focus();
  }
}

function stubDownloadUrls(url: string) {
  const create = vi.fn(() => url);
  const revoke = vi.fn();
  vi.stubGlobal(
    'URL',
    class extends URL {
      static override createObjectURL = create;
      static override revokeObjectURL = revoke;
    },
  );
  return { create, revoke };
}

describe('ExportPanelComponent', () => {
  provideLayoutTestStubs();
  const canMutate = signal(true);
  const download = vi.fn<ImportExportDataService['exportFile']>();
  let queryParams: Record<string, string> = {};
  afterEach(() => {
    TestBed.resetTestingModule();
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
    queryParams = {};
    canMutate.set(true);
  });

  async function render() {
    download.mockReset().mockResolvedValue({
      blob: new Blob(['x']),
      filename: 'authoring-export.csv',
    });
    TestBed.configureTestingModule({
      imports: [ExportPanelComponent],
      providers: [
        provideZonelessChangeDetection(),
        ExportStore,
        { provide: ImportExportDataService, useValue: { exportFile: download } },
        {
          provide: SessionStore,
          useValue: {
            currentProject: signal({ id: 'project', name: 'Storefront' }),
            has: () => true,
            onSessionEnd: () => () => undefined,
          },
        },
        {
          provide: NarrowMutationPolicy,
          useValue: {
            canMutate,
            narrow: computed(() => !canMutate()),
            allowsMutation: () => canMutate(),
          },
        },
        {
          provide: ActivatedRoute,
          useValue: { queryParamMap: of(convertToParamMap(queryParams)) },
        },
      ],
    });
    const fixture = TestBed.createComponent(ExportPanelComponent);
    await fixture.whenStable();
    const loader = TestbedHarnessEnvironment.documentRootLoader(fixture);
    const panel = await TestbedHarnessEnvironment.harnessForFixture(fixture, PanelHarness);
    return { fixture, loader, panel };
  }

  it('renders the initial Brain layout without reporting an Angular error', async () => {
    const error = vi.spyOn(console, 'error').mockImplementation(() => undefined);
    const { panel } = await render();
    expect(await panel.scopeText()).toContain('Whole project');
    expect(await panel.notice()).toBeNull();
    expect(error).not.toHaveBeenCalled();
  });

  it('offers XLSX through the existing selectable format picker without a request before confirmation', async () => {
    const { loader, panel, fixture } = await render();
    await (await panel.formatTrigger()).click();
    const list = await loader.getHarness(OptionListHarness);
    const options = await list.options();
    expect(await Promise.all(options.map((option) => option.text()))).toEqual([
      'CSV',
      'JSON',
      'XLSX',
    ]);
    const xlsx = options[2];
    if (!xlsx) throw new Error('Expected the XLSX option');
    expect(await xlsx.getAttribute('aria-disabled')).not.toBe('true');
    await xlsx.click();
    await fixture.whenStable();
    expect(TestBed.inject(ExportStore).draft().format).toBe('xlsx');
    expect(download).not.toHaveBeenCalled();
  });

  it('labels a selection scope as no scope instead of claiming the whole project', async () => {
    queryParams = { ids: 'selected-case' };
    const { panel } = await render();
    const label = await panel.scopeText();
    expect(label).toContain('No scope');
    expect(label).not.toContain('Whole project');
  });

  it('moves focus to the narrow notice when the focused scope control is removed', async () => {
    const { fixture, panel } = await render();
    await panel.focusScopeTrigger();
    expect(document.activeElement?.id).toBe('authoring-export-scope');
    canMutate.set(false);
    fixture.detectChanges();
    await fixture.whenStable();
    const notice = await panel.notice();
    expect(notice).not.toBeNull();
    expect(document.activeElement?.hasAttribute('data-export-narrow-notice')).toBe(true);
  });

  it('keeps the download URL alive for 30 s so a late start is not cancelled', () => {
    vi.useFakeTimers();
    const create = vi.fn(() => 'blob:export');
    const revoke = vi.fn();
    const original = globalThis.URL;
    vi.stubGlobal(
      'URL',
      class extends URL {
        static override createObjectURL = create;
        static override revokeObjectURL = revoke;
      },
    );
    const click = vi
      .spyOn(HTMLAnchorElement.prototype, 'click')
      .mockImplementation(() => undefined);
    try {
      downloadExportFile(document, {
        blob: new Blob(['kind\nnode']),
        filename: 'authoring-export.csv',
      });
      expect(click).toHaveBeenCalledTimes(1);
      expect(revoke).not.toHaveBeenCalled();
      vi.advanceTimersByTime(29_999);
      expect(revoke).not.toHaveBeenCalled();
      vi.advanceTimersByTime(1);
      expect(revoke).toHaveBeenCalledWith('blob:export');
    } finally {
      vi.stubGlobal('URL', original);
      vi.useRealTimers();
    }
  });

  it('releases a clicked XLSX object URL on abort and never inserts response markup', () => {
    vi.useFakeTimers();
    const { create, revoke } = stubDownloadUrls('blob:workbook');
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (
      this: HTMLAnchorElement,
    ) {
      expect(this.download).toBe('authoring-export.xlsx');
      expect(this.getAttribute('href')).toBe('blob:workbook');
    });
    const controller = new AbortController();
    const blob = new Blob(['<svg data-hostile-export onload="alert(1)"/>']);
    try {
      downloadExportFile(document, { blob, filename: 'authoring-export.xlsx' }, controller.signal);
      expect(create).toHaveBeenCalledWith(blob);
      expect(click).toHaveBeenCalledTimes(1);
      expect(document.querySelector('[data-hostile-export]')).toBeNull();
      expect(document.querySelector('a[download="authoring-export.xlsx"]')).toBeNull();
      controller.abort();
      expect(revoke).toHaveBeenCalledTimes(1);
      expect(revoke).toHaveBeenCalledWith('blob:workbook');
      vi.runAllTimers();
      expect(revoke).toHaveBeenCalledTimes(1);
    } finally {
      vi.useRealTimers();
    }
  });

  it('does not create an object URL for an already aborted download', () => {
    const { create, revoke } = stubDownloadUrls('blob:aborted');
    const controller = new AbortController();
    controller.abort();
    expect(() =>
      downloadExportFile(
        document,
        { blob: new Blob(['PK']), filename: 'authoring-export.xlsx' },
        controller.signal,
      ),
    ).toThrow();
    expect(create).not.toHaveBeenCalled();
    expect(revoke).not.toHaveBeenCalled();
  });

  it('releases a downloaded workbook URL when its panel is destroyed', async () => {
    const { fixture } = await render();
    const { create, revoke } = stubDownloadUrls('blob:destroyed');
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined);
    download.mockResolvedValueOnce({
      blob: new Blob(['PK']),
      filename: 'authoring-export.xlsx',
    });
    const store = TestBed.inject(ExportStore);
    store.edit({ format: 'xlsx' });
    store.openConfirmation();
    await fixture.whenStable();
    const loader = TestbedHarnessEnvironment.documentRootLoader(fixture);
    const confirm = await loader.getHarness(ExportDialogHarness);
    await (await confirm.confirm()).click();
    await fixture.whenStable();
    expect(create).toHaveBeenCalledTimes(1);
    fixture.destroy();
    expect(revoke).toHaveBeenCalledTimes(1);
    expect(revoke).toHaveBeenCalledWith('blob:destroyed');
  });
});
