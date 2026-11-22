import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { BehaviorSubject } from 'rxjs';
import { EditorHarness } from './editor.testing';
import { StudioDraftEditorReviewPage } from './studio-draft-editor-review.page';

const scenes = [
  ['default', 'Saved · supplied fixture'],
  ['dirty', 'Unsaved changes · Minh Trần'],
  ['busy', 'Saving…'],
  ['saving', 'Saving…'],
  ['saved', 'Saved · supplied fixture'],
  ['loading', 'Loading the supplied fixture…'],
  ['empty', 'No historical draft resource'],
  ['401', 'Session expired'],
  ['error', 'req-114'],
  ['403', 'Not authorized to make changes'],
  ['404', 'Not found'],
  ['429', 'Retry-After: 30 s'],
  ['412', 'current dr-5 / cr-10'],
  ['invalid', 'Enter an assertion before saving or running.'],
  ['readonly', 'Read-only revision'],
  ['merged', 'Merged · immutable'],
  ['published', 'Published · immutable'],
  ['running', 'Sandbox running. No new result yet.'],
  ['failed', 'Dry run failed.'],
  ['passed', 'Release eligibility is unchanged.'],
  ['aiempty', 'No suggestion for this revision.'],
  ['aiout', 'AI unavailable: no server suggestion returned.'],
  ['unknown', 'Saved · supplied fixture'],
] as const;

async function render(state?: string) {
  const params = new BehaviorSubject(convertToParamMap(state ? { state } : {}));
  TestBed.configureTestingModule({
    imports: [StudioDraftEditorReviewPage],
    providers: [
      provideRouter([]),
      { provide: ActivatedRoute, useValue: { queryParamMap: params } },
    ],
  });
  const fixture = TestBed.createComponent(StudioDraftEditorReviewPage);
  const page = await TestbedHarnessEnvironment.harnessForFixture(fixture, EditorHarness);
  return { page, params };
}

describe('StudioDraftEditorReviewPage', () => {
  it.each(scenes)('renders %s as a review specimen', async (state, message) => {
    const { page } = await render(state);
    expect(await page.text()).toContain(message);
    if (['loading', 'error', '401', '404', 'empty'].includes(state)) {
      expect(await page.text()).not.toContain('case-checkout-17');
    }
  });

  it('keeps definition, validation, sandbox and history in one workspace', async () => {
    const { page } = await render();
    for (const region of [
      'app-editor-definition',
      'app-editor-validation',
      'app-editor-sandbox',
      'app-editor-versions',
    ]) {
      expect(await page.all(region)).toHaveLength(1);
    }
    const counts = await page.all('[data-editor-metrics] dd');
    expect(await Promise.all(counts.map((value) => value.text()))).toEqual(['2', '3', '4']);
    expect(await page.all('[role="tablist"], [role="dialog"]')).toHaveLength(0);
  });

  it('reacts to a query change without remounting the page and never enables workflow actions', async () => {
    const { page, params } = await render();
    for (const id of ['save', 'discard', 'submit']) {
      expect(await (await page.find('#editor-' + id)).getProperty('disabled')).toBe(true);
    }
    expect(await page.text()).toContain('Preview only');
    params.next(convertToParamMap({ state: '404' }));
    expect(await page.text()).not.toContain('Draft identity');
  });
});
