import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';
import { EditorValidationComponent } from './editor-validation.component';
import { draftEditorFixture } from './draft-editor.fixture';
import { EditorHarness } from './editor.testing';

describe('EditorValidationComponent', () => {
  it.each([
    ['default', 'AI-generated · untrusted'],
    ['aiempty', 'No suggestion for this revision.'],
    ['aiout', 'AI unavailable: no server suggestion returned.'],
  ])('renders %s without applying suggested code', async (state, message) => {
    const fixture = TestBed.createComponent(EditorValidationComponent);
    fixture.componentRef.setInput('state', state);
    fixture.componentRef.setInput('model', draftEditorFixture);
    const panel = await TestbedHarnessEnvironment.harnessForFixture(fixture, EditorHarness);
    expect(await panel.text()).toContain(message);
    expect(await panel.all('li app-status-pill hlm-icon')).toHaveLength(3);
    expect(await panel.text()).toContain('toBeVisible() → toBeTruthy()');
  });
});
