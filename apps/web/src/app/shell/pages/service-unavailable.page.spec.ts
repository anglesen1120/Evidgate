import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { SessionStore } from '../../core/session/session.store';
import { ServiceUnavailablePage } from './service-unavailable.page';

describe('ServiceUnavailablePage', () => {
  const session = { reload: vi.fn<() => Promise<void>>() };
  beforeEach(() => {
    session.reload.mockReset().mockResolvedValue(undefined);
    TestBed.configureTestingModule({
      imports: [ServiceUnavailablePage],
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        { provide: SessionStore, useValue: session },
      ],
    });
  });

  it('has exactly one heading and retries bootstrap before navigating home', async () => {
    const fixture = TestBed.createComponent(ServiceUnavailablePage);
    await fixture.whenStable();
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelectorAll('h1')).toHaveLength(1);
    expect(root.querySelectorAll('button')).toHaveLength(1);
    expect(root.querySelector('section')?.getAttribute('data-slot')).toBe('card');
    expect(root.querySelector('section')?.getAttribute('aria-labelledby')).toBe(
      root.querySelector('h1')?.id,
    );
    expect(root.querySelector('button')?.hasAttribute('hlmBtn')).toBe(true);
    await fixture.componentInstance.retry();
    expect(session.reload).toHaveBeenCalledOnce();
    expect(navigate).toHaveBeenCalledWith('/');
    expect(session.reload.mock.invocationCallOrder[0]!).toBeLessThan(
      navigate.mock.invocationCallOrder[0]!,
    );
  });

  it('disables repeated retries while loading and recovers to a neutral failure status', async () => {
    // The app's ES2022 lib does not expose Promise.withResolvers.
    let rejectReload!: (error: Error) => void;
    session.reload.mockReturnValue(
      new Promise<void>((_resolve, reject) => {
        rejectReload = reject;
      }),
    );
    const fixture = TestBed.createComponent(ServiceUnavailablePage);
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
    const retry = fixture.componentInstance.retry();
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('button')?.disabled).toBe(true);
    expect(root.querySelector('section')?.getAttribute('aria-busy')).toBe('true');
    await fixture.componentInstance.retry();
    expect(session.reload).toHaveBeenCalledOnce();
    rejectReload(new Error('unavailable'));
    await retry;
    await fixture.whenStable();
    expect(navigate).not.toHaveBeenCalled();
    expect(root.querySelector('button')?.disabled).toBe(false);
    expect(root.querySelector('[role="status"]')?.textContent).toContain('still unavailable');
    expect(root.querySelector('section')?.getAttribute('aria-busy')).toBe('false');
  });
});
