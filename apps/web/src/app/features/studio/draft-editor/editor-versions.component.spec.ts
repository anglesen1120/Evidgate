import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';
import { EditorVersionsComponent } from './editor-versions.component';
import { draftEditorFixture } from './draft-editor.fixture';
import { EditorHarness } from './editor.testing';

describe('EditorVersionsComponent', () => {
  it('keeps published versions immutable and proposed versions separate', async () => {
    const fixture = TestBed.createComponent(EditorVersionsComponent);
    fixture.componentRef.setInput('model', draftEditorFixture);
    const panel = await TestbedHarnessEnvironment.harnessForFixture(fixture, EditorHarness);
    const rows = await panel.all('[data-revision]');
    expect(await Promise.all(rows.map((row) => row.text()))).toEqual([
      expect.stringMatching(/SHOP-TC-0051 v2.*Approved.*Immutable/s),
      expect.stringMatching(/SHOP-TC-0051 v3.*Awaiting review/s),
      expect.stringMatching(/SHOP-TC-0042 v4.*Approved.*Immutable/s),
      expect.stringMatching(/SHOP-TC-0042 v5.*Drafting/s),
    ]);
    expect(await panel.all('[data-revision="v3"] .status-pill--neutral hlm-icon')).toHaveLength(1);
    expect(await panel.all('ol li')).toHaveLength(7);
    const result = await panel.find('#editor-step-result');
    expect(await result.getProperty('readOnly')).toBe(true);
    expect(await result.getProperty('value')).toBe('Discount field is visible');
  });
});
