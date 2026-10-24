import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { ForbiddenPage } from './forbidden.page';

describe('ForbiddenPage', () => {
  it('has one heading and one home link; never echoes the attempted URL', async () => {
    await TestBed.configureTestingModule({
      imports: [ForbiddenPage],
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([{ path: '**', component: ForbiddenPage }]),
      ],
    }).compileComponents();
    await TestBed.inject(Router).navigateByUrl('/private-value?secret=not-for-display');
    const fixture = TestBed.createComponent(ForbiddenPage);
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelectorAll('h1')).toHaveLength(1);
    expect(root.querySelector('h1')?.textContent).toBe('You do not have access to this page');
    expect(root.querySelectorAll('a')).toHaveLength(1);
    expect(root.querySelector('a')?.getAttribute('href')).toBe('/');
    expect(root.textContent).not.toContain('private-value');
    expect(root.textContent).not.toContain('not-for-display');
  });
});
