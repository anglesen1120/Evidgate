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

class PanelHarness extends ComponentHarness {
  static hostSelector = 'app-authoring-export-panel';
  readonly scope = this.locatorFor('[data-export-scope]');
  readonly notice = this.locatorForOptional('[data-export-narrow-notice]');
  readonly scopeTrigger = this.locatorFor('#authoring-export-scope');
  readonly formatTrigger = this.locatorFor('#authoring-export-format');
  readonly xlsxReason = this.locatorFor('[data-export-xlsx-reason]');
  async scopeText(): Promise<string> {
    return (await this.scope()).text();
  }
  async focusScopeTrigger(): Promise<void> {
    await (await this.scopeTrigger()).focus();
  }
}

describe('ExportPanelComponent', () => {
  provideLayoutTestStubs();
  const canMutate = signal(true);
  const download = vi.fn<ImportExportDataService['exportFile']>();
  let queryParams: Record<string, string> = {};
  afterEach(() => {
    TestBed.resetTestingModule();
    vi.restoreAllMocks();
    queryParams = {};
    canMutate.set(true);
  });

  async function render() {
    download.mockResolvedValue({ blob: new Blob(['x']), filename: 'authoring-export.csv' });
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

  it('announces the XLSX choice as unavailable with its reason and never selects it', async () => {
    const { loader, panel, fixture } = await render();
    expect(await (await panel.xlsxReason()).text()).toContain('XLSX export is unavailable');
    await (await panel.formatTrigger()).click();
    const list = await loader.getHarness(OptionListHarness);
    const options = await list.options();
    const described = await Promise.all(
      options.map(async (option) => ({
        text: await option.text(),
        disabled: (await option.getAttribute('aria-disabled')) === 'true',
        describedBy: await option.getAttribute('aria-describedby'),
      })),
    );
    expect(described).toEqual([
      { text: 'CSV', disabled: false, describedBy: null },
      { text: 'JSON', disabled: false, describedBy: null },
      { text: 'XLSX — unavailable', disabled: true, describedBy: 'export-xlsx-reason' },
    ]);
    const xlsx = options[2];
    if (!xlsx) throw new Error('Expected the unavailable XLSX option');
    await xlsx.click();
    await fixture.whenStable();
    expect(TestBed.inject(ExportStore).draft().format).toBe('csv');
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
});
