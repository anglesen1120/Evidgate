import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { ForbiddenPage } from './forbidden.page';

describe('ForbiddenPage', () => {
  it('composes the shared pattern with one page heading; never echoes the attempted URL', async () => {
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
    expect(root.querySelector('h1')?.textContent).toBe('Not authorized');
    expect(root.querySelector('h1')?.classList.contains('sr-only')).toBe(true);
    expect(root.querySelector('h1')?.hasAttribute('aria-hidden')).toBe(false);
    expect(root.querySelectorAll('app-forbidden-pattern')).toHaveLength(1);
    expect(root.querySelector('app-forbidden-pattern h2')?.textContent).toBe('Not authorized');
    expect(root.querySelector('app-forbidden-pattern p')?.textContent?.trim()).toBe(
      'Ask a project administrator for access to this workspace.',
    );
    expect(root.querySelector('a')).toBeNull();
    expect(root.textContent).not.toContain('private-value');
    expect(root.textContent).not.toContain('not-for-display');
  });
});
