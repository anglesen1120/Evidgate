import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { NotFoundPage } from './not-found.page';

describe('NotFoundPage', () => {
  it('uses exactly the same copy for missing routes, foreign orgs and unknown projects', async () => {
    await TestBed.configureTestingModule({
      imports: [NotFoundPage],
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([{ path: '**', component: NotFoundPage }]),
      ],
    }).compileComponents();
    const texts: string[] = [];
    for (const url of [
      '/missing',
      '/orgs/foreign/settings',
      '/orgs/org-one/projects/unknown/overview',
    ]) {
      await TestBed.inject(Router).navigateByUrl(url);
      const fixture = TestBed.createComponent(NotFoundPage);
      await fixture.whenStable();
      const root = fixture.nativeElement as HTMLElement;
      expect(root.querySelectorAll('h1')).toHaveLength(1);
      expect(root.querySelector('h1')?.textContent).toBe('Not found');
      expect(root.querySelector('h1')?.classList.contains('sr-only')).toBe(true);
      expect(root.querySelector('h1')?.hasAttribute('aria-hidden')).toBe(false);
      expect(root.querySelectorAll('app-not-found-pattern')).toHaveLength(1);
      expect(root.querySelector('app-not-found-pattern h2')?.textContent).toBe('Not found');
      expect(root.querySelector('app-not-found-pattern p')?.textContent?.trim()).toBe(
        'This page does not exist or you do not have access to it.',
      );
      expect(root.querySelector('a')).toBeNull();
      expect(root.textContent).not.toMatch(/foreign|unknown|another|permission|organization/i);
      texts.push(root.textContent ?? '');
      fixture.destroy();
    }
    expect(new Set(texts).size).toBe(1);
    expect(texts[0]).toContain('Not found');
  });
});
