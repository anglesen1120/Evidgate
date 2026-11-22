import { TestBed } from '@angular/core/testing';
import { mergeReviewView } from './studio-review-merge.fixture';
import { ReviewHistoryComponent } from './review-history.component';

describe('ReviewHistoryComponent', () => {
  function render(state: string) {
    const fixture = TestBed.createComponent(ReviewHistoryComponent);
    fixture.componentRef.setInput('view', mergeReviewView(state));
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it.each(['default', 'failed', 'merged'])(
    'retains the original sandbox failure for %s',
    (state) => {
      const root = render(state);
      expect(root.textContent).toContain('Original sandbox failure · attempt #1');
      expect(root.textContent).toContain('Sandbox artifact ev-s1 unavailable');
      expect(root.textContent).toContain(state === 'failed' ? 'dry_failed' : 'dry_passed');
      expect(root.textContent).toContain('Not evaluated');
      expect(root.textContent).not.toContain('PASS');
    },
  );

  it.each(['awaiting-review', 'candidate-approved', 'conflict'])(
    'pins the original CI decision to its frozen run for %s',
    (state) => {
      const root = render(state);
      for (const value of ['run-1842 / ev-3', 'Incomplete', 'has_failures', 'FAIL']) {
        expect(root.textContent).toContain(value);
      }
      expect(root.textContent).not.toContain('PASS');
      expect(root.textContent?.includes('24/24')).toBe(state !== 'conflict');
    },
  );
});
