import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { SignInRequiredPage } from './sign-in-required.page';

describe('SignInRequiredPage', () => {
  it('has its own main landmark, one heading and one home link without a fake login form', async () => {
    await TestBed.configureTestingModule({
      imports: [SignInRequiredPage],
      providers: [provideZonelessChangeDetection(), provideRouter([])],
    }).compileComponents();
    const fixture = TestBed.createComponent(SignInRequiredPage);
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelectorAll('main')).toHaveLength(1);
    expect(root.querySelectorAll('h1')).toHaveLength(1);
    expect(root.querySelector('h1')?.textContent).toBe('Sign in required');
    expect(root.querySelectorAll('a')).toHaveLength(1);
    expect(root.querySelector('a')?.getAttribute('href')).toBe('/');
    expect(root.querySelector('form')).toBeNull();
  });
});
