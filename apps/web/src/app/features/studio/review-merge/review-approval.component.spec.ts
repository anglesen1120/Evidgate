import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { mergeReviewView } from './studio-review-merge.fixture';
import { ReviewApprovalComponent } from './review-approval.component';

describe('ReviewApprovalComponent', () => {
  function render(state: string) {
    TestBed.configureTestingModule({ providers: [provideRouter([])] });
    const fixture = TestBed.createComponent(ReviewApprovalComponent);
    fixture.componentRef.setInput('view', mergeReviewView(state));
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('shows assignment without granting approval; both badges have a text label and icon', () => {
    const root = render('default');
    expect(root.textContent).toContain('Assigned reviewer');
    expect(root.querySelector('.status-pill--info')?.textContent).toContain('Validated');
    expect(root.querySelector('.status-pill--neutral')?.textContent).toContain('Awaiting review');
    expect(root.querySelectorAll('app-status-pill hlm-icon')).toHaveLength(2);
    expect(root.querySelector('[data-recorded-decision]')).toBeNull();
  });

  it.each(['approved', 'eligible', 'merged', 'candidate-approved'])(
    'distinguishes validation from the %s review decision',
    (state) => {
      const root = render(state);
      expect(root.querySelector('.status-pill--info')?.textContent).toContain('Validated');
      expect(root.querySelector('.status-pill--success')?.textContent).toContain('Approved');
      const decision = root.querySelector('[data-recorded-decision]');
      expect(decision?.textContent).toContain('Minh Trần');
      expect(decision?.textContent).toContain(
        state === 'candidate-approved' ? 'ch-8af2…3e20' : 'ah-1f9b…473a',
      );
      if (state === 'candidate-approved') {
        expect(decision?.querySelector('time')?.dateTime).toBe('2026-10-13T10:00:00Z');
      } else {
        expect(decision?.textContent).toContain('Decision time unavailable');
      }
    },
  );

  it.each(['failed', '412', 'conflict', 'reloaded'])(
    'does not display stale validation for %s',
    (state) => {
      const root = render(state);
      expect(root.querySelector('.status-pill--info')).toBeNull();
      expect(root.querySelector('[data-recorded-decision]')).toBeNull();
    },
  );
});
