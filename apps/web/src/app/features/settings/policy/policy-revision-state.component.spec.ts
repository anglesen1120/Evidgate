import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { StatusPillComponent } from '../../../shared/ui/chips/status-pill.component';
import { PolicyRevisionStateComponent } from './policy-revision-state.component';

describe('PolicyRevisionStateComponent', () => {
  afterEach(() => TestBed.resetTestingModule());
  it.each([
    ['active', 'Active', 'info'],
    ['retired', 'Retired', 'neutral'],
    ['<future-state>', 'Unknown state', 'neutral'],
  ])('localizes %s without inferring a successful evaluation', (state, label, tone) => {
    const fixture = TestBed.createComponent(PolicyRevisionStateComponent);
    fixture.componentRef.setInput('state', state);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    const pill = fixture.debugElement.query(By.directive(StatusPillComponent))
      .componentInstance as StatusPillComponent;
    expect(pill.label()).toBe(label);
    expect(pill.tone()).toBe(tone);
    expect(root.querySelector('app-status-pill')?.textContent).toContain(label);
    if (state.startsWith('<')) {
      expect(root.querySelector('code')?.textContent).toContain(state);
      expect(root.querySelector('future-state')).toBeNull();
    }
  });
});
