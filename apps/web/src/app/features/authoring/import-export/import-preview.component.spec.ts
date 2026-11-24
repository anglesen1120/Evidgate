import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';
import type { ImportResponse } from '@evidgate/sdk';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { ImportOptionHarness } from './import-export-page.harness';
import { ImportPreviewComponent } from './import-preview.component';
import type { LocalPreview } from './local-file-preview';

provideLayoutTestStubs();

class PreviewHarness extends ComponentHarness {
  static hostSelector = 'app-import-preview';
  async summary(): Promise<string> {
    return (await this.locatorFor('app-data-table-pager [role="status"]')()).text();
  }
  async move(direction: 'previous' | 'next'): Promise<void> {
    await (await this.locatorFor(`[data-page="${direction}"]`)()).click();
  }
  async available(direction: 'previous' | 'next'): Promise<boolean> {
    return (await this.locatorFor(`[data-page="${direction}"]`)())
      .getAttribute('aria-disabled')
      .then((value) => value !== 'true');
  }
  async hasRow(index: number): Promise<boolean> {
    return (await this.locatorForOptional(`#import-row-${index}-status`)()) !== null;
  }
  async focused(index: number): Promise<boolean> {
    return (await this.locatorFor(`#import-row-${index}-title`)()).isFocused();
  }
  async openSize(): Promise<void> {
    await (await this.locatorFor('app-data-table-pager hlm-select-trigger button')()).click();
  }
}

function local(count: number): LocalPreview {
  return {
    issue: null,
    rows: Array.from({ length: count }, (_, offset) => ({
      index: offset + 1,
      cells: { title: `Title ${offset + 1}`, kind: 'node', key: `n${offset + 1}` },
    })),
  };
}

function report(count: number): ImportResponse {
  return {
    batch_id: null,
    counts: { created: count, updated: 0, restored: 0, unchanged: 0 },
    rows: Array.from({ length: count }, (_, offset) => ({
      index: offset + 1,
      key: `n${offset + 1}`,
      kind: 'node',
      outcome: 'create',
      code: null,
    })),
  };
}

describe('Import preview client paging', () => {
  afterEach(() => TestBed.resetTestingModule());
  async function setup(count: number) {
    const fixture = TestBed.createComponent(ImportPreviewComponent);
    const source = local(count);
    const response = report(count);
    fixture.componentRef.setInput('local', source);
    fixture.componentRef.setInput('report', response);
    fixture.detectChanges();
    await fixture.whenStable();
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, PreviewHarness);
    return { fixture, harness, source, response };
  }

  it.each([0, 1])('shows an honest range and no navigation for %s rows', async (count) => {
    const { harness } = await setup(count);
    expect(await harness.summary()).toBe(`${count}–${count} of ${count}`);
    expect(await harness.available('previous')).toBe(false);
    expect(await harness.available('next')).toBe(false);
    await harness.move('next');
    expect(await harness.summary()).toBe(`${count}–${count} of ${count}`);
  });

  it('pages first/middle/last and Previous after Next without mutating any preview payload rows', async () => {
    const { harness, source, response, fixture } = await setup(41);
    const before = JSON.stringify({ source, response });
    expect(await harness.summary()).toBe('1–20 of 41');
    expect(await harness.hasRow(21)).toBe(false);
    await harness.move('next');
    expect(await harness.summary()).toBe('21–40 of 41');
    expect(await harness.hasRow(1)).toBe(false);
    expect(await harness.hasRow(21)).toBe(true);
    await harness.move('next');
    expect(await harness.summary()).toBe('41–41 of 41');
    expect(await harness.available('next')).toBe(false);
    await harness.move('next');
    expect(await harness.summary()).toBe('41–41 of 41');
    await harness.move('previous');
    expect(await harness.summary()).toBe('21–40 of 41');
    expect(JSON.stringify({ source, response })).toBe(before);
    expect(fixture.componentInstance.report()).toBe(response);
    expect(fixture.componentInstance.local()).toBe(source);
    expect(response.rows).toHaveLength(41);
  });

  it('resets to the first page for 50/100/20 sizes and when the input rows change', async () => {
    const { harness, fixture } = await setup(101);
    const root = TestbedHarnessEnvironment.documentRootLoader(fixture);
    for (const size of [50, 100, 20]) {
      await harness.move('next');
      await harness.openSize();
      const options = await root.getAllHarnesses(ImportOptionHarness);
      expect(await Promise.all(options.map((option) => option.text()))).toEqual([
        '20',
        '50',
        '100',
      ]);
      for (const option of options)
        if ((await option.text()) === String(size)) await option.click();
      expect(await harness.summary()).toBe(`1–${size} of 101`);
    }
    await harness.move('next');
    fixture.componentRef.setInput('local', local(1));
    fixture.componentRef.setInput('report', report(1));
    fixture.detectChanges();
    await fixture.whenStable();
    expect(await harness.summary()).toBe('1–1 of 1');
  });

  it('jumps to the absolute validation index page and focuses its field without losing other rows', async () => {
    const { fixture, harness } = await setup(61);
    fixture.componentRef.setInput('details', [
      { index: 61, path: '/title', reason: 'invalid_text' },
    ]);
    fixture.detectChanges();
    fixture.componentInstance.reveal(61, '/title');
    fixture.detectChanges();
    await fixture.whenStable();
    expect(await harness.summary()).toBe('61–61 of 61');
    expect(await harness.focused(61)).toBe(true);
    fixture.componentInstance.reveal(999, '/title');
    fixture.detectChanges();
    expect(await harness.summary()).toBe('61–61 of 61');
    await harness.move('previous');
    expect(await harness.summary()).toBe('41–60 of 61');
  });

  it('finds a sparse server validation index by its row position, not by dividing its absolute index', async () => {
    const { fixture, harness } = await setup(21);
    fixture.componentRef.setInput('details', [
      { index: 1001, path: '/title', reason: 'invalid_text' },
    ]);
    fixture.detectChanges();
    fixture.componentInstance.reveal(1001, '/title');
    fixture.detectChanges();
    await fixture.whenStable();
    expect(await harness.summary()).toBe('21–22 of 22');
    expect(await harness.focused(1001)).toBe(true);
    expect(fixture.componentInstance.report()?.rows).toHaveLength(21);
  });
});
