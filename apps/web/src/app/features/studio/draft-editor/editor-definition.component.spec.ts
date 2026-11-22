import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow/narrow-mutation-policy';
import { EditorDefinitionComponent } from './editor-definition.component';
import { draftEditorFixture } from './draft-editor.fixture';
import { EditorHarness } from './editor.testing';

async function render(state: string, desktop = true) {
  TestBed.configureTestingModule({
    imports: [EditorDefinitionComponent],
    providers: [
      {
        provide: NarrowMutationPolicy,
        useValue: { canMutate: signal(desktop), allowsMutation: () => desktop },
      },
    ],
  });
  const fixture = TestBed.createComponent(EditorDefinitionComponent);
  fixture.componentRef.setInput('model', draftEditorFixture);
  fixture.componentRef.setInput('state', state);
  return TestbedHarnessEnvironment.harnessForFixture(fixture, EditorHarness);
}

describe('EditorDefinitionComponent', () => {
  it.each(['403', '412', '429', 'busy', 'running', 'published', 'merged', 'readonly'])(
    'locks fields in %s',
    async (state) => {
      const panel = await render(state);
      expect(await (await panel.find('#editor-assertion')).getProperty('readOnly')).toBe(true);
    },
  );
  it('validates locally and distinguishes an unsaved edit from the saved source', async () => {
    const panel = await render('invalid');
    const field = await panel.find('#editor-assertion');
    expect(await field.getAttribute('aria-invalid')).toBe('true');
    expect(await field.getAttribute('aria-describedby')).toBe('editor-assertion-error');
    await field.setInputValue('   ');
    await field.dispatchEvent('input');
    expect(await field.getAttribute('aria-invalid')).toBe('true');
    await field.setInputValue('Check the order total');
    await field.dispatchEvent('input');
    expect(await field.getAttribute('aria-invalid')).toBeNull();
    expect(await field.getAttribute('aria-describedby')).toBeNull();
    expect(await panel.text()).toContain('Unsaved changes · Minh Trần');
  });
  it('keeps fields selectable and read-only on narrow screens', async () => {
    const panel = await render('default', false);
    expect(await (await panel.find('#editor-action')).getProperty('readOnly')).toBe(true);
  });
});
