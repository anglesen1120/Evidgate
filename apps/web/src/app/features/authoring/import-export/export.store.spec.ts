import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap } from '@angular/router';
import { of } from 'rxjs';
import { SessionStore } from '../../../core/session/session.store';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow/narrow-mutation-policy';
import { ImportExportDataService } from './import-export-data.service';
import { ExportStore } from './export.store';

const NODE = '018f0000-0000-7000-8000-000000000055';
describe('ExportStore exact confirmed scope', () => {
  const download = vi.fn<ImportExportDataService['exportFile']>();
  let store: InstanceType<typeof ExportStore>;
  let desktop = true;
  beforeEach(() => {
    download
      .mockReset()
      .mockResolvedValue({ blob: new Blob(['export']), filename: 'authoring-export.csv' });
    desktop = true;
    TestBed.configureTestingModule({
      providers: [
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
        { provide: NarrowMutationPolicy, useValue: { allowsMutation: () => desktop } },
        { provide: ActivatedRoute, useValue: { queryParamMap: of(convertToParamMap({})) } },
      ],
    });
    store = TestBed.inject(ExportStore);
  });
  afterEach(() => TestBed.resetTestingModule());

  it('does not request before explicit dialog confirmation and carries exactly the chosen node, format and include kinds', async () => {
    store.edit({ scope: 'node', nodeId: NODE, format: 'xlsx', kinds: ['items', 'cases'] });
    store.openConfirmation();
    expect(download).not.toHaveBeenCalled();
    expect(store.confirmation()?.query).toEqual({
      format: 'xlsx',
      node_id: NODE,
      include: 'items,cases',
    });
    await store.confirmDownload();
    expect(download.mock.calls[0]?.slice(0, 2)).toEqual([
      'project',
      { format: 'xlsx', node_id: NODE, include: 'items,cases' },
    ]);
  });

  it('renders selected and current filtered export unavailable with a reason, never widening them', async () => {
    for (const scope of ['selection', 'filtered'] as const) {
      store.edit({ scope });
      expect(store.unavailableReason()).toContain('no ids or filter parameter');
      store.openConfirmation();
      await store.confirmDownload();
    }
    expect(download).not.toHaveBeenCalled();
  });

  it('refuses empty node scope, malformed UUID and no include kinds instead of using server defaults', () => {
    store.edit({ scope: 'node', nodeId: '' });
    expect(store.unavailableReason()).toContain('No node is selected');
    store.edit({ nodeId: 'not-a-uuid' });
    expect(store.query()).toBeNull();
    store.edit({ scope: 'project', kinds: [] });
    expect(store.query()).toBeNull();
    store.openConfirmation();
    expect(store.confirmation()).toBeNull();
  });

  it('keeps read-only project export available on narrow view but guards scope-changing handlers', async () => {
    desktop = false;
    store.edit({ scope: 'node', nodeId: NODE });
    expect(store.draft().scope).toBe('project');
    store.openConfirmation();
    await store.confirmDownload();
    expect(download.mock.calls[0]?.[1]).toEqual({ format: 'csv', include: 'nodes,items,cases' });
  });

  it('rejects a response for a scope that changed while the request was pending', async () => {
    let finish: ((value: { blob: Blob; filename: string }) => void) | undefined;
    // This compiler project targets ES2022, before Promise.withResolvers.
    download.mockReturnValueOnce(
      new Promise((resolve) => {
        finish = resolve;
      }),
    );
    store.openConfirmation();
    const pending = store.confirmDownload();
    store.edit({ scope: 'node', nodeId: NODE });
    finish?.({ blob: new Blob(['export']), filename: 'authoring-export.csv' });
    expect(await pending).toBeNull();
  });
});
