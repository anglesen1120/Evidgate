import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ShellIconComponent } from './shell-icon.component';

describe('ShellIconComponent', () => {
  it('keeps static navigation geometry decorative and outside keyboard order', async () => {
    TestBed.configureTestingModule({
      imports: [ShellIconComponent],
      providers: [provideZonelessChangeDetection()],
    });
    const fixture = TestBed.createComponent(ShellIconComponent);
    fixture.componentRef.setInput('name', 'shield-check');
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    const svg = root.querySelector('svg');
    expect(svg?.getAttribute('aria-hidden')).toBe('true');
    expect(svg?.getAttribute('focusable')).toBe('false');
    expect(svg?.hasAttribute('tabindex')).toBe(false);
    expect(svg?.querySelector('path')).not.toBeNull();
    fixture.componentRef.setInput('name', 'moon');
    await fixture.whenStable();
    expect(root.querySelectorAll('svg')).toHaveLength(1);
    expect(root.querySelector('svg path')?.getAttribute('d')).toContain('M20 14.5');
  });
});
