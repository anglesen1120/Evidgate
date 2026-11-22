import { TestBed } from '@angular/core/testing';
import { mergeReviewView } from './studio-review-merge.fixture';
import { ReviewPatchComponent } from './review-patch.component';

describe('ReviewPatchComponent', () => {
  function render(state: string) {
    const fixture = TestBed.createComponent(ReviewPatchComponent);
    fixture.componentRef.setInput('view', mergeReviewView(state));
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('keeps the existing assertions and marks the additional step in the draft only', () => {
    const panes = render('default').querySelectorAll('[data-patch-pane]');
    expect(panes).toHaveLength(2);
    for (const pane of panes) {
      expect(pane.textContent).toContain('toBeVisible');
      expect(pane.textContent).toContain('toContainText(orderTotal)');
    }
    expect(panes[0]?.textContent).not.toContain('orderId');
    expect(panes[1]?.querySelector('ins')?.textContent).toContain('orderId');
  });

  it.each(['nodiff', 'unchanged', 'reloaded', 'conflict'])(
    'does not invent a diff for %s',
    (state) => {
      expect(render(state).querySelector('[data-patch-pane]')).toBeNull();
    },
  );

  it('opens the full source for the long specimen', () => {
    expect(render('long').querySelector('details')?.open).toBe(true);
  });

  it('treats code as text, not markup', () => {
    const view = mergeReviewView('default');
    const fixture = TestBed.createComponent(ReviewPatchComponent);
    fixture.componentRef.setInput('view', { ...view, fullPatch: '<img src=x onerror=alert(1)>' });
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('pre')?.textContent).toContain('<img');
    expect(root.querySelector('img')).toBeNull();
  });
});
