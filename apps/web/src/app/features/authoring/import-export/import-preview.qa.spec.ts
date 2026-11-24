import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { ImportPreviewComponent } from './import-preview.component';
import type { LocalPreview } from './local-file-preview';

class PreviewQaHarness extends ComponentHarness {
  static hostSelector = 'app-import-preview';
  async summary() {
    return (await this.locatorFor('app-data-table-pager [role="status"]')()).text();
  }
  async focused(index: number) {
    return (await this.locatorFor(`#import-row-${index}-title`)()).isFocused();
  }
}

describe('Import preview error-index page boundaries', () => {
  provideLayoutTestStubs();
  afterEach(() => TestBed.resetTestingModule());
  it('reveals indices on both sides of exact page boundaries without changing original rows', async () => {
    const fixture = TestBed.createComponent(ImportPreviewComponent);
    const local: LocalPreview = {
      issue: null,
      rows: Array.from({ length: 41 }, (_, offset) => ({
        index: offset + 1,
        cells: { title: 'Row ' + (offset + 1), key: 'key-' + offset, kind: 'node' },
      })),
    };
    const before = JSON.stringify(local);
    fixture.componentRef.setInput('local', local);
    fixture.componentRef.setInput(
      'details',
      [20, 21, 40, 41].map((index) => ({ index, path: '/title', reason: 'invalid_text' })),
    );
    await fixture.whenStable();
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, PreviewQaHarness);
    for (const [index, range] of [
      [20, '1–20 of 41'],
      [21, '21–40 of 41'],
      [40, '21–40 of 41'],
      [41, '41–41 of 41'],
    ] as const) {
      fixture.componentInstance.reveal(index, '/title');
      fixture.detectChanges();
      await fixture.whenStable();
      expect(await harness.summary()).toContain(range);
      expect(await harness.focused(index)).toBe(true);
    }
    expect(fixture.componentInstance.local()).toBe(local);
    expect(JSON.stringify(local)).toBe(before);
    fixture.componentRef.setInput('details', []);
    await fixture.whenStable();
    expect(await harness.summary()).toContain('1–20 of 41');
    expect(local.rows).toHaveLength(41);
  });
});
