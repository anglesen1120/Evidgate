import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { HlmIcon } from './index';

const ACTIVITY =
  'M22 12h-2.48a2 2 0 0 0-1.93 1.46l-2.35 8.36a.25.25 0 0 1-.48 0L9.24 2.18a.25.25 0 0 0-.48 0l-2.35 8.36A2 2 0 0 1 4.49 12H2';

describe('HlmIcon fixed status geometry', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [HlmIcon],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
  });

  it.each([
    ['lucideClock', 'M12 6v6l4 2', true],
    ['lucideActivity', ACTIVITY, false],
    ['lucideCircle', null, true],
  ] as const)(
    'renders pinned Lucide geometry for %s without an SVG-string sink',
    (name, path, circle) => {
      const fixture = TestBed.createComponent(HlmIcon);
      fixture.componentRef.setInput('name', name);
      fixture.detectChanges();
      const root = fixture.nativeElement as HTMLElement;
      const svg = root.querySelector('svg');
      expect(root.getAttribute('aria-hidden')).toBe('true');
      expect(svg?.getAttribute('viewBox')).toBe('0 0 24 24');
      expect(svg?.getAttribute('stroke')).toBe('currentColor');
      expect(svg?.getAttribute('focusable')).toBe('false');
      expect(root.querySelector('path')?.getAttribute('d') ?? null).toBe(path);
      expect(Boolean(root.querySelector('circle'))).toBe(circle);
      expect(root.querySelector('script, foreignObject, animate, [style]')).toBeNull();
      expect(root.classList.contains('shrink-0')).toBe(true);
    },
  );
});
