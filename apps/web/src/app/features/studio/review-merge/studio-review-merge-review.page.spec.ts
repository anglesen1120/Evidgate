import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { BehaviorSubject } from 'rxjs';
import { HeaderHarness } from '../../../shared/ui/page-header/header.harness';
import { StudioReviewMergeReviewPage } from './studio-review-merge-review.page';

const examples = [
  ['default', 'Assertion patch'],
  ['loading', 'Loading the supplied fixture'],
  ['empty', 'No resource in this fixture'],
  ['error', 'req-114'],
  ['403', 'Not authorized to make changes'],
  ['404', 'Unknown and out-of-scope'],
  ['401', 'Session expired'],
  ['412', 'dr-6 / cr-10'],
  ['429', 'Retry-After: 30 s'],
  ['nodiff', 'No patch is attached'],
  ['unchanged', 'No changed assertion hunk'],
  ['long', 'Inspect full patch'],
  ['failed', 'dry_failed'],
  ['requested', 'no PR opened'],
  ['open', 'Open · waiting for review'],
  ['approved', 'Open · independent approval recorded'],
  ['eligible', 'merge eligibility'],
  ['withdrawn', 'back to draft'],
  ['closed', 'Closed without merge'],
  ['merged', 'Catalog sync queued'],
  ['prout', 'PR unavailable'],
  ['conflict', 'ch-91d4…63fc'],
  ['awaiting-review', 'ch-8af2…3e20'],
  ['candidate-approved', '2026-10-13T10:00:00Z'],
  ['reloaded', 'replacement patch was not supplied'],
] as const;

describe('StudioReviewMergeReviewPage', () => {
  function render(state?: string) {
    const params = new BehaviorSubject(convertToParamMap({ state }));
    TestBed.configureTestingModule({
      imports: [StudioReviewMergeReviewPage],
      providers: [
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { queryParamMap: params } },
      ],
    });
    const fixture = TestBed.createComponent(StudioReviewMergeReviewPage);
    fixture.detectChanges();
    return { fixture, root: fixture.nativeElement as HTMLElement, params };
  }

  it.each(examples)('renders the %s specimen', (state, expected) => {
    const { root } = render(state);
    expect(root.textContent).toContain(expected);
    if (['401', '404', 'loading', 'empty', 'error'].includes(state)) {
      expect(root.textContent).not.toContain('d-114');
    }
  });

  it('falls back to the historical draft and responds to a new query without remounting', async () => {
    const { fixture, root, params } = render('unrecognized');
    const header = await TestbedHarnessEnvironment.loader(fixture).getHarness(HeaderHarness);
    expect(await header.getTitle()).toBe('Review draft');
    expect(root.textContent).toContain('d-114');
    params.next(convertToParamMap({ state: 'conflict' }));
    fixture.detectChanges();
    expect(root.textContent).toContain('ch-91d4…63fc');
    expect(root.textContent).not.toContain('d-114');
    expect(root.textContent).not.toContain('24/24');
    expect(root.querySelector('.status-pill--success')).toBeNull();
  });

  it('preserves original failure and separates evidence, outcome and release in the merged fixture', () => {
    const { root } = render('merged');
    for (const fact of [
      'Original sandbox failure',
      'ev-s1',
      'Evidence completeness',
      'dry_passed',
      'Not evaluated',
    ]) {
      expect(root.textContent).toContain(fact);
    }
  });
});
