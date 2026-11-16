import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';

import { LoadingPatternComponent } from './loading-pattern.component';
import { RequestStateComponent } from './request-state.component';

describe('RequestStateComponent loading label', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [RequestStateComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
  });

  it.each(['Loading members…', 'Đang tải nhật ký kiểm toán…'])(
    'passes the contextual label %s to its one loading status',
    (label) => {
      const fixture = TestBed.createComponent(RequestStateComponent);
      fixture.componentRef.setInput('loadingLabel', label);
      fixture.detectChanges();
      const child = fixture.debugElement
        .query(By.directive(LoadingPatternComponent))
        .injector.get(LoadingPatternComponent);
      expect(child.label()).toBe(label);
      const root = fixture.nativeElement as HTMLElement;
      expect(root.querySelector('h2')?.textContent?.trim()).toBe(label);
      expect(root.querySelectorAll('[role="status"]')).toHaveLength(1);
      expect(fixture.componentInstance.status()).toBe('loading');
    },
  );

  it.each([undefined, ''])('keeps a context-neutral fallback for an absent %s label', (label) => {
    const fixture = TestBed.createComponent(RequestStateComponent);
    fixture.componentRef.setInput('loadingLabel', label);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('h2')?.textContent?.trim()).toBe('Loading…');
    expect(root.textContent).not.toContain('evidence');
    expect(root.querySelector('button')).toBeNull();
    expect(fixture.componentInstance.status()).toBe('loading');
  });
});
