import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';
import { EditorSandboxComponent } from './editor-sandbox.component';
import { draftEditorFixture } from './draft-editor.fixture';
import { EditorHarness } from './editor.testing';

describe('EditorSandboxComponent', () => {
  it.each([
    ['default', 'draft'],
    ['passed', 'dry_passed'],
    ['running', 'dry_running'],
    ['failed', 'dry_failed'],
  ])('preserves the original failure and separate unset decision in %s', async (state, status) => {
    const fixture = TestBed.createComponent(EditorSandboxComponent);
    fixture.componentRef.setInput('state', state);
    fixture.componentRef.setInput('model', draftEditorFixture);
    const panel = await TestbedHarnessEnvironment.harnessForFixture(fixture, EditorHarness);
    expect(await panel.text()).toContain(status);
    expect(await panel.text()).toContain('Order receipt missing.');
    expect(await panel.text()).toContain('ev-s1');
    expect(await panel.text()).toContain('Screenshot ready; trace restricted');
    expect(await (await panel.find('app-decision-chip')).text()).toContain('Pending');
    expect(await (await panel.find('app-decision-chip')).text()).not.toContain('PASS');
  });

  it('renders script content as text, never as elements', async () => {
    const script = '<strong data-script-node>receipt</strong>';
    const fixture = TestBed.createComponent(EditorSandboxComponent);
    fixture.componentRef.setInput('state', 'default');
    fixture.componentRef.setInput('model', {
      ...draftEditorFixture,
      sandbox: { ...draftEditorFixture.sandbox, script },
    });
    const panel = await TestbedHarnessEnvironment.harnessForFixture(fixture, EditorHarness);
    await (await panel.find('summary')).click();
    expect(await (await panel.find('pre code')).text()).toBe(script);
    expect(await panel.all('[data-script-node]')).toHaveLength(0);
  });
});
