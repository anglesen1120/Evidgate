import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { SignInRequiredPage } from './sign-in-required.page';

describe('SignInRequiredPage', () => {
  it('explains the block with a heading and no action or fake login form', async () => {
    await TestBed.configureTestingModule({
      imports: [SignInRequiredPage],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(SignInRequiredPage);
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelectorAll('main')).toHaveLength(1);
    expect(root.querySelectorAll('h1')).toHaveLength(1);
    expect(root.querySelector('h1')?.textContent).toBe('Sign in required');
    expect(root.querySelector('p')?.textContent).toBe(
      'Sign-in is not available in this workspace yet.',
    );
    // No link: the visitor is anonymous and every route this app can send them to ends back here
    // (review D2 — an action that loops is worse than none).
    expect(root.querySelectorAll('a')).toHaveLength(0);
    expect(root.querySelector('form')).toBeNull();
  });
});
