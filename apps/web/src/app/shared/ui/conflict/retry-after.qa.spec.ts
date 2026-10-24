// State sheet: docs/design/web/state-sheet.md#429-retry-after-indicator
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { RetryAfterComponent } from './retry-after.component';

const MISSING_INTERVAL = 'did not say how long to wait';

describe('RetryAfterComponent QA boundaries', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [RetryAfterComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
  });

  afterEach(() => {
    TestBed.resetTestingModule();
  });

  function render(seconds: number | null) {
    const fixture = TestBed.createComponent(RetryAfterComponent);
    fixture.componentRef.setInput('retryAfterSeconds', seconds);
    fixture.componentRef.setInput('retryEligible', true);
    fixture.componentRef.setInput('code', 'rate_limited');
    fixture.componentRef.setInput('requestId', 'request-429');
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('shows the supplied interval for a positive value and never the missing copy', () => {
    const root = render(30);
    expect(root.textContent).toContain('30');
    expect(root.textContent).not.toContain(MISSING_INTERVAL);
  });

  it('shows the supplied zero interval instead of the missing-interval copy', () => {
    const root = render(0);
    expect(root.textContent).toContain('Retry after: 0 s');
    expect(root.textContent).not.toContain(MISSING_INTERVAL);
  });
});
