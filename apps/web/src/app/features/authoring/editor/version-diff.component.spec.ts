import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { AuthoringVersionDetailDto } from '@evidgate/sdk';
import type { AuthoringVersionHead } from '../../../core/data/authoring-editor-data.service';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { VersionDiffComponent } from './version-diff.component';

provideLayoutTestStubs();
class DiffHarness extends ComponentHarness {
  static hostSelector = 'app-editor-version-diff';
  async text() {
    return (await this.host()).text();
  }
  async count(selector: string) {
    return (await this.locatorForAll(selector)()).length;
  }
}
function head(content: AuthoringVersionDetailDto['content'], no: number): AuthoringVersionHead {
  return {
    etag: '"sha256:' + String(no).repeat(64) + '"',
    view: {
      version_id: 'version-' + no,
      entity_kind: 'manual_test_case',
      entity_id: 'case',
      version_no: no,
      batch_id: 'batch',
      change_kind: 'update',
      source: 'manual',
      content_hash: 'sha256:' + String(no).repeat(64),
      author: null,
      reason: null,
      ai_run_id: null,
      created_at: '2026-11-19T00:00:00Z',
      content,
    },
  };
}
describe('VersionDiffComponent', () => {
  beforeEach(() =>
    TestBed.configureTestingModule({
      imports: [VersionDiffComponent],
      providers: [provideZonelessChangeDetection()],
    }),
  );
  afterEach(() => TestBed.resetTestingModule());
  async function mount(
    left: AuthoringVersionDetailDto['content'],
    right: AuthoringVersionDetailDto['content'],
  ) {
    const fixture = TestBed.createComponent(VersionDiffComponent);
    fixture.componentRef.setInput('left', head(left, 1));
    fixture.componentRef.setInput('right', head(right, 2));
    await fixture.whenStable();
    return TestbedHarnessEnvironment.harnessForFixture(fixture, DiffHarness);
  }
  it('compares actual stored values and distinguishes missing fields from explicit null', async () => {
    const harness = await mount(
      { title: 'Before title', removed: null },
      { title: 'After title', added: 'New value' },
    );
    expect(await harness.text()).toContain('Before title');
    expect(await harness.text()).toContain('After title');
    expect(await harness.text()).toContain('Not present in this snapshot');
    expect(await harness.text()).toContain('null');
    expect(await harness.text()).toContain('− Before');
    expect(await harness.text()).toContain('+ After');
  });
  it('renders hostile markup as text, not executable HTML', async () => {
    const hostile = '<img src=x onerror=alert(1)><script>alert(1)</script>';
    const harness = await mount({ title: hostile }, { title: hostile });
    expect(await harness.text()).toContain(hostile);
    expect(await harness.count('img, script')).toBe(0);
    expect(await harness.text()).toContain('Unchanged');
  });
  it('renders an explicit empty state for two empty snapshots', async () => {
    expect(await (await mount({}, {})).text()).toContain('Both stored snapshots are empty');
  });
});
